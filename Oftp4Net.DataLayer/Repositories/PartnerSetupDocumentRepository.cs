using Havit.Data.EntityFrameworkCore;
using Havit.Data.EntityFrameworkCore.Patterns.Caching;
using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Havit.Data.Patterns.DataLoaders;
using Havit.Data.Patterns.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public class PartnerSetupDocumentRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<PartnerSetupDocument, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<PartnerSetupDocument, int> repositoryQueryProvider)
    : DbRepository<PartnerSetupDocument, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager,
        repositoryQueryProvider), IPartnerSetupDocumentRepository
{
    public async Task<PartnerSetupDocument?> FindByReceivedFileAsync(int receivedFileId, CancellationToken cancellationToken = default)
    {
        return await Data.FirstOrDefaultAsync(d => d.ReceivedFileId == receivedFileId, cancellationToken);
    }

    public async Task<List<PartnerSetupDocument>> GetDueScheduledAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        return await Data
            .Where(d => d.Status == PartnerSetupStatus.Scheduled && d.ValidFrom <= now)
            .OrderBy(d => d.ValidFrom)
            .ToListAsync(cancellationToken);
    }

    public async Task<DateTime?> GetNextScheduledAsync(CancellationToken cancellationToken = default)
    {
        return await Data
            .Where(d => d.Status == PartnerSetupStatus.Scheduled)
            .MinAsync(d => (DateTime?)d.ValidFrom, cancellationToken);
    }

    public async Task<List<PartnerSetupDocument>> GetOpenAsync(int? partnerId = null, CancellationToken cancellationToken = default)
    {
        return await Data
            .Include(d => d.Partner)
            .Where(d => d.Status == PartnerSetupStatus.PendingApproval || d.Status == PartnerSetupStatus.AwaitingEndResponse ||
                        d.Status == PartnerSetupStatus.Scheduled)
            .Where(d => partnerId == null || d.PartnerId == partnerId)
            .OrderBy(d => d.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<PartnerSetupDocument>> GetLatestAsync(int count, CancellationToken cancellationToken = default)
    {
        return await Data
            .Include(d => d.Partner)
            .Include(d => d.ReceivedFile)
            .OrderByDescending(d => d.Id)
            .Take(count)
            .ToListAsync(cancellationToken);
    }
}
