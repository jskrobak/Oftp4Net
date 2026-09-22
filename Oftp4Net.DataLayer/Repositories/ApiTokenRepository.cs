using Havit.Data.EntityFrameworkCore;
using Havit.Data.EntityFrameworkCore.Patterns.Caching;
using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Havit.Data.Patterns.DataLoaders;
using Havit.Data.Patterns.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public class ApiTokenRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<ApiToken, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<ApiToken, int> repositoryQueryProvider)
    : DbRepository<ApiToken, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager,
        repositoryQueryProvider), IApiTokenRepository
{
    public async Task<ApiToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        return await Data.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);
    }
}
