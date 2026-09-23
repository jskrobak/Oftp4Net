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

    /// <summary>
    /// The next records older than <paramref name="before"/> in the order of their time and id, following the
    /// record <paramref name="afterTimestamp"/> / <paramref name="afterId"/> (none: from the beginning).
    /// </summary>
    Task<List<TransferEvent>> GetNextOlderThanAsync(DateTime before, DateTime? afterTimestamp, int afterId, int take,
        CancellationToken cancellationToken = default);

    /// <summary>Removes the details and hook parameters of the records, their message stays.</summary>
    Task ClearDetailsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes up to <paramref name="take"/> records up to the record <paramref name="exportedTimestamp"/> /
    /// <paramref name="exportedId"/> in the order of their time and id (they are in the archive) that are
    /// informational and older than <paramref name="informationBefore"/>, or older than <paramref name="othersBefore"/>.
    /// Returns their count.
    /// </summary>
    Task<int> DeleteOldAsync(DateTime exportedTimestamp, int exportedId, DateTime informationBefore, DateTime othersBefore,
        int take, CancellationToken cancellationToken = default);
}
