using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.Patterns.Repositories;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface ISendQueueItemRepository: IRepository<SendQueueItem, int>
{
    Task<DataFragment<SendQueueItem>> GetFragmentAsync(SendQueueFilter filter, GridDataProviderRequest<SendQueueItem> request,
        CancellationToken cancellationToken = default);

    Task<List<SendQueueItem>> GetAllWithRefsAsync();
    Task<List<SendQueueItem>> GetAllToProcessAsync();

    /// <summary>
    /// Items of the partner waiting to be sent, optionally only those sent under the given identity.
    /// </summary>
    Task<List<SendQueueItem>> GetPendingForPartnerAsync(int partnerId, int? identityId,
        CancellationToken cancellationToken = default);

    /// <summary>Finds a sent item by the virtual file identification echoed in an End to End Response.</summary>
    Task<SendQueueItem?> FindSentAsync(string virtualFileName, string fileDate, string fileTime,
        CancellationToken cancellationToken = default);
}