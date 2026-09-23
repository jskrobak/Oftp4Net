using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Oftp4Net.Services.Security;

/// <summary>What the revocation lists say about a certificate.</summary>
public enum CrlResult
{
    /// <summary>The certificate is on no list of its issuer.</summary>
    Good,

    /// <summary>The issuer revoked the certificate; it must not be used any more.</summary>
    Revoked,

    /// <summary>
    /// The certificate names no revocation list, so there is nothing to read. Usual for a self signed certificate
    /// the administrator stored here.
    /// </summary>
    NoList,

    /// <summary>
    /// The list could not be read, or the one we have is older than the maximum period between updates. The
    /// certificate counts as unusable while that lasts (Odette OP08 2.6).
    /// </summary>
    Unknown,
}

public sealed record CrlVerdict(CrlResult Result, string? Problem = null);

/// <summary>
/// Downloads the certificate revocation lists of the certification authorities and keeps them (Odette OP08 2.6):
/// a list is fetched again after <see cref="GlobalSettings.CrlRefreshHours"/> or when the issuer announced a newer
/// one, and a certificate whose list is older than <see cref="GlobalSettings.CrlMaxAgeDays"/> is treated as
/// unusable until a current list has been read. Both periods are settings, as the specification requires, so that
/// they can be put down for the interoperability tests.
/// </summary>
public class CrlStore(IHttpClientFactory httpClientFactory, GlobalSettingsService settingsService, ILogger<CrlStore> logger)
{
    public const string HttpClientName = "crl";

    private const int MaxSize = 20 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Entry> _lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _download = new(4, 4);

    private sealed record Entry(CertificateRevocationList? List, DateTime Read, string? Error);

    /// <summary>
    /// What the lists of <paramref name="issuer"/> say about <paramref name="certificate"/>. The certificate has
    /// to be issued by it; a certificate that names no list is <see cref="CrlResult.NoList"/>.
    /// </summary>
    public async Task<CrlVerdict> CheckAsync(X509Certificate2 certificate, X509Certificate2 issuer,
        CancellationToken cancellationToken = default)
    {
        var urls = CertificateRevocationList.DistributionPoints(certificate);
        if (urls.Count == 0)
            return new CrlVerdict(CrlResult.NoList);

        var settings = await settingsService.GetGlobalSettingsAsync();
        var problems = new List<string>();

        foreach (var url in urls)
        {
            var entry = await GetAsync(url, issuer, settings, cancellationToken);
            if (entry.List is null)
            {
                problems.Add($"{url}: {entry.Error}");
                continue;
            }

            if (entry.List.Revokes(certificate))
                return new CrlVerdict(CrlResult.Revoked,
                    $"the list of {issuer.Subject} of {entry.List.ThisUpdate:g} revokes the certificate");

            // A list we could not refresh for too long says nothing about today.
            var age = DateTime.Now - entry.Read;
            if (age > TimeSpan.FromDays(settings.CrlMaxAgeDays))
            {
                problems.Add($"{url}: the list is {age.TotalDays:F0} days old, at most {settings.CrlMaxAgeDays} are accepted" +
                             (entry.Error is null ? "" : $" ({entry.Error})"));
                continue;
            }

            return new CrlVerdict(CrlResult.Good);
        }

        return new CrlVerdict(CrlResult.Unknown, string.Join("; ", problems));
    }

    /// <summary>Forgets everything that was read, e.g. after the periods were changed.</summary>
    public void Clear() => _lists.Clear();

    private async Task<Entry> GetAsync(string url, X509Certificate2 issuer, GlobalSettings settings,
        CancellationToken cancellationToken)
    {
        if (_lists.TryGetValue(url, out var known) && !NeedsRefresh(known, settings))
            return known;

        await _download.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have fetched it while we waited.
            if (_lists.TryGetValue(url, out known) && !NeedsRefresh(known, settings))
                return known;

            var entry = await DownloadAsync(url, issuer, known, cancellationToken);
            _lists[url] = entry;
            return entry;
        }
        finally
        {
            _download.Release();
        }
    }

    private static bool NeedsRefresh(Entry entry, GlobalSettings settings)
    {
        if (entry.List is null)
            // A failed attempt is repeated after a while, not on every file.
            return DateTime.Now - entry.Read > TimeSpan.FromMinutes(15);

        return DateTime.Now - entry.Read > TimeSpan.FromHours(settings.CrlRefreshHours) ||
               entry.List.NextUpdate is { } next && next < DateTimeOffset.Now;
    }

    private async Task<Entry> DownloadAsync(string url, X509Certificate2 issuer, Entry? previous,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaxSize)
                throw new InvalidDataException($"the list has {response.Content.Headers.ContentLength} octets");

            var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (content.Length > MaxSize)
                throw new InvalidDataException($"the list has {content.Length} octets");

            var list = CertificateRevocationList.Read(content, issuer);
            logger.LogInformation("Revocation list of {Issuer} read from {Url}: {Count} certificate(s), issued {Issued}, next {Next}",
                issuer.Subject, url, list.Count, list.ThisUpdate, list.NextUpdate);
            return new Entry(list, DateTime.Now, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or CrlException)
        {
            logger.LogWarning("The revocation list of {Issuer} cannot be read from {Url}: {Problem}",
                issuer.Subject, url, ex.Message);
            // The copy we have stays in use until it is too old; the error is remembered with it.
            return previous?.List is null
                ? new Entry(null, DateTime.Now, ex.Message)
                : previous with { Error = ex.Message };
        }
    }
}
