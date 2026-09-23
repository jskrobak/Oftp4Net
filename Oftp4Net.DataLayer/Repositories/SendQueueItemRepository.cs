using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.EntityFrameworkCore;
using Havit.Data.EntityFrameworkCore.Patterns.Caching;
using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Havit.Data.Patterns.DataLoaders;
using Havit.Data.Patterns.Infrastructure;
using Havit.Services.TimeServices;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public class SendQueueItemRepository(
    ITimeService timeService,
    IDbContext dbContext,
    IEntityKeyAccessor<SendQueueItem, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<SendQueueItem, int> repositoryQueryProvider)
    : DbRepository<SendQueueItem, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager,
        repositoryQueryProvider), ISendQueueItemRepository
{
    public async Task<DataFragment<SendQueueItem>> GetFragmentAsync(SendQueueFilter filter,
        GridDataProviderRequest<SendQueueItem> request,
        CancellationToken cancellationToken = default)
    {
        var filtered = filter.Apply(Data
            .Include(i => i.Identity)
            .Include(i => i.Partner));

        var cnt = await filtered.CountAsync(cancellationToken);

        var data = await filtered.ApplyGridDataProviderRequest<SendQueueItem>(request)
            .ToListAsync(cancellationToken: cancellationToken);

        return new DataFragment<SendQueueItem>()
        {
            Data = data,
            TotalCount = cnt
        };
    }

    public async Task<List<SendQueueItem>> GetAllWithRefsAsync()
    {
        return await Data
            .Include(i => i.Identity)
            .Include(i => i.Partner)
            .ToListAsync();
    }

    public async Task<List<SendQueueItem>> GetAllToProcessAsync()
    {
        return await Data
            .Where(i => (i.Status == SendStatus.NEW || i.Status == SendStatus.ERROR) &&
                        i.NextRetry <= timeService.GetCurrentTime())
            .Include(i => i.Identity)
            .Include(i => i.Partner)
            .OrderBy(i => i.NextRetry)
            .ThenBy(i => i.Id)
            .ToListAsync();
    }

    public async Task<List<SendQueueItem>> GetPendingForPartnerAsync(int partnerId, int? identityId,
        CancellationToken cancellationToken = default)
    {
        var now = timeService.GetCurrentTime();
        return await Data
            .Where(i => i.PartnerId == partnerId
                        && (identityId == null || i.IdentityId == identityId)
                        && (i.Status == SendStatus.NEW || i.Status == SendStatus.ERROR)
                        && i.NextRetry <= now)
            .Include(i => i.Identity)
            .OrderBy(i => i.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<DataFragment<SendQueueItem>> GetListAsync(SendQueueFilter filter, int skip, int take,
        CancellationToken cancellationToken = default)
    {
        var filtered = filter.Apply(Data);
        var count = await filtered.CountAsync(cancellationToken);
        var items = await filtered
            .OrderByDescending(i => i.Id)
            .Skip(skip)
            .Take(take)
            .Include(i => i.Identity)
            .Include(i => i.Partner)
            .ToListAsync(cancellationToken);

        return new DataFragment<SendQueueItem> { Data = items, TotalCount = count };
    }

    public async Task<SendQueueItem?> FindWithRefsAsync(int id, CancellationToken cancellationToken = default)
    {
        return await Data
            .Include(i => i.Identity)
            .Include(i => i.Partner)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public async Task<SendQueueItem?> FindSentAsync(string virtualFileName, string fileDate, string fileTime,
        CancellationToken cancellationToken = default)
    {
        return await Data
            .Where(i => i.VirtualFileName == virtualFileName
                        && i.FileDate == fileDate
                        && i.FileTime == fileTime
                        && i.Status == SendStatus.SENT)
            .Include(i => i.Identity)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<int> CountWaitingAsync(DateTime createdBefore, CancellationToken cancellationToken = default)
    {
        return Data
            .Where(i => (i.Status == SendStatus.NEW || i.Status == SendStatus.ERROR) && i.Created < createdBefore)
            .CountAsync(cancellationToken);
    }

    public async Task<List<SendQueuePartnerState>> GetStateByPartnerAsync(CancellationToken cancellationToken = default)
    {
        var counts = await Data
            .Where(i => i.Status == SendStatus.NEW || i.Status == SendStatus.ERROR || i.Status == SendStatus.FAILED
                        || i.Status == SendStatus.SENT)
            .GroupBy(i => i.PartnerId)
            .Select(g => new
            {
                PartnerId = g.Key,
                Waiting = g.Count(i => i.Status == SendStatus.NEW || i.Status == SendStatus.ERROR),
                OldestWaiting = g.Where(i => i.Status == SendStatus.NEW || i.Status == SendStatus.ERROR).Min(i => (DateTime?)i.Created),
                Failed = g.Count(i => i.Status == SendStatus.FAILED),
                AwaitingEndResponse = g.Count(i => i.Status == SendStatus.SENT),
                OldestSent = g.Where(i => i.Status == SendStatus.SENT).Min(i => i.SentDate),
            })
            .ToListAsync(cancellationToken);

        // The error of the item that failed last, among those still waiting or failed for good.
        var errors = await Data
            .Where(i => (i.Status == SendStatus.ERROR || i.Status == SendStatus.FAILED) && i.LastErrorDate != null)
            .GroupBy(i => i.PartnerId)
            .Select(g => g.OrderByDescending(i => i.LastErrorDate).Select(i => new { i.PartnerId, i.LastError, i.LastErrorDate }).First())
            .ToListAsync(cancellationToken);
        var errorByPartner = errors.ToDictionary(e => e.PartnerId);

        return counts
            .Select(c => errorByPartner.TryGetValue(c.PartnerId, out var error)
                ? new SendQueuePartnerState(c.PartnerId, c.Waiting, c.OldestWaiting, c.Failed, c.AwaitingEndResponse, c.OldestSent,
                    error.LastError, error.LastErrorDate)
                : new SendQueuePartnerState(c.PartnerId, c.Waiting, c.OldestWaiting, c.Failed, c.AwaitingEndResponse, c.OldestSent,
                    null, null))
            .ToList();
    }

    public Task<int> CountFailedAsync(DateTime failedSince, CancellationToken cancellationToken = default)
    {
        return Data
            .Where(i => i.Status == SendStatus.FAILED && i.LastErrorDate >= failedSince)
            .CountAsync(cancellationToken);
    }
}