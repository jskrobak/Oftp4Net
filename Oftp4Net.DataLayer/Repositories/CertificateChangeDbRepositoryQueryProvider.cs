using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;
using DbContext = Havit.Data.EntityFrameworkCore.DbContext;

namespace Oftp4Net.DataLayer.Repositories;

public class CertificateChangeDbRepositoryQueryProvider: IRepositoryQueryProvider<CertificateChange, int>
{
    private readonly ISoftDeleteManager _softDeleteManager;
        
    private readonly Func<DbContext, int, CertificateChange> _getObjectQuery;
    private readonly Func<DbContext, int, CancellationToken, Task<CertificateChange>> _getObjectAsyncQuery;
    private readonly Func<DbContext, int[], IEnumerable<CertificateChange>> _getObjectsQuery;
    private readonly Func<DbContext, int[], IAsyncEnumerable<CertificateChange>> _getObjectsAsyncQuery;
    private readonly Func<DbContext, IEnumerable<CertificateChange>> _getAllQuery;
    private readonly Func<DbContext, IAsyncEnumerable<CertificateChange>> _getAllAsyncQuery;


    public CertificateChangeDbRepositoryQueryProvider(ISoftDeleteManager softDeleteManager)
    {
        _softDeleteManager = softDeleteManager;

        _getObjectQuery = EF.CompileQuery((DbContext dbContext, int id) => dbContext
            .Set<CertificateChange>()
            .TagWith("CertificateChangeDbRepository.GetObject")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int id, CancellationToken cancellationToken) => dbContext
            .Set<CertificateChange>()
            .TagWith("CertificateChangeDbRepository.GetObjectAsync")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectsQuery = EF.CompileQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<CertificateChange>()
            .TagWith("CertificateChangeDbRepository.GetObjects")
            .Where(entity => ids.Contains(entity.Id)));
       

        _getObjectsAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<CertificateChange>()
            .TagWith("CertificateChangeDbRepository.GetObjectsAsync")
            .Where(entity => ids.Contains(entity.Id)));

        _getAllQuery = EF.CompileQuery((DbContext dbContext) => dbContext
            .Set<CertificateChange>()
            .TagWith("CertificateChangeDbRepository.GetAll")
            .WhereNotDeleted(_softDeleteManager));

        _getAllAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext) => dbContext
            .Set<CertificateChange>()
            .TagWith("CertificateChangeDbRepository.GetAllAsync")
            .WhereNotDeleted(_softDeleteManager));
    }
    
    public Func<DbContext, int, CertificateChange> GetGetObjectQuery() => _getObjectQuery;
    public Func<DbContext, int, CancellationToken, Task<CertificateChange>> GetGetObjectAsyncQuery() => _getObjectAsyncQuery;
    public Func<DbContext, int[], IEnumerable<CertificateChange>> GetGetObjectsQuery() => _getObjectsQuery;
    public Func<DbContext, int[], IAsyncEnumerable<CertificateChange>> GetGetObjectsAsyncQuery() => _getObjectsAsyncQuery;
    public Func<DbContext, IAsyncEnumerable<CertificateChange>> GetGetAllAsyncQuery() => _getAllAsyncQuery;
    public Func<DbContext, IEnumerable<CertificateChange>> GetGetAllQuery() => _getAllQuery;

}