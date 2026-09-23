using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.DataLayer.Repositories;

namespace Oftp4Net.Services.Health;

/// <summary>
/// Files do not get stuck: none waits to be sent for longer than <see cref="WaitingLimit"/>, none failed for good
/// within <see cref="FailedWindow"/> and every received file had its End to End Response delivered within
/// <see cref="UnconfirmedLimit"/>.
/// </summary>
public sealed class SendQueueHealthCheck(IServiceScopeFactory serviceScopeFactory, ITimeService timeService) : IHealthCheck
{
    public static readonly TimeSpan WaitingLimit = TimeSpan.FromHours(24);
    public static readonly TimeSpan FailedWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan UnconfirmedLimit = TimeSpan.FromHours(1);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = timeService.GetCurrentTime();
        using var scope = serviceScopeFactory.CreateScope();
        var sendQueue = scope.ServiceProvider.GetRequiredService<ISendQueueItemRepository>();
        var receivedFiles = scope.ServiceProvider.GetRequiredService<IReceivedFileRepository>();

        return Evaluate(
            await sendQueue.CountWaitingAsync(now - WaitingLimit, cancellationToken),
            await sendQueue.CountFailedAsync(now - FailedWindow, cancellationToken),
            await receivedFiles.CountUnconfirmedAsync(now - UnconfirmedLimit, cancellationToken));
    }

    public static HealthCheckResult Evaluate(int waiting, int failed, int unconfirmed)
    {
        var data = new Dictionary<string, object>
        {
            ["waitingOver24Hours"] = waiting,
            ["failedLast24Hours"] = failed,
            ["unconfirmedOver1Hour"] = unconfirmed,
        };

        var problems = new List<string>();
        if (waiting > 0)
            problems.Add($"{waiting} file(s) have been waiting to be sent for more than {WaitingLimit.TotalHours:F0} hours.");
        if (failed > 0)
            problems.Add($"{failed} file(s) failed for good in the last {FailedWindow.TotalHours:F0} hours.");
        if (unconfirmed > 0)
            problems.Add($"{unconfirmed} received file(s) wait for more than {UnconfirmedLimit.TotalHours:F0} hour(s) for their End to End Response to reach the partner.");

        return problems.Count > 0
            ? HealthCheckResult.Degraded(string.Join(" ", problems), data: data)
            : HealthCheckResult.Healthy("No file is stuck.", data);
    }
}
