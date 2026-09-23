using Havit.Services.TimeServices;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Health;

/// <summary>
/// The revocation lists of the certification authorities can be read. A list older than
/// <see cref="GlobalSettings.CrlMaxAgeDays"/> makes the certificates of its authority unusable.
/// </summary>
public sealed class RevocationListsHealthCheck(CrlStore crlStore, GlobalSettingsService settingsService,
    ITimeService timeService) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();
        return Evaluate(settings.CheckCertificateRevocation, crlStore.Lists, settings.CrlMaxAgeDays, timeService.GetCurrentTime());
    }

    public static HealthCheckResult Evaluate(bool check, IReadOnlyCollection<CrlListState> lists, int maxAgeDays, DateTime now)
    {
        if (!check)
            return HealthCheckResult.Healthy("Certificate revocation is not checked.");

        if (lists.Count == 0)
            return HealthCheckResult.Healthy("No revocation list has been needed yet.");

        var maxAge = TimeSpan.FromDays(maxAgeDays);
        var data = lists.ToDictionary(l => l.Url, l => (object)(
            !l.HasList ? $"not read: {l.Error}"
            : now - l.Read > maxAge ? $"read {l.Read:s}, too old" + (l.Error is null ? "" : $": {l.Error}")
            : $"read {l.Read:s}" + (l.Error is null ? "" : $", refresh fails: {l.Error}")));

        var problems = lists
            .Where(l => !l.HasList || now - l.Read > maxAge)
            .Select(l => l.HasList
                ? $"The revocation list {l.Url} is {(now - l.Read).TotalDays:F0} days old, at most {maxAgeDays} are accepted."
                : $"The revocation list {l.Url} cannot be read: {l.Error}")
            .ToList();

        if (problems.Count > 0)
            return HealthCheckResult.Degraded(
                string.Join(" ", problems) + " Certificates of these authorities are not used until a current list is read.",
                data: data);

        return HealthCheckResult.Healthy($"{lists.Count} revocation list(s) current.", data);
    }
}
