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

public class ListenerRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<Listener, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<Listener, int> repositoryQueryProvider)
    : DbRepository<Listener, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager,
        repositoryQueryProvider), IListenerRepository
{
    public async Task<DataFragment<Listener>> GetFragmentAsync(ListenerFilter filter,
        GridDataProviderRequest<Listener> request,
        CancellationToken cancellationToken = default)
    {
        var filtered = filter.Apply(Data
            .Include(i => i.Certificate)
            .Include(i => i.Identity));

        var cnt = await filtered.CountAsync(cancellationToken);

        var data = await filtered.ApplyGridDataProviderRequest<Listener>(request)
            .ToListAsync(cancellationToken: cancellationToken);

        return new DataFragment<Listener>()
        {
            Data = data,
            TotalCount = cnt
        };
    }

    public async Task<List<Listener>> GetEnabledWithRefsAsync(CancellationToken cancellationToken = default)
    {
        return await Data
            .Where(i => i.Enabled)
            .Include(i => i.Certificate)
            .Include(i => i.Identity)
            .ToListAsync(cancellationToken);
    }
}