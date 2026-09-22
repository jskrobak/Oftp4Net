using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.EntityFrameworkCore;
using Havit.Data.EntityFrameworkCore.Patterns.Caching;
using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Havit.Data.Patterns.DataLoaders;
using Havit.Data.Patterns.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public class IdentityRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<Identity, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<Identity, int> repositoryQueryProvider)
    : DbRepository<Identity, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager,
        repositoryQueryProvider), IIdentityRepository
{
    public async Task<DataFragment<Identity>> GetFragmentAsync(IdentityFilter filter,
        GridDataProviderRequest<Identity> request,
        CancellationToken cancellationToken = default)
    {
        var filtered = filter.Apply(Data);

        var cnt = await filtered.CountAsync(cancellationToken);

        var data = await filtered.ApplyGridDataProviderRequest<Identity>(request)
            .ToListAsync(cancellationToken: cancellationToken);

        return new DataFragment<Identity>()
        {
            Data = data,
            TotalCount = cnt
        };
    }

    public async Task<Identity?> FindBySfidAsync(string sfid, CancellationToken cancellationToken = default)
    {
        return await Data.FirstOrDefaultAsync(i => i.SFID == sfid, cancellationToken);
    }
}