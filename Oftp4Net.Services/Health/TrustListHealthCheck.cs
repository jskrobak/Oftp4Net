using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services.Health;

/// <summary>The Odette trust list is current: it was downloaded and its next update is not overdue.</summary>
public sealed class TrustListHealthCheck(TslService tslService, GlobalSettingsService settingsService) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();
        return Evaluate(settings.TslEnabled, tslService.Status);
    }

    public static HealthCheckResult Evaluate(bool enabled, TslStatus status)
    {
        if (!enabled)
            return HealthCheckResult.Healthy("The trust list is not used.");

        // Right after the start, before the first attempt.
        if (status.List is null && status.CheckedDate is null)
            return HealthCheckResult.Healthy("The trust list has not been read yet.");

        var data = new Dictionary<string, object>();
        if (status.SourceUrl is not null)
            data["source"] = status.SourceUrl;
        if (status.LoadedDate is { } loaded)
            data["loaded"] = loaded.ToString("s");
        if (status.List?.NextUpdate is { } nextUpdate)
            data["nextUpdate"] = nextUpdate.ToString("s");

        if (status.List is null)
            return HealthCheckResult.Degraded(
                $"No trust list is available, the Odette certification authorities are not trusted: {status.Error}", data: data);

        if (status.Outdated)
            return HealthCheckResult.Degraded(
                $"The trust list is outdated, it was to be updated on {status.List.NextUpdate:g}." +
                (status.Error is null ? "" : $" The download fails: {status.Error}"), data: data);

        if (status.Error is not null)
            return HealthCheckResult.Degraded(
                $"The trust list cannot be downloaded ({status.Error}), the one of {status.LoadedDate:g} stays in use.", data: data);

        return HealthCheckResult.Healthy($"The trust list of {status.LoadedDate:g} is in use.", data);
    }
}
