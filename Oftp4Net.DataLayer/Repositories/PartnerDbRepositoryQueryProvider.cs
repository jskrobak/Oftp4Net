using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;
using DbContext = Havit.Data.EntityFrameworkCore.DbContext;

namespace Oftp4Net.DataLayer.Repositories;

public class PartnerDbRepositoryQueryProvider: IRepositoryQueryProvider<Partner, int>
{
    private readonly ISoftDeleteManager _softDeleteManager;
        
    private readonly Func<DbContext, int, Partner> _getObjectQuery;
    private readonly Func<DbContext, int, CancellationToken, Task<Partner>> _getObjectAsyncQuery;
    private readonly Func<DbContext, int[], IEnumerable<Partner>> _getObjectsQuery;
    private readonly Func<DbContext, int[], IAsyncEnumerable<Partner>> _getObjectsAsyncQuery;
    private readonly Func<DbContext, IEnumerable<Partner>> _getAllQuery;
    private readonly Func<DbContext, IAsyncEnumerable<Partner>> _getAllAsyncQuery;


    public PartnerDbRepositoryQueryProvider(ISoftDeleteManager softDeleteManager)
    {
        _softDeleteManager = softDeleteManager;

        _getObjectQuery = EF.CompileQuery((DbContext dbContext, int id) => dbContext
            .Set<Partner>()
            .TagWith("PartnerDbRepository.GetObject")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int id, CancellationToken cancellationToken) => dbContext
            .Set<Partner>()
            .TagWith("PartnerDbRepository.GetObjectAsync")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectsQuery = EF.CompileQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<Partner>()
            .TagWith("PartnerDbRepository.GetObjects")
            .Where(entity => ids.Contains(entity.Id)));
       

        _getObjectsAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<Partner>()
            .TagWith("PartnerDbRepository.GetObjectsAsync")
            .Where(entity => ids.Contains(entity.Id)));

        _getAllQuery = EF.CompileQuery((DbContext dbContext) => dbContext
            .Set<Partner>()
            .TagWith("PartnerDbRepository.GetAll")
            .WhereNotDeleted(_softDeleteManager));

        _getAllAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext) => dbContext
            .Set<Partner>()
            .TagWith("PartnerDbRepository.GetAllAsync")
            .WhereNotDeleted(_softDeleteManager));
    }
    
    public Func<DbContext, int, Partner> GetGetObjectQuery() => _getObjectQuery;
    public Func<DbContext, int, CancellationToken, Task<Partner>> GetGetObjectAsyncQuery() => _getObjectAsyncQuery;
    public Func<DbContext, int[], IEnumerable<Partner>> GetGetObjectsQuery() => _getObjectsQuery;
    public Func<DbContext, int[], IAsyncEnumerable<Partner>> GetGetObjectsAsyncQuery() => _getObjectsAsyncQuery;
    public Func<DbContext, IAsyncEnumerable<Partner>> GetGetAllAsyncQuery() => _getAllAsyncQuery;
    public Func<DbContext, IEnumerable<Partner>> GetGetAllQuery() => _getAllQuery;

}