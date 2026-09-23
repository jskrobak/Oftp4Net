using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Oftp4Net.Services.Api;

namespace Oftp4Net.Services.Health;

/// <summary>
/// Receives the results of the health checks the application runs regularly (every 30 seconds), keeps the last
/// one for the dashboard and reports every change: in the log and, when <c>HealthChecks:WebhookUrl</c> is
/// configured, with the webhook <c>health.changed</c> (signed with <c>HealthChecks:WebhookSecret</c>).
/// </summary>
public sealed class HealthMonitor(IConfiguration configuration, IWebhookDispatcher webhooks, ILogger<HealthMonitor> logger)
    : IHealthCheckPublisher
{
    public const string WebhookEvent = "health.changed";

    private readonly Lock _lock = new();

    /// <summary>The last result; <c>null</c> until the checks ran for the first time.</summary>
    public HealthReport? Report { get; private set; }

    /// <summary>When <see cref="Report"/> was made.</summary>
    public DateTimeOffset? Checked { get; private set; }

    /// <summary>Raised after every result, changed or not.</summary>
    public event Action? Updated;

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        Update(report);
        return Task.CompletedTask;
    }

    /// <summary>Runs the checks now instead of waiting for the next period, e.g. from the dashboard.</summary>
    public async Task CheckNowAsync(HealthCheckService healthCheckService, CancellationToken cancellationToken = default) =>
        Update(await healthCheckService.CheckHealthAsync(cancellationToken));

    private void Update(HealthReport report)
    {
        List<Change> changes;
        lock (_lock)
        {
            changes = Changes(Report, report);
            Report = report;
            Checked = DateTimeOffset.Now;
        }

        foreach (var change in changes)
            Log(change);

        if (changes.Count > 0 && configuration["HealthChecks:WebhookUrl"] is { Length: > 0 } url)
        {
            webhooks.Dispatch(url, configuration["HealthChecks:WebhookSecret"], new WebhookPayload
            {
                Event = WebhookEvent,
                Status = report.Status.ToString(),
                Error = Problems(report),
            });
        }

        Updated?.Invoke();
    }

    public sealed record Change(string Name, HealthStatus? Previous, HealthStatus Current, string? Description);

    /// <summary>
    /// Checks whose state differs from the previous result. The first result reports only the checks that are not
    /// healthy, so that a normal start stays quiet.
    /// </summary>
    public static List<Change> Changes(HealthReport? previous, HealthReport current)
    {
        var changes = new List<Change>();
        foreach (var (name, entry) in current.Entries)
        {
            HealthStatus? before = previous?.Entries.TryGetValue(name, out var old) == true ? old.Status : null;
            if (before == entry.Status || before is null && entry.Status == HealthStatus.Healthy)
                continue;

            changes.Add(new Change(name, before, entry.Status, entry.Description ?? entry.Exception?.Message));
        }

        return changes;
    }

    /// <summary>The descriptions of the checks that are not healthy, for the webhook.</summary>
    public static string? Problems(HealthReport report)
    {
        var problems = report.Entries
            .Where(e => e.Value.Status != HealthStatus.Healthy)
            .Select(e => $"{e.Key}: {e.Value.Description ?? e.Value.Exception?.Message}")
            .ToList();
        return problems.Count == 0 ? null : string.Join("\n", problems);
    }

    private void Log(Change change)
    {
        switch (change.Current)
        {
            case HealthStatus.Healthy:
                logger.LogInformation("Health check {Check} is healthy again (was {Previous}): {Description}",
                    change.Name, change.Previous, change.Description);
                break;
            case HealthStatus.Degraded:
                logger.LogWarning("Health check {Check} is degraded: {Description}", change.Name, change.Description);
                break;
            default:
                logger.LogError("Health check {Check} is unhealthy: {Description}", change.Name, change.Description);
                break;
        }
    }
}
