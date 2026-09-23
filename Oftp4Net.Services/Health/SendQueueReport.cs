using Havit.Services.TimeServices;
using Microsoft.Extensions.DependencyInjection;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Health;

/// <summary>
/// The send queue in numbers, for monitoring (<c>/health/send-queue</c>): the thresholds belong to the monitoring,
/// so the report has no opinion, only counts and ages. Ages are whole minutes, <c>0</c> when there is nothing, so
/// that a monitoring item always gets a number.
/// </summary>
public sealed record SendQueueReport(
    int Waiting,
    int OldestWaitingMinutes,
    int Failed,
    int AwaitingEndResponse,
    int OldestAwaitingEndResponseMinutes,
    IReadOnlyList<SendQueuePartnerReport> Partners);

/// <summary>
/// The send queue of one partner. Every partner is listed, also with nothing in the queue, so that the items a
/// monitoring discovers per partner do not come and go with the files.
/// </summary>
public sealed record SendQueuePartnerReport(
    string Partner,
    string Ssid,
    int Waiting,
    int OldestWaitingMinutes,
    int Failed,
    int AwaitingEndResponse,
    int OldestAwaitingEndResponseMinutes,
    string? LastError,
    DateTime? LastErrorDate);

public sealed class SendQueueReportService(IServiceScopeFactory serviceScopeFactory, ITimeService timeService)
{
    public async Task<SendQueueReport> GetReportAsync(CancellationToken cancellationToken = default)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var partners = await scope.ServiceProvider.GetRequiredService<IPartnerRepository>().GetAllAsync(cancellationToken);
        var states = await scope.ServiceProvider.GetRequiredService<ISendQueueItemRepository>().GetStateByPartnerAsync(cancellationToken);

        return Build(partners, states, timeService.GetCurrentTime());
    }

    public static SendQueueReport Build(IEnumerable<Partner> partners, IReadOnlyCollection<SendQueuePartnerState> states, DateTime now)
    {
        var byPartner = states.ToDictionary(s => s.PartnerId);
        var reports = partners
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p =>
            {
                var state = byPartner.GetValueOrDefault(p.Id);
                return new SendQueuePartnerReport(p.Name, p.SSID,
                    state?.Waiting ?? 0, Minutes(state?.OldestWaiting, now),
                    state?.Failed ?? 0,
                    state?.AwaitingEndResponse ?? 0, Minutes(state?.OldestSent, now),
                    state?.LastError, state?.LastErrorDate);
            })
            .ToList();

        return new SendQueueReport(
            states.Sum(s => s.Waiting), Minutes(states.Min(s => s.OldestWaiting), now),
            states.Sum(s => s.Failed),
            states.Sum(s => s.AwaitingEndResponse), Minutes(states.Min(s => s.OldestSent), now),
            reports);
    }

    private static int Minutes(DateTime? since, DateTime now) =>
        since is { } value && value < now ? (int)(now - value).TotalMinutes : 0;
}
