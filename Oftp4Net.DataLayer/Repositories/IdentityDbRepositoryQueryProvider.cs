using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;
using DbContext = Havit.Data.EntityFrameworkCore.DbContext;

namespace Oftp4Net.DataLayer.Repositories;

public class IdentityDbRepositoryQueryProvider: IRepositoryQueryProvider<Identity, int>
{
    private readonly ISoftDeleteManager _softDeleteManager;
        
    private readonly Func<DbContext, int, Identity> _getObjectQuery;
    private readonly Func<DbContext, int, CancellationToken, Task<Identity>> _getObjectAsyncQuery;
    private readonly Func<DbContext, int[], IEnumerable<Identity>> _getObjectsQuery;
    private readonly Func<DbContext, int[], IAsyncEnumerable<Identity>> _getObjectsAsyncQuery;
    private readonly Func<DbContext, IEnumerable<Identity>> _getAllQuery;
    private readonly Func<DbContext, IAsyncEnumerable<Identity>> _getAllAsyncQuery;


    public IdentityDbRepositoryQueryProvider(ISoftDeleteManager softDeleteManager)
    {
        _softDeleteManager = softDeleteManager;

        _getObjectQuery = EF.CompileQuery((DbContext dbContext, int id) => dbContext
            .Set<Identity>()
            .TagWith("IdentityDbRepository.GetObject")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int id, CancellationToken cancellationToken) => dbContext
            .Set<Identity>()
            .TagWith("IdentityDbRepository.GetObjectAsync")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectsQuery = EF.CompileQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<Identity>()
            .TagWith("IdentityDbRepository.GetObjects")
            .Where(entity => ids.Contains(entity.Id)));
       

        _getObjectsAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<Identity>()
            .TagWith("IdentityDbRepository.GetObjectsAsync")
            .Where(entity => ids.Contains(entity.Id)));

        _getAllQuery = EF.CompileQuery((DbContext dbContext) => dbContext
            .Set<Identity>()
            .TagWith("IdentityDbRepository.GetAll")
            .WhereNotDeleted(_softDeleteManager));

        _getAllAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext) => dbContext
            .Set<Identity>()
            .TagWith("IdentityDbRepository.GetAllAsync")
            .WhereNotDeleted(_softDeleteManager));
    }
    
    public Func<DbContext, int, Identity> GetGetObjectQuery() => _getObjectQuery;
    public Func<DbContext, int, CancellationToken, Task<Identity>> GetGetObjectAsyncQuery() => _getObjectAsyncQuery;
    public Func<DbContext, int[], IEnumerable<Identity>> GetGetObjectsQuery() => _getObjectsQuery;
    public Func<DbContext, int[], IAsyncEnumerable<Identity>> GetGetObjectsAsyncQuery() => _getObjectsAsyncQuery;
    public Func<DbContext, IAsyncEnumerable<Identity>> GetGetAllAsyncQuery() => _getAllAsyncQuery;
    public Func<DbContext, IEnumerable<Identity>> GetGetAllQuery() => _getAllQuery;

}