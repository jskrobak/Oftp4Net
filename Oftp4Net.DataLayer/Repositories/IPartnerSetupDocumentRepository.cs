using Havit.Data.Patterns.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface IPartnerSetupDocumentRepository : IRepository<PartnerSetupDocument, int>
{
    /// <summary>The datasheet received in <paramref name="receivedFileId"/>, if the file was one.</summary>
    Task<PartnerSetupDocument?> FindByReceivedFileAsync(int receivedFileId, CancellationToken cancellationToken = default);

    /// <summary>Accepted datasheets whose time has come (<see cref="PartnerSetupDocument.ValidFrom"/>).</summary>
    Task<List<PartnerSetupDocument>> GetDueScheduledAsync(DateTime now, CancellationToken cancellationToken = default);

    /// <summary>When the next scheduled datasheet becomes valid, <c>null</c> when none is scheduled.</summary>
    Task<DateTime?> GetNextScheduledAsync(CancellationToken cancellationToken = default);

    /// <summary>Datasheets waiting for approval or for their time, of one partner or of all.</summary>
    Task<List<PartnerSetupDocument>> GetOpenAsync(int? partnerId = null, CancellationToken cancellationToken = default);

    /// <summary>The newest datasheets first, with their partner and received file.</summary>
    Task<List<PartnerSetupDocument>> GetLatestAsync(int count, CancellationToken cancellationToken = default);
}
