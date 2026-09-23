using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Oftp4Net.Services.Health;

/// <summary>
/// Every enabled listener is running. One that could not start (port taken, certificate missing) leaves partners
/// with nowhere to connect, although the rest of the application runs.
/// </summary>
public sealed class ListenersHealthCheck(ListenerService listenerService) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Evaluate(listenerService.Status));

    public static HealthCheckResult Evaluate(IReadOnlyCollection<ListenerStatus> listeners)
    {
        if (listeners.Count == 0)
            return HealthCheckResult.Healthy("No listener is enabled.");

        var data = listeners.ToDictionary(l => $"{l.Name} ({l.EndPoint})",
            l => (object)(l.Running ? "running" : $"stopped: {l.Error}"));

        var stopped = listeners.Where(l => !l.Running).ToList();
        if (stopped.Count > 0)
            return HealthCheckResult.Unhealthy(
                string.Join(" ", stopped.Select(l => $"Listener {l.Name} on {l.EndPoint} is not running: {l.Error}")), data: data);

        return HealthCheckResult.Healthy($"{listeners.Count} listener(s) running.", data);
    }
}
