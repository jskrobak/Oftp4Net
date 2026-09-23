using Havit.Services.TimeServices;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Oftp4Net.Services.Health;

/// <summary>
/// The send service runs and goes through the send queue regularly. It is <c>Degraded</c> while an administrator
/// has paused it, since files wait for as long.
/// </summary>
public sealed class SendServiceHealthCheck(SendService sendService, GlobalSettingsService settingsService,
    ITimeService timeService) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();
        return Evaluate(sendService.IsRunning, sendService.IsPaused, sendService.Started, sendService.LastRun,
            sendService.ActiveSessions, settings.SendIntervalSeconds, timeService.GetCurrentTime());
    }

    /// <summary>The queue has to be processed within three intervals and a minute, otherwise the loop is stuck.</summary>
    public static TimeSpan MaxDelay(int sendIntervalSeconds) => TimeSpan.FromSeconds(3 * sendIntervalSeconds + 60);

    public static HealthCheckResult Evaluate(bool running, bool paused, DateTime? started, DateTime? lastRun,
        int activeSessions, int sendIntervalSeconds, DateTime now)
    {
        var data = new Dictionary<string, object>
        {
            ["lastRun"] = lastRun?.ToString("s") ?? "never",
            ["activeSessions"] = activeSessions,
        };

        if (!running)
            return HealthCheckResult.Unhealthy("The send service is not running, no files are sent.", data: data);

        if (paused)
            return HealthCheckResult.Degraded("The send service is paused, no files are sent until it is resumed.", data: data);

        if ((lastRun ?? started) is { } reference && now - reference > MaxDelay(sendIntervalSeconds))
            return HealthCheckResult.Unhealthy(
                $"The send queue was last processed at {reference:g}, although it is to be processed every {sendIntervalSeconds} s.",
                data: data);

        return HealthCheckResult.Healthy($"The send service is running, {activeSessions} session(s) active.", data);
    }
}
