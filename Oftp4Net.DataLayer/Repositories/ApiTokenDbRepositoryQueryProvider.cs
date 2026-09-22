using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;
using DbContext = Havit.Data.EntityFrameworkCore.DbContext;

namespace Oftp4Net.DataLayer.Repositories;

public class ApiTokenDbRepositoryQueryProvider: IRepositoryQueryProvider<ApiToken, int>
{
    private readonly ISoftDeleteManager _softDeleteManager;
        
    private readonly Func<DbContext, int, ApiToken> _getObjectQuery;
    private readonly Func<DbContext, int, CancellationToken, Task<ApiToken>> _getObjectAsyncQuery;
    private readonly Func<DbContext, int[], IEnumerable<ApiToken>> _getObjectsQuery;
    private readonly Func<DbContext, int[], IAsyncEnumerable<ApiToken>> _getObjectsAsyncQuery;
    private readonly Func<DbContext, IEnumerable<ApiToken>> _getAllQuery;
    private readonly Func<DbContext, IAsyncEnumerable<ApiToken>> _getAllAsyncQuery;


    public ApiTokenDbRepositoryQueryProvider(ISoftDeleteManager softDeleteManager)
    {
        _softDeleteManager = softDeleteManager;

        _getObjectQuery = EF.CompileQuery((DbContext dbContext, int id) => dbContext
            .Set<ApiToken>()
            .TagWith("ApiTokenDbRepository.GetObject")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int id, CancellationToken cancellationToken) => dbContext
            .Set<ApiToken>()
            .TagWith("ApiTokenDbRepository.GetObjectAsync")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectsQuery = EF.CompileQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<ApiToken>()
            .TagWith("ApiTokenDbRepository.GetObjects")
            .Where(entity => ids.Contains(entity.Id)));
       

        _getObjectsAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<ApiToken>()
            .TagWith("ApiTokenDbRepository.GetObjectsAsync")
            .Where(entity => ids.Contains(entity.Id)));

        _getAllQuery = EF.CompileQuery((DbContext dbContext) => dbContext
            .Set<ApiToken>()
            .TagWith("ApiTokenDbRepository.GetAll")
            .WhereNotDeleted(_softDeleteManager));

        _getAllAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext) => dbContext
            .Set<ApiToken>()
            .TagWith("ApiTokenDbRepository.GetAllAsync")
            .WhereNotDeleted(_softDeleteManager));
    }
    
    public Func<DbContext, int, ApiToken> GetGetObjectQuery() => _getObjectQuery;
    public Func<DbContext, int, CancellationToken, Task<ApiToken>> GetGetObjectAsyncQuery() => _getObjectAsyncQuery;
    public Func<DbContext, int[], IEnumerable<ApiToken>> GetGetObjectsQuery() => _getObjectsQuery;
    public Func<DbContext, int[], IAsyncEnumerable<ApiToken>> GetGetObjectsAsyncQuery() => _getObjectsAsyncQuery;
    public Func<DbContext, IAsyncEnumerable<ApiToken>> GetGetAllAsyncQuery() => _getAllAsyncQuery;
    public Func<DbContext, IEnumerable<ApiToken>> GetGetAllQuery() => _getAllQuery;

}