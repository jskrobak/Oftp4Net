using Havit.Data.EntityFrameworkCore;
using Havit.Data.EntityFrameworkCore.Patterns.Caching;
using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Havit.Data.Patterns.DataLoaders;
using Havit.Data.Patterns.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public class CertificateChangeRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<CertificateChange, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<CertificateChange, int> repositoryQueryProvider)
    : DbRepository<CertificateChange, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager,
        repositoryQueryProvider), ICertificateChangeRepository
{
    public Task<List<CertificateChange>> GetDueAsync(DateTime now, CancellationToken cancellationToken = default) => Data
        .Where(c => c.Status == CertificateChangeStatus.Scheduled && c.ActivateAt <= now)
        .Include(c => c.Partner)
        .Include(c => c.Certificate)
        .OrderBy(c => c.ActivateAt)
        .ThenBy(c => c.Id)
        .ToListAsync(cancellationToken);

    public Task<List<CertificateChange>> GetByPartnerAsync(int partnerId, CancellationToken cancellationToken = default) => Data
        .Where(c => c.PartnerId == partnerId)
        .Include(c => c.Certificate)
        .OrderByDescending(c => c.ActivateAt)
        .ToListAsync(cancellationToken);

    public Task<DateTime?> GetNextScheduledAsync(CancellationToken cancellationToken = default) => Data
        .Where(c => c.Status == CertificateChangeStatus.Scheduled)
        .MinAsync(c => (DateTime?)c.ActivateAt, cancellationToken);

    public Task<List<CertificateChange>> GetScheduledAsync(CancellationToken cancellationToken = default) => Data
        .Where(c => c.Status == CertificateChangeStatus.Scheduled)
        .Include(c => c.Partner)
        .Include(c => c.Certificate)
        .OrderBy(c => c.ActivateAt)
        .ToListAsync(cancellationToken);

    public async Task<List<CertificateChange>> GetOverviewAsync(int take, CancellationToken cancellationToken = default)
    {
        var scheduled = await GetScheduledAsync(cancellationToken);
        var others = await Data
            .Where(c => c.Status != CertificateChangeStatus.Scheduled)
            .Include(c => c.Certificate)
            .OrderByDescending(c => c.ActivateAt)
            .ThenByDescending(c => c.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        return scheduled.OrderByDescending(c => c.ActivateAt).Concat(others).ToList();
    }
}
