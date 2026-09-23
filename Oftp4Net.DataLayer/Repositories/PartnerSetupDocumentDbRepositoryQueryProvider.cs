using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;
using DbContext = Havit.Data.EntityFrameworkCore.DbContext;

namespace Oftp4Net.DataLayer.Repositories;

public class PartnerSetupDocumentDbRepositoryQueryProvider: IRepositoryQueryProvider<PartnerSetupDocument, int>
{
    private readonly ISoftDeleteManager _softDeleteManager;
        
    private readonly Func<DbContext, int, PartnerSetupDocument> _getObjectQuery;
    private readonly Func<DbContext, int, CancellationToken, Task<PartnerSetupDocument>> _getObjectAsyncQuery;
    private readonly Func<DbContext, int[], IEnumerable<PartnerSetupDocument>> _getObjectsQuery;
    private readonly Func<DbContext, int[], IAsyncEnumerable<PartnerSetupDocument>> _getObjectsAsyncQuery;
    private readonly Func<DbContext, IEnumerable<PartnerSetupDocument>> _getAllQuery;
    private readonly Func<DbContext, IAsyncEnumerable<PartnerSetupDocument>> _getAllAsyncQuery;


    public PartnerSetupDocumentDbRepositoryQueryProvider(ISoftDeleteManager softDeleteManager)
    {
        _softDeleteManager = softDeleteManager;

        _getObjectQuery = EF.CompileQuery((DbContext dbContext, int id) => dbContext
            .Set<PartnerSetupDocument>()
            .TagWith("PartnerSetupDocumentDbRepository.GetObject")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int id, CancellationToken cancellationToken) => dbContext
            .Set<PartnerSetupDocument>()
            .TagWith("PartnerSetupDocumentDbRepository.GetObjectAsync")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectsQuery = EF.CompileQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<PartnerSetupDocument>()
            .TagWith("PartnerSetupDocumentDbRepository.GetObjects")
            .Where(entity => ids.Contains(entity.Id)));
       

        _getObjectsAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<PartnerSetupDocument>()
            .TagWith("PartnerSetupDocumentDbRepository.GetObjectsAsync")
            .Where(entity => ids.Contains(entity.Id)));

        _getAllQuery = EF.CompileQuery((DbContext dbContext) => dbContext
            .Set<PartnerSetupDocument>()
            .TagWith("PartnerSetupDocumentDbRepository.GetAll")
            .WhereNotDeleted(_softDeleteManager));

        _getAllAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext) => dbContext
            .Set<PartnerSetupDocument>()
            .TagWith("PartnerSetupDocumentDbRepository.GetAllAsync")
            .WhereNotDeleted(_softDeleteManager));
    }
    
    public Func<DbContext, int, PartnerSetupDocument> GetGetObjectQuery() => _getObjectQuery;
    public Func<DbContext, int, CancellationToken, Task<PartnerSetupDocument>> GetGetObjectAsyncQuery() => _getObjectAsyncQuery;
    public Func<DbContext, int[], IEnumerable<PartnerSetupDocument>> GetGetObjectsQuery() => _getObjectsQuery;
    public Func<DbContext, int[], IAsyncEnumerable<PartnerSetupDocument>> GetGetObjectsAsyncQuery() => _getObjectsAsyncQuery;
    public Func<DbContext, IAsyncEnumerable<PartnerSetupDocument>> GetGetAllAsyncQuery() => _getAllAsyncQuery;
    public Func<DbContext, IEnumerable<PartnerSetupDocument>> GetGetAllQuery() => _getAllQuery;

}