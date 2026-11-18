using Havit.Data.EntityFrameworkCore;
using Havit.Data.EntityFrameworkCore.Patterns.Caching;
using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Havit.Data.Patterns.DataLoaders;
using Havit.Data.Patterns.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public class CertificateAnnouncementLinkRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<CertificateAnnouncementLink, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<CertificateAnnouncementLink, int> repositoryQueryProvider)
    : DbRepository<CertificateAnnouncementLink, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager,
        repositoryQueryProvider), ICertificateAnnouncementLinkRepository
{
    public Task<List<CertificateAnnouncementLink>> GetByChangeAsync(int changeId, CancellationToken cancellationToken = default) => Data
        .Where(l => l.CertificateChangeId == changeId)
        .OrderBy(l => l.Id)
        .ToListAsync(cancellationToken);

    public Task<CertificateAnnouncementLink?> FindByTokenAsync(string token, CancellationToken cancellationToken = default) => Data
        .Where(l => l.Token == token)
        .Include(l => l.CertificateChange).ThenInclude(c => c!.Certificate)
        .FirstOrDefaultAsync(cancellationToken);
}
