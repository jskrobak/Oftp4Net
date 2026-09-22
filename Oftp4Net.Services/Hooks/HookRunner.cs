using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Oftp4Net.Services.Hooks;

/// <summary>Events that can run a user script (configuration section <c>Hooks</c>).</summary>
public enum HookEvent
{
    /// <summary>A file was received and stored (before the End to End Response is sent).</summary>
    OnReceived,

    /// <summary>Receiving a file failed (it is not stored).</summary>
    OnReceiveFailed,

    /// <summary>A file was transferred and the partner confirmed receipt (EFPA).</summary>
    OnSent,

    /// <summary>Sending a file failed; it may be retried (see <c>OFTP_WILL_RETRY</c>).</summary>
    OnSendFailed,

    /// <summary>The partner confirmed delivery to the final destination (EERP).</summary>
    OnDelivered,

    /// <summary>The partner reported that the file could not be delivered (NERP).</summary>
    OnNotDelivered,
}

public sealed class HookOptions
{
    /// <summary>Script or executable per event, e.g. <c>Hooks:OnReceived=/scripts/on_received.sh</c>.</summary>
    public string? OnReceived { get; set; }
    public string? OnReceiveFailed { get; set; }
    public string? OnSent { get; set; }
    public string? OnSendFailed { get; set; }
    public string? OnDelivered { get; set; }
    public string? OnNotDelivered { get; set; }

    /// <summary>A script running longer is killed.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    public string? GetCommand(HookEvent hookEvent) => hookEvent switch
    {
        HookEvent.OnReceived => OnReceived,
        HookEvent.OnReceiveFailed => OnReceiveFailed,
        HookEvent.OnSent => OnSent,
        HookEvent.OnSendFailed => OnSendFailed,
        HookEvent.OnDelivered => OnDelivered,
        HookEvent.OnNotDelivered => OnNotDelivered,
        _ => null
    };
}

public interface IHookDispatcher
{
    /// <summary>
    /// Queues the script configured for the event. Parameters are passed as environment variables
    /// (<c>OFTP_</c> + upper case name) and as a JSON object on standard input. Returns immediately.
    /// </summary>
    void Dispatch(HookEvent hookEvent, IReadOnlyDictionary<string, string?> parameters);
}

/// <summary>
/// Runs hook scripts one after another in the background, so a slow or failing script never delays or breaks
/// an OFTP session. Hooks still waiting when the application stops are not run.
/// </summary>
public sealed class HookRunner(IConfiguration configuration, ILogger<HookRunner> logger) : BackgroundService, IHookDispatcher
{
    private const int QueueCapacity = 1000;

    // Readable JSON for scripts (no \u escaping of '+' or diacritics); it is not embedded in HTML.
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Channel<(HookEvent Event, string Command, Dictionary<string, string?> Parameters)> _queue =
        Channel.CreateBounded<(HookEvent, string, Dictionary<string, string?>)>(
            new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    private HookOptions Options => configuration.GetSection("Hooks").Get<HookOptions>() ?? new HookOptions();

    /// <summary>Configured hooks, for display.</summary>
    public IReadOnlyDictionary<HookEvent, string> ConfiguredHooks
    {
        get
        {
            var options = Options;
            return Enum.GetValues<HookEvent>()
                .Where(e => !string.IsNullOrWhiteSpace(options.GetCommand(e)))
                .ToDictionary(e => e, e => options.GetCommand(e)!);
        }
    }

    public void Dispatch(HookEvent hookEvent, IReadOnlyDictionary<string, string?> parameters)
    {
        var command = Options.GetCommand(hookEvent);
        if (string.IsNullOrWhiteSpace(command))
            return;

        var all = new Dictionary<string, string?>(parameters)
        {
            ["event"] = hookEvent.ToString(),
            ["timestamp"] = DateTimeOffset.Now.ToString("O"),
        };

        if (!_queue.Writer.TryWrite((hookEvent, command, all)))
            logger.LogError("Hook {Event} was not run: the hook queue is full", hookEvent);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (hookEvent, command, parameters) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(hookEvent, command, parameters, TimeSpan.FromSeconds(Options.TimeoutSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Hook {Event} ({Command}) could not be started", hookEvent, command);
            }
        }
    }

    /// <summary>Runs the command and waits for it. Returns the exit code, or <c>null</c> when it was killed.</summary>
    public async Task<int?> RunAsync(HookEvent hookEvent, string command, IReadOnlyDictionary<string, string?> parameters,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var (name, value) in parameters)
            startInfo.Environment[ToEnvironmentName(name)] = value ?? "";

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"Process '{command}' did not start.");

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(parameters, JsonOptions));
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The script does not read its input.
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
                throw;

            logger.LogError("Hook {Event} ({Command}) was killed after {Timeout} seconds", hookEvent, command, timeout.TotalSeconds);
            return null;
        }

        var stdout = (await output).Trim();
        var stderr = (await error).Trim();

        if (process.ExitCode == 0)
            logger.LogInformation("Hook {Event} ({Command}) finished{Output}", hookEvent, command,
                stdout.Length > 0 ? ": " + stdout : "");
        else
            logger.LogError("Hook {Event} ({Command}) failed with exit code {ExitCode}: {Error}", hookEvent, command,
                process.ExitCode, stderr.Length > 0 ? stderr : stdout);

        return process.ExitCode;
    }

    /// <summary>"virtualFileName" -> "OFTP_VIRTUAL_FILE_NAME".</summary>
    public static string ToEnvironmentName(string name)
    {
        var chars = new List<char> { 'O', 'F', 'T', 'P', '_' };
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                chars.Add('_');
            chars.Add(char.ToUpperInvariant(name[i]));
        }

        return new string(chars.ToArray());
    }
}
