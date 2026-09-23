using Havit.Data.EntityFrameworkCore;
using Havit.Data.EntityFrameworkCore.Patterns.Caching;
using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Havit.Data.Patterns.DataLoaders;
using Havit.Data.Patterns.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public class CertificateSigningRequestRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<CertificateSigningRequest, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<CertificateSigningRequest, int> repositoryQueryProvider)
    : DbRepository<CertificateSigningRequest, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager,
        repositoryQueryProvider), ICertificateSigningRequestRepository
{
    public async Task<List<CertificateSigningRequest>> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        return await Data
            .Where(r => r.CompletedDate == null)
            .OrderByDescending(r => r.Id)
            .ToListAsync(cancellationToken);
    }
}
