using Havit.Services.TimeServices;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.Services.Retention;

namespace Oftp4Net.Services.Health;

/// <summary>The nightly retention runs and succeeds, otherwise the database and the outbox keep growing.</summary>
public sealed class RetentionHealthCheck(RetentionService retentionService, GlobalSettingsService settingsService,
    ITimeService timeService) : IHealthCheck
{
    /// <summary>A run is due every day; two give it a night to spare.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(2);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();
        return Evaluate(settings.RetentionEnabled, retentionService.Started, retentionService.LastResult,
            retentionService.LastSuccess, timeService.GetCurrentTime());
    }

    public static HealthCheckResult Evaluate(bool enabled, DateTime? started, RetentionResult? lastResult, DateTime? lastSuccess, DateTime now)
    {
        if (!enabled)
            return HealthCheckResult.Healthy("Retention is switched off, nothing is removed.");

        var data = new Dictionary<string, object>();
        if (lastResult is not null)
        {
            data["lastRun"] = lastResult.Finished.ToString("s");
            data["eventsArchived"] = lastResult.EventsArchived;
            data["eventsDeleted"] = lastResult.EventsDeleted;
            data["outboxFilesDeleted"] = lastResult.OutboxFilesDeleted;
            data["sendQueueItemsDeleted"] = lastResult.SendQueueItemsDeleted;
            data["receivedFilesDeleted"] = lastResult.ReceivedFilesDeleted;
        }

        if (lastResult?.Error is { } error)
            return HealthCheckResult.Degraded($"The last retention run at {lastResult.Started:g} failed: {error}", data: data);

        if ((lastSuccess ?? started) is { } reference && now - reference > MaxAge)
            return HealthCheckResult.Degraded(
                lastSuccess is null
                    ? $"Retention has not run since the start at {reference:g}."
                    : $"Retention last succeeded at {reference:g}.", data: data);

        return lastResult is null
            ? HealthCheckResult.Healthy("Retention has not run yet, it starts a few minutes after the application.")
            : HealthCheckResult.Healthy($"Last run at {lastResult.Finished:g}: {lastResult}.", data);
    }
}
