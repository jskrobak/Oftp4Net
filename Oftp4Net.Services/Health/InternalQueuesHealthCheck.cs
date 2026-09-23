using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.Services.Api;
using Oftp4Net.Services.Hooks;
using Oftp4Net.Services.TransferEvents;

namespace Oftp4Net.Services.Health;

/// <summary>
/// The in-memory queues of the transfer log, the webhooks and the hooks keep up. A full queue throws records,
/// webhook calls or hook runs away, so it is reported before that happens (at <see cref="WarningFill"/>).
/// </summary>
public sealed class InternalQueuesHealthCheck(TransferEventLog transferEventLog, WebhookDispatcher webhookDispatcher,
    HookRunner hookRunner) : IHealthCheck
{
    public const double WarningFill = 0.8;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate([transferEventLog.Queue, webhookDispatcher.Queue, hookRunner.Queue]));

    public static HealthCheckResult Evaluate(IReadOnlyCollection<QueueState> queues)
    {
        var data = queues.ToDictionary(q => q.Name, q => (object)(
            $"{q.Count} of {q.Capacity}" + (q.Dropped > 0 ? $", {q.Dropped} dropped since the start" : "")));

        var full = queues.Where(q => q.Count >= q.Capacity * WarningFill).ToList();
        if (full.Count > 0)
            return HealthCheckResult.Degraded(
                string.Join(" ", full.Select(q => $"The {q.Name.ToLowerInvariant()} queue holds {q.Count} of {q.Capacity} items and does not keep up.")),
                data: data);

        return HealthCheckResult.Healthy("The internal queues keep up.", data);
    }
}
