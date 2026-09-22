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

public class TransferEventRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<TransferEvent, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<TransferEvent, int> repositoryQueryProvider)
    : DbRepository<TransferEvent, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager,
        repositoryQueryProvider), ITransferEventRepository
{
    public async Task<DataFragment<TransferEvent>> GetFragmentAsync(TransferEventFilter filter,
        GridDataProviderRequest<TransferEvent> request, CancellationToken cancellationToken = default)
    {
        var filtered = filter.Apply(Data.AsNoTracking());

        var cnt = await filtered.CountAsync(cancellationToken);

        var data = await filtered.ApplyGridDataProviderRequest<TransferEvent>(request)
            .ToListAsync(cancellationToken);

        return new DataFragment<TransferEvent>
        {
            Data = data,
            TotalCount = cnt
        };
    }

    public async Task<int> ArchiveOlderThanAsync(DateTime cutoff, CancellationToken cancellationToken = default)
    {
        return await Data
            .Where(e => !e.IsArchived && e.Timestamp < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsArchived, true), cancellationToken);
    }
}
