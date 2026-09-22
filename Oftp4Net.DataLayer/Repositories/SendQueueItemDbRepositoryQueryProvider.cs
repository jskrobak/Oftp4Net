using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;
using DbContext = Havit.Data.EntityFrameworkCore.DbContext;

namespace Oftp4Net.DataLayer.Repositories;

public class SendQueueItemDbRepositoryQueryProvider: IRepositoryQueryProvider<SendQueueItem, int>
{
    private readonly ISoftDeleteManager _softDeleteManager;
        
    private readonly Func<DbContext, int, SendQueueItem> _getObjectQuery;
    private readonly Func<DbContext, int, CancellationToken, Task<SendQueueItem>> _getObjectAsyncQuery;
    private readonly Func<DbContext, int[], IEnumerable<SendQueueItem>> _getObjectsQuery;
    private readonly Func<DbContext, int[], IAsyncEnumerable<SendQueueItem>> _getObjectsAsyncQuery;
    private readonly Func<DbContext, IEnumerable<SendQueueItem>> _getAllQuery;
    private readonly Func<DbContext, IAsyncEnumerable<SendQueueItem>> _getAllAsyncQuery;


    public SendQueueItemDbRepositoryQueryProvider(ISoftDeleteManager softDeleteManager)
    {
        _softDeleteManager = softDeleteManager;

        _getObjectQuery = EF.CompileQuery((DbContext dbContext, int id) => dbContext
            .Set<SendQueueItem>()
            .TagWith("SendQueueItemDbRepository.GetObject")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int id, CancellationToken cancellationToken) => dbContext
            .Set<SendQueueItem>()
            .TagWith("SendQueueItemDbRepository.GetObjectAsync")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectsQuery = EF.CompileQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<SendQueueItem>()
            .TagWith("SendQueueItemDbRepository.GetObjects")
            .Where(entity => ids.Contains(entity.Id)));
       

        _getObjectsAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<SendQueueItem>()
            .TagWith("SendQueueItemDbRepository.GetObjectsAsync")
            .Where(entity => ids.Contains(entity.Id)));

        _getAllQuery = EF.CompileQuery((DbContext dbContext) => dbContext
            .Set<SendQueueItem>()
            .TagWith("SendQueueItemDbRepository.GetAll")
            .WhereNotDeleted(_softDeleteManager));

        _getAllAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext) => dbContext
            .Set<SendQueueItem>()
            .TagWith("SendQueueItemDbRepository.GetAllAsync")
            .WhereNotDeleted(_softDeleteManager));
    }
    
    public Func<DbContext, int, SendQueueItem> GetGetObjectQuery() => _getObjectQuery;
    public Func<DbContext, int, CancellationToken, Task<SendQueueItem>> GetGetObjectAsyncQuery() => _getObjectAsyncQuery;
    public Func<DbContext, int[], IEnumerable<SendQueueItem>> GetGetObjectsQuery() => _getObjectsQuery;
    public Func<DbContext, int[], IAsyncEnumerable<SendQueueItem>> GetGetObjectsAsyncQuery() => _getObjectsAsyncQuery;
    public Func<DbContext, IAsyncEnumerable<SendQueueItem>> GetGetAllAsyncQuery() => _getAllAsyncQuery;
    public Func<DbContext, IEnumerable<SendQueueItem>> GetGetAllQuery() => _getAllQuery;

}