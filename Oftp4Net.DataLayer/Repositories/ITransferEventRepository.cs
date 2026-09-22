using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.Patterns.Repositories;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface ITransferEventRepository : IRepository<TransferEvent, int>
{
    Task<DataFragment<TransferEvent>> GetFragmentAsync(TransferEventFilter filter, GridDataProviderRequest<TransferEvent> request,
        CancellationToken cancellationToken = default);

    /// <summary>Filtered page of records, newest first (REST API).</summary>
    Task<DataFragment<TransferEvent>> GetListAsync(TransferEventFilter filter, int skip, int take,
        CancellationToken cancellationToken = default);

    /// <summary>Marks records older than <paramref name="cutoff"/> as archived, returns their count.</summary>
    Task<int> ArchiveOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default);
}
