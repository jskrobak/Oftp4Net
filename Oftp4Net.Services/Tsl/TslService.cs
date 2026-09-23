using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Oftp4Net.Services.Tsl;

/// <summary>State of the trust list shown in the settings.</summary>
public sealed record TslStatus
{
    public TrustServiceList? List { get; init; }

    /// <summary>Address the list in use comes from.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>When the list in use was downloaded (or read from the local copy).</summary>
    public DateTime? LoadedDate { get; init; }

    /// <summary>When a download was last attempted.</summary>
    public DateTime? CheckedDate { get; init; }

    /// <summary>Why the last download or check failed; the previous list stays in use.</summary>
    public string? Error { get; init; }

    public bool Outdated => List?.NextUpdate is { } next && next < DateTimeOffset.Now;
}

/// <summary>
/// Keeps the Odette Trust Service Status List (TSL, Odette OP08 2.8): downloads it regularly, verifies its signature
/// and keeps a local copy, so that the certification authorities are known also when the download fails. The list
/// has to be signed with the certificate pinned in the settings (taken over from the first list), and a list older
/// than the one in use (lower sequence number) is refused.
/// </summary>
public class TslService(
    GlobalSettingsService settingsService,
    IHttpClientFactory httpClientFactory,
    ILogger<TslService> logger) : BackgroundService
{
    public const string ProductionUrl = "https://www.odette.org/TSL/TSL_OFTP2.XML";
    public const string TestUrl = "https://www.odette.org/TSL/TSL_Test.xml";
    public const string HttpClientName = "tsl";

    private const int MaxSize = 10 * 1024 * 1024;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    /// <summary>A failed download is tried again after this time, not only after the refresh period.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private X509Certificate2Collection _anchors = [];
    private X509Certificate2Collection _verificationRoots = [];

    public TslStatus Status { get; private set; } = new();

    /// <summary>Raised when another list is used, e.g. to reload the trust anchors of the listeners.</summary>
    public event Action? Changed;

    /// <summary>Certification authorities of the list that are trusted; empty when the TSL is disabled.</summary>
    public X509Certificate2Collection TrustAnchors => _anchors;

    /// <summary>
    /// Certificates of <see cref="TrustAnchors"/> that are there to verify the authority above them and must not
    /// issue end entity certificates themselves (the roots of the listed OFTP2 authorities).
    /// </summary>
    public X509Certificate2Collection VerificationRoots => _verificationRoots;

    /// <summary>Downloads the list now.</summary>
    public async Task<TslStatus> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();
        await DownloadAsync(settings, cancellationToken);
        return Status;
    }

    /// <summary>Checks the list soon, e.g. after the settings changed.</summary>
    public void Trigger()
    {
        try
        {
            _trigger.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already triggered.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settingsService.SettingsChanged += Trigger;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Checking the trust list failed");
                }

                try
                {
                    await _trigger.WaitAsync(CheckInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            settingsService.SettingsChanged -= Trigger;
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();
        if (!settings.TslEnabled)
        {
            if (Status.List is not null || Status.SourceUrl is not null)
                Use(null, null, null, null);
            return;
        }

        // Another address: its local copy first, so that the authorities are known right away, also without the internet.
        if (Status.SourceUrl != settings.TslUrl)
        {
            var local = await LoadLocalCopyAsync(settings, cancellationToken);
            Use(local?.List, local?.Date, null, settings.TslUrl);
            await DownloadAsync(settings, cancellationToken);
            return;
        }

        var now = DateTime.Now;
        // An outdated list (past its next update) is looked for more often, but not on every check.
        var refreshDue = Status.LoadedDate is not { } loaded || loaded.AddHours(settings.TslRefreshHours) <= now ||
                         Status.Outdated && loaded.Add(RetryInterval) <= now;
        var retryAllowed = Status.Error is null || Status.CheckedDate is not { } checkedDate || checkedDate.Add(RetryInterval) <= now;
        if (refreshDue && retryAllowed)
            await DownloadAsync(settings, cancellationToken);
    }

    private async Task DownloadAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!settings.TslEnabled)
            {
                Use(null, null, "The trust list is disabled in the settings.", null);
                return;
            }

            byte[] content;
            try
            {
                content = await FetchAsync(settings.TslUrl, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                Fail($"The trust list cannot be downloaded from {settings.TslUrl}: {ex.Message}");
                return;
            }

            TrustServiceList list;
            try
            {
                list = TrustServiceList.Parse(content);
                await CheckSignerAsync(list, settings);
            }
            catch (TslException ex)
            {
                Fail(ex.Message);
                return;
            }

            if (Status.List is { } current && Status.SourceUrl == settings.TslUrl && SameSource(current, list) &&
                list.SequenceNumber < current.SequenceNumber)
            {
                Fail($"The downloaded trust list ({list.SequenceNumber}) is older than the one in use ({current.SequenceNumber}).");
                return;
            }

            await SaveLocalCopyAsync(settings.TslUrl, content, cancellationToken);
            var changed = Status.List?.SequenceNumber != list.SequenceNumber || Status.List?.SchemeName != list.SchemeName;
            Use(list, DateTime.Now, null, settings.TslUrl);

            if (changed)
                logger.LogInformation("Trust list {Name} {Sequence} of {Issued} loaded from {Url}: {Trusted} trusted certification authorities",
                    list.SchemeName, list.SequenceNumber, list.IssueDate, settings.TslUrl, list.Providers.Count(p => p.Trusted));
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<byte[]> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxSize)
            throw new InvalidDataException($"The list has {response.Content.Headers.ContentLength} bytes, at most {MaxSize} are accepted.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxSize)
                throw new InvalidDataException($"The list is larger than {MaxSize} bytes.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// The list is downloaded without a trusted channel (Odette publishes it over plain HTTP as well), so its signer
    /// is pinned: the first list pins its certificate, a later list has to be signed with the same one.
    /// </summary>
    private async Task CheckSignerAsync(TrustServiceList list, GlobalSettings settings)
    {
        if (string.IsNullOrEmpty(settings.TslSignerThumbprint))
        {
            var updated = await settingsService.GetGlobalSettingsAsync();
            updated.TslSignerThumbprint = list.SignerThumbprint;
            await settingsService.SetGlobalSettingsAsync(updated);
            logger.LogWarning("The trust list signer {Subject} ({Thumbprint}) is trusted from now on",
                list.Signer.Subject, list.SignerThumbprint);
            return;
        }

        if (!string.Equals(settings.TslSignerThumbprint, list.SignerThumbprint, StringComparison.OrdinalIgnoreCase))
            throw new TslException($"The trust list is signed by {list.Signer.Subject} ({list.SignerThumbprint}), not by the trusted " +
                                   $"signer ({settings.TslSignerThumbprint}). Check the new signer and clear the trusted signer in the settings to accept it.");
    }

    private void Use(TrustServiceList? list, DateTime? loadedDate, string? error, string? sourceUrl)
    {
        _anchors = list?.TrustAnchors ?? [];
        _verificationRoots = list?.VerificationRoots ?? [];
        Status = new TslStatus
        {
            List = list,
            SourceUrl = sourceUrl,
            LoadedDate = loadedDate,
            CheckedDate = list is null && error is null ? null : DateTime.Now,
            Error = error,
        };
        Changed?.Invoke();
    }

    private void Fail(string error)
    {
        logger.LogWarning("Trust list: {Error}", error);
        Status = Status with { CheckedDate = DateTime.Now, Error = error };
        Changed?.Invoke();
    }

    private static bool SameSource(TrustServiceList a, TrustServiceList b) => a.SchemeName == b.SchemeName;

    private string LocalCopyPath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..12];
        return Path.Combine(settingsService.ResolvePath("tsl"), $"tsl-{hash}.xml");
    }

    private async Task SaveLocalCopyAsync(string url, byte[] content, CancellationToken cancellationToken)
    {
        try
        {
            var path = LocalCopyPath(url);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "The trust list cannot be stored locally");
        }
    }

    private async Task<(TrustServiceList List, DateTime Date)?> LoadLocalCopyAsync(GlobalSettings settings, CancellationToken cancellationToken)
    {
        var path = LocalCopyPath(settings.TslUrl);
        if (!File.Exists(path))
            return null;

        try
        {
            var list = TrustServiceList.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
            if (!string.IsNullOrEmpty(settings.TslSignerThumbprint) &&
                !string.Equals(settings.TslSignerThumbprint, list.SignerThumbprint, StringComparison.OrdinalIgnoreCase))
                return null;

            return (list, File.GetLastWriteTime(path));
        }
        catch (Exception ex) when (ex is TslException or IOException)
        {
            logger.LogWarning(ex, "The local copy of the trust list {Path} cannot be used", path);
            return null;
        }
    }
}
