using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;
using DbContext = Havit.Data.EntityFrameworkCore.DbContext;

namespace Oftp4Net.DataLayer.Repositories;

public class TransferEventDbRepositoryQueryProvider: IRepositoryQueryProvider<TransferEvent, int>
{
    private readonly ISoftDeleteManager _softDeleteManager;
        
    private readonly Func<DbContext, int, TransferEvent> _getObjectQuery;
    private readonly Func<DbContext, int, CancellationToken, Task<TransferEvent>> _getObjectAsyncQuery;
    private readonly Func<DbContext, int[], IEnumerable<TransferEvent>> _getObjectsQuery;
    private readonly Func<DbContext, int[], IAsyncEnumerable<TransferEvent>> _getObjectsAsyncQuery;
    private readonly Func<DbContext, IEnumerable<TransferEvent>> _getAllQuery;
    private readonly Func<DbContext, IAsyncEnumerable<TransferEvent>> _getAllAsyncQuery;


    public TransferEventDbRepositoryQueryProvider(ISoftDeleteManager softDeleteManager)
    {
        _softDeleteManager = softDeleteManager;

        _getObjectQuery = EF.CompileQuery((DbContext dbContext, int id) => dbContext
            .Set<TransferEvent>()
            .TagWith("TransferEventDbRepository.GetObject")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int id, CancellationToken cancellationToken) => dbContext
            .Set<TransferEvent>()
            .TagWith("TransferEventDbRepository.GetObjectAsync")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectsQuery = EF.CompileQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<TransferEvent>()
            .TagWith("TransferEventDbRepository.GetObjects")
            .Where(entity => ids.Contains(entity.Id)));
       

        _getObjectsAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<TransferEvent>()
            .TagWith("TransferEventDbRepository.GetObjectsAsync")
            .Where(entity => ids.Contains(entity.Id)));

        _getAllQuery = EF.CompileQuery((DbContext dbContext) => dbContext
            .Set<TransferEvent>()
            .TagWith("TransferEventDbRepository.GetAll")
            .WhereNotDeleted(_softDeleteManager));

        _getAllAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext) => dbContext
            .Set<TransferEvent>()
            .TagWith("TransferEventDbRepository.GetAllAsync")
            .WhereNotDeleted(_softDeleteManager));
    }
    
    public Func<DbContext, int, TransferEvent> GetGetObjectQuery() => _getObjectQuery;
    public Func<DbContext, int, CancellationToken, Task<TransferEvent>> GetGetObjectAsyncQuery() => _getObjectAsyncQuery;
    public Func<DbContext, int[], IEnumerable<TransferEvent>> GetGetObjectsQuery() => _getObjectsQuery;
    public Func<DbContext, int[], IAsyncEnumerable<TransferEvent>> GetGetObjectsAsyncQuery() => _getObjectsAsyncQuery;
    public Func<DbContext, IAsyncEnumerable<TransferEvent>> GetGetAllAsyncQuery() => _getAllAsyncQuery;
    public Func<DbContext, IEnumerable<TransferEvent>> GetGetAllQuery() => _getAllQuery;

}