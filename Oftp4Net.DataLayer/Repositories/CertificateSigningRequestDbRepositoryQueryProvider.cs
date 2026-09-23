using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;
using DbContext = Havit.Data.EntityFrameworkCore.DbContext;

namespace Oftp4Net.DataLayer.Repositories;

public class CertificateSigningRequestDbRepositoryQueryProvider: IRepositoryQueryProvider<CertificateSigningRequest, int>
{
    private readonly ISoftDeleteManager _softDeleteManager;
        
    private readonly Func<DbContext, int, CertificateSigningRequest> _getObjectQuery;
    private readonly Func<DbContext, int, CancellationToken, Task<CertificateSigningRequest>> _getObjectAsyncQuery;
    private readonly Func<DbContext, int[], IEnumerable<CertificateSigningRequest>> _getObjectsQuery;
    private readonly Func<DbContext, int[], IAsyncEnumerable<CertificateSigningRequest>> _getObjectsAsyncQuery;
    private readonly Func<DbContext, IEnumerable<CertificateSigningRequest>> _getAllQuery;
    private readonly Func<DbContext, IAsyncEnumerable<CertificateSigningRequest>> _getAllAsyncQuery;


    public CertificateSigningRequestDbRepositoryQueryProvider(ISoftDeleteManager softDeleteManager)
    {
        _softDeleteManager = softDeleteManager;

        _getObjectQuery = EF.CompileQuery((DbContext dbContext, int id) => dbContext
            .Set<CertificateSigningRequest>()
            .TagWith("CertificateSigningRequestDbRepository.GetObject")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int id, CancellationToken cancellationToken) => dbContext
            .Set<CertificateSigningRequest>()
            .TagWith("CertificateSigningRequestDbRepository.GetObjectAsync")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectsQuery = EF.CompileQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<CertificateSigningRequest>()
            .TagWith("CertificateSigningRequestDbRepository.GetObjects")
            .Where(entity => ids.Contains(entity.Id)));
       

        _getObjectsAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<CertificateSigningRequest>()
            .TagWith("CertificateSigningRequestDbRepository.GetObjectsAsync")
            .Where(entity => ids.Contains(entity.Id)));

        _getAllQuery = EF.CompileQuery((DbContext dbContext) => dbContext
            .Set<CertificateSigningRequest>()
            .TagWith("CertificateSigningRequestDbRepository.GetAll")
            .WhereNotDeleted(_softDeleteManager));

        _getAllAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext) => dbContext
            .Set<CertificateSigningRequest>()
            .TagWith("CertificateSigningRequestDbRepository.GetAllAsync")
            .WhereNotDeleted(_softDeleteManager));
    }
    
    public Func<DbContext, int, CertificateSigningRequest> GetGetObjectQuery() => _getObjectQuery;
    public Func<DbContext, int, CancellationToken, Task<CertificateSigningRequest>> GetGetObjectAsyncQuery() => _getObjectAsyncQuery;
    public Func<DbContext, int[], IEnumerable<CertificateSigningRequest>> GetGetObjectsQuery() => _getObjectsQuery;
    public Func<DbContext, int[], IAsyncEnumerable<CertificateSigningRequest>> GetGetObjectsAsyncQuery() => _getObjectsAsyncQuery;
    public Func<DbContext, IAsyncEnumerable<CertificateSigningRequest>> GetGetAllAsyncQuery() => _getAllAsyncQuery;
    public Func<DbContext, IEnumerable<CertificateSigningRequest>> GetGetAllQuery() => _getAllQuery;

}