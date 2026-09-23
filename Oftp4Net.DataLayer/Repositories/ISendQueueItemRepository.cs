using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.Patterns.Repositories;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

/// <summary>What waits in the send queue for one partner.</summary>
/// <param name="Waiting">Items new or to be retried.</param>
/// <param name="OldestWaiting">When the oldest of them was queued.</param>
/// <param name="Failed">Items that failed for good and wait for the administrator.</param>
/// <param name="AwaitingEndResponse">Items sent, whose End to End Response has not arrived yet.</param>
/// <param name="OldestSent">When the oldest of them was sent.</param>
/// <param name="LastError">The error of the item that failed last, with its time.</param>
public sealed record SendQueuePartnerState(int PartnerId, int Waiting, DateTime? OldestWaiting, int Failed,
    int AwaitingEndResponse, DateTime? OldestSent, string? LastError, DateTime? LastErrorDate);

public interface ISendQueueItemRepository: IRepository<SendQueueItem, int>
{
    Task<DataFragment<SendQueueItem>> GetFragmentAsync(SendQueueFilter filter, GridDataProviderRequest<SendQueueItem> request,
        CancellationToken cancellationToken = default);

    Task<List<SendQueueItem>> GetAllWithRefsAsync();
    Task<List<SendQueueItem>> GetAllToProcessAsync();

    /// <summary>Filtered page of queue items, newest first (REST API).</summary>
    Task<DataFragment<SendQueueItem>> GetListAsync(SendQueueFilter filter, int skip, int take,
        CancellationToken cancellationToken = default);

    Task<SendQueueItem?> FindWithRefsAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Items of the partner waiting to be sent, optionally only those sent under the given identity.
    /// </summary>
    Task<List<SendQueueItem>> GetPendingForPartnerAsync(int partnerId, int? identityId,
        CancellationToken cancellationToken = default);

    /// <summary>Finds a sent item by the virtual file identification echoed in an End to End Response.</summary>
    Task<SendQueueItem?> FindSentAsync(string virtualFileName, string fileDate, string fileTime,
        CancellationToken cancellationToken = default);

    /// <summary>Items still waiting to be sent (new or to be retried) that were queued before the given time.</summary>
    Task<int> CountWaitingAsync(DateTime createdBefore, CancellationToken cancellationToken = default);

    /// <summary>Items that failed for good since the given time.</summary>
    Task<int> CountFailedAsync(DateTime failedSince, CancellationToken cancellationToken = default);

    /// <summary>The state of the send queue of every partner that has items waiting, failed or not yet confirmed.</summary>
    Task<List<SendQueuePartnerState>> GetStateByPartnerAsync(CancellationToken cancellationToken = default);
}