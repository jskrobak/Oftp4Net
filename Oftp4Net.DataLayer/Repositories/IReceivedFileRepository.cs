using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.Patterns.Repositories;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface IReceivedFileRepository: IRepository<ReceivedFile, int>
{
    Task<DataFragment<ReceivedFile>> GetFragmentAsync(ReceivedFileFilter filter, GridDataProviderRequest<ReceivedFile> request,
        CancellationToken cancellationToken = default);

    /// <summary>Received files of the partner whose End to End Response has not been delivered yet.</summary>
    Task<List<ReceivedFile>> GetUnconfirmedAsync(int partnerId, CancellationToken cancellationToken = default);

    /// <summary>Filtered page of received files, newest first (REST API).</summary>
    Task<DataFragment<ReceivedFile>> GetListAsync(ReceivedFileFilter filter, int skip, int take,
        CancellationToken cancellationToken = default);

    Task<ReceivedFile?> FindWithRefsAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Partners that have received files with an End to End Response still to be delivered.</summary>
    Task<List<ReceivedFile>> GetAllUnconfirmedAsync(DateTime createdBefore, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(int partnerId, string virtualFileName, string fileDate, string fileTime,
        CancellationToken cancellationToken = default);
}
