using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Health;

/// <summary>
/// Scheduled certificate changes of partners are applied on time: none is overdue by more than
/// <see cref="OverdueLimit"/> (the scheduler would be stuck), none failed within <see cref="FailedWindow"/>, and no
/// scheduled certificate expires before it is to be used.
/// </summary>
public sealed class CertificateChangesHealthCheck(IServiceScopeFactory serviceScopeFactory, ITimeService timeService) : IHealthCheck
{
    public static readonly TimeSpan OverdueLimit = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FailedWindow = TimeSpan.FromDays(7);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICertificateChangeRepository>();
        var scheduled = await repository.GetScheduledAsync(cancellationToken);
        var failed = (await repository.GetAllAsync(cancellationToken))
            .Where(c => c.Status == CertificateChangeStatus.Failed)
            .ToList();

        return Evaluate(scheduled, failed, timeService.GetCurrentTime());
    }

    public static HealthCheckResult Evaluate(IReadOnlyCollection<CertificateChange> scheduled, IReadOnlyCollection<CertificateChange> failed, DateTime now)
    {
        var data = new Dictionary<string, object> { ["scheduled"] = scheduled.Count };
        if (scheduled.Count > 0)
            data["next"] = scheduled.Min(c => c.ActivateAt).ToString("s");

        var problems = new List<string>();
        foreach (var change in scheduled.Where(c => now - c.ActivateAt > OverdueLimit))
            problems.Add($"The certificate change of partner {change.PartnerName} due at {change.ActivateAt:g} has not been applied.");
        foreach (var change in scheduled.Where(c => c.Certificate is { } certificate && certificate.ValidTo < c.ActivateAt))
            problems.Add($"The certificate scheduled for partner {change.PartnerName} at {change.ActivateAt:g} expires before, on {change.Certificate!.ValidTo:d}.");
        foreach (var change in failed.Where(c => now - c.ActivateAt < FailedWindow))
            problems.Add($"The certificate change of partner {change.PartnerName} at {change.ActivateAt:g} failed: {change.LastError}");

        return problems.Count > 0
            ? HealthCheckResult.Degraded(string.Join(" ", problems), data: data)
            : HealthCheckResult.Healthy(scheduled.Count == 0
                ? "No certificate change is scheduled."
                : $"{scheduled.Count} certificate change(s) scheduled, the next one at {scheduled.Min(c => c.ActivateAt):g}.", data);
    }
}
