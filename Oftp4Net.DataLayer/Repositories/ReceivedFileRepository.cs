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

public class ReceivedFileRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<ReceivedFile, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<ReceivedFile, int> repositoryQueryProvider)
    : DbRepository<ReceivedFile, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager,
        repositoryQueryProvider), IReceivedFileRepository
{
    public async Task<DataFragment<ReceivedFile>> GetFragmentAsync(ReceivedFileFilter filter,
        GridDataProviderRequest<ReceivedFile> request,
        CancellationToken cancellationToken = default)
    {
        var filtered = filter.Apply(Data.Include(i => i.Partner));

        var cnt = await filtered.CountAsync(cancellationToken);

        var data = await filtered.ApplyGridDataProviderRequest<ReceivedFile>(request)
            .ToListAsync(cancellationToken: cancellationToken);

        return new DataFragment<ReceivedFile>()
        {
            Data = data,
            TotalCount = cnt
        };
    }

    public async Task<DataFragment<ReceivedFile>> GetListAsync(ReceivedFileFilter filter, int skip, int take,
        CancellationToken cancellationToken = default)
    {
        var filtered = filter.Apply(Data.Include(i => i.Partner));
        var count = await filtered.CountAsync(cancellationToken);
        var items = await filtered
            .OrderByDescending(i => i.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return new DataFragment<ReceivedFile> { Data = items, TotalCount = count };
    }

    public async Task<ReceivedFile?> FindWithRefsAsync(int id, CancellationToken cancellationToken = default)
    {
        return await Data.Include(i => i.Partner).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public async Task<List<ReceivedFile>> GetUnconfirmedAsync(int partnerId, CancellationToken cancellationToken = default)
    {
        return await Data
            .Where(i => i.PartnerId == partnerId && i.ConfirmedDate == null
                        && (i.Status == ReceiveStatus.RECEIVED || i.Status == ReceiveStatus.NOT_DELIVERED))
            .OrderBy(i => i.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ReceivedFile>> GetAllUnconfirmedAsync(DateTime createdBefore,
        CancellationToken cancellationToken = default)
    {
        return await Data
            .Where(i => i.PartnerId != null && i.ConfirmedDate == null
                        && (i.Created < createdBefore || i.DecidedDate != null)
                        && (i.Status == ReceiveStatus.RECEIVED || i.Status == ReceiveStatus.NOT_DELIVERED))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(int partnerId, string virtualFileName, string fileDate, string fileTime,
        CancellationToken cancellationToken = default)
    {
        return await Data.AnyAsync(i => i.PartnerId == partnerId
                                        && i.VirtualFileName == virtualFileName
                                        && i.FileDate == fileDate
                                        && i.FileTime == fileTime
                                        && i.Status != ReceiveStatus.FAILED
                                        && i.Status != ReceiveStatus.INTERRUPTED, cancellationToken);
    }

    public async Task<ReceivedFile?> FindInterruptedAsync(int partnerId, string virtualFileName, string fileDate, string fileTime,
        CancellationToken cancellationToken = default)
    {
        return await Data
            .Where(i => i.PartnerId == partnerId
                        && i.VirtualFileName == virtualFileName
                        && i.FileDate == fileDate
                        && i.FileTime == fileTime
                        && i.Status == ReceiveStatus.INTERRUPTED)
            .OrderByDescending(i => i.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
