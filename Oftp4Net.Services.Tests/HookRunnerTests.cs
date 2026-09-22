using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Oftp4Net.Services.Hooks;

namespace Oftp4Net.Services.Tests;

public sealed class HookRunnerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("oftp4net-hooks-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string CreateScript(string name, string body)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static HookRunner CreateRunner(Dictionary<string, string?>? configuration = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build(), NullLogger<HookRunner>.Instance);

    [Theory]
    [InlineData("virtualFileName", "OFTP_VIRTUAL_FILE_NAME")]
    [InlineData("event", "OFTP_EVENT")]
    [InlineData("partnerSsid", "OFTP_PARTNER_SSID")]
    public void EnvironmentNames(string name, string expected)
    {
        Assert.Equal(expected, HookRunner.ToEnvironmentName(name));
    }

    [Fact]
    public async Task ParametersArePassedAsEnvironmentAndJson()
    {
        if (OperatingSystem.IsWindows())
            return;

        var output = Path.Combine(_directory, "out");
        var script = CreateScript("hook.sh",
            $"echo \"$OFTP_VIRTUAL_FILE_NAME|$OFTP_FILE_PATH|$OFTP_EMPTY\" > '{output}.env'\ncat > '{output}.json'");

        var exitCode = await CreateRunner().RunAsync(HookEvent.OnReceived, script,
            new Dictionary<string, string?>
            {
                ["virtualFileName"] = "ORDERS.EDI",
                ["filePath"] = "/data/received/a b.edi",
                ["empty"] = null,
            }, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Equal("ORDERS.EDI|/data/received/a b.edi|", File.ReadAllText(output + ".env").Trim());
        var json = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(output + ".json"))!;
        Assert.Equal("ORDERS.EDI", json["virtualFileName"]);
        Assert.Null(json["empty"]);
    }

    [Fact]
    public async Task ExitCodeIsReturned()
    {
        if (OperatingSystem.IsWindows())
            return;

        var script = CreateScript("fail.sh", "echo broken >&2\nexit 3");

        var exitCode = await CreateRunner().RunAsync(HookEvent.OnSent, script, new Dictionary<string, string?>(),
            TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public async Task ScriptIsKilledAfterTimeout()
    {
        if (OperatingSystem.IsWindows())
            return;

        var script = CreateScript("slow.sh", "sleep 30");
        var started = DateTime.UtcNow;

        var exitCode = await CreateRunner().RunAsync(HookEvent.OnSent, script, new Dictionary<string, string?>(),
            TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.Null(exitCode);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DispatchedHooksRunInTheBackgroundInOrder()
    {
        if (OperatingSystem.IsWindows())
            return;

        var output = Path.Combine(_directory, "events");
        var script = CreateScript("log.sh", $"echo \"$OFTP_EVENT $OFTP_VIRTUAL_FILE_NAME\" >> '{output}'");
        var runner = CreateRunner(new Dictionary<string, string?>
        {
            ["Hooks:OnSent"] = script,
            ["Hooks:OnDelivered"] = script,
        });

        await runner.StartAsync(CancellationToken.None);
        runner.Dispatch(HookEvent.OnSent, new Dictionary<string, string?> { ["virtualFileName"] = "A" });
        runner.Dispatch(HookEvent.OnReceived, new Dictionary<string, string?> { ["virtualFileName"] = "not configured" });
        runner.Dispatch(HookEvent.OnDelivered, new Dictionary<string, string?> { ["virtualFileName"] = "A" });

        for (var i = 0; i < 100 && (!File.Exists(output) || File.ReadAllLines(output).Length < 2); i++)
            await Task.Delay(100);
        await runner.StopAsync(CancellationToken.None);

        Assert.Equal(["OnSent A", "OnDelivered A"], File.ReadAllLines(output));
    }
}
