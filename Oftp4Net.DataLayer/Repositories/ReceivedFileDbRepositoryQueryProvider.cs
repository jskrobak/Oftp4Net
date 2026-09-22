using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;
using DbContext = Havit.Data.EntityFrameworkCore.DbContext;

namespace Oftp4Net.DataLayer.Repositories;

public class ReceivedFileDbRepositoryQueryProvider: IRepositoryQueryProvider<ReceivedFile, int>
{
    private readonly ISoftDeleteManager _softDeleteManager;
        
    private readonly Func<DbContext, int, ReceivedFile> _getObjectQuery;
    private readonly Func<DbContext, int, CancellationToken, Task<ReceivedFile>> _getObjectAsyncQuery;
    private readonly Func<DbContext, int[], IEnumerable<ReceivedFile>> _getObjectsQuery;
    private readonly Func<DbContext, int[], IAsyncEnumerable<ReceivedFile>> _getObjectsAsyncQuery;
    private readonly Func<DbContext, IEnumerable<ReceivedFile>> _getAllQuery;
    private readonly Func<DbContext, IAsyncEnumerable<ReceivedFile>> _getAllAsyncQuery;


    public ReceivedFileDbRepositoryQueryProvider(ISoftDeleteManager softDeleteManager)
    {
        _softDeleteManager = softDeleteManager;

        _getObjectQuery = EF.CompileQuery((DbContext dbContext, int id) => dbContext
            .Set<ReceivedFile>()
            .TagWith("ReceivedFileDbRepository.GetObject")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int id, CancellationToken cancellationToken) => dbContext
            .Set<ReceivedFile>()
            .TagWith("ReceivedFileDbRepository.GetObjectAsync")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectsQuery = EF.CompileQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<ReceivedFile>()
            .TagWith("ReceivedFileDbRepository.GetObjects")
            .Where(entity => ids.Contains(entity.Id)));
       

        _getObjectsAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<ReceivedFile>()
            .TagWith("ReceivedFileDbRepository.GetObjectsAsync")
            .Where(entity => ids.Contains(entity.Id)));

        _getAllQuery = EF.CompileQuery((DbContext dbContext) => dbContext
            .Set<ReceivedFile>()
            .TagWith("ReceivedFileDbRepository.GetAll")
            .WhereNotDeleted(_softDeleteManager));

        _getAllAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext) => dbContext
            .Set<ReceivedFile>()
            .TagWith("ReceivedFileDbRepository.GetAllAsync")
            .WhereNotDeleted(_softDeleteManager));
    }
    
    public Func<DbContext, int, ReceivedFile> GetGetObjectQuery() => _getObjectQuery;
    public Func<DbContext, int, CancellationToken, Task<ReceivedFile>> GetGetObjectAsyncQuery() => _getObjectAsyncQuery;
    public Func<DbContext, int[], IEnumerable<ReceivedFile>> GetGetObjectsQuery() => _getObjectsQuery;
    public Func<DbContext, int[], IAsyncEnumerable<ReceivedFile>> GetGetObjectsAsyncQuery() => _getObjectsAsyncQuery;
    public Func<DbContext, IAsyncEnumerable<ReceivedFile>> GetGetAllAsyncQuery() => _getAllAsyncQuery;
    public Func<DbContext, IEnumerable<ReceivedFile>> GetGetAllQuery() => _getAllQuery;

}