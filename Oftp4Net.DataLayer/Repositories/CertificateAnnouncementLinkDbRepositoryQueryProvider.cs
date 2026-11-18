using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;
using DbContext = Havit.Data.EntityFrameworkCore.DbContext;

namespace Oftp4Net.DataLayer.Repositories;

public class CertificateAnnouncementLinkDbRepositoryQueryProvider: IRepositoryQueryProvider<CertificateAnnouncementLink, int>
{
    private readonly ISoftDeleteManager _softDeleteManager;
        
    private readonly Func<DbContext, int, CertificateAnnouncementLink> _getObjectQuery;
    private readonly Func<DbContext, int, CancellationToken, Task<CertificateAnnouncementLink>> _getObjectAsyncQuery;
    private readonly Func<DbContext, int[], IEnumerable<CertificateAnnouncementLink>> _getObjectsQuery;
    private readonly Func<DbContext, int[], IAsyncEnumerable<CertificateAnnouncementLink>> _getObjectsAsyncQuery;
    private readonly Func<DbContext, IEnumerable<CertificateAnnouncementLink>> _getAllQuery;
    private readonly Func<DbContext, IAsyncEnumerable<CertificateAnnouncementLink>> _getAllAsyncQuery;


    public CertificateAnnouncementLinkDbRepositoryQueryProvider(ISoftDeleteManager softDeleteManager)
    {
        _softDeleteManager = softDeleteManager;

        _getObjectQuery = EF.CompileQuery((DbContext dbContext, int id) => dbContext
            .Set<CertificateAnnouncementLink>()
            .TagWith("CertificateAnnouncementLinkDbRepository.GetObject")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int id, CancellationToken cancellationToken) => dbContext
            .Set<CertificateAnnouncementLink>()
            .TagWith("CertificateAnnouncementLinkDbRepository.GetObjectAsync")
            .Where(entity => entity.Id == id)
            .FirstOrDefault())!;

        _getObjectsQuery = EF.CompileQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<CertificateAnnouncementLink>()
            .TagWith("CertificateAnnouncementLinkDbRepository.GetObjects")
            .Where(entity => ids.Contains(entity.Id)));
       

        _getObjectsAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, int[] ids) => dbContext
            .Set<CertificateAnnouncementLink>()
            .TagWith("CertificateAnnouncementLinkDbRepository.GetObjectsAsync")
            .Where(entity => ids.Contains(entity.Id)));

        _getAllQuery = EF.CompileQuery((DbContext dbContext) => dbContext
            .Set<CertificateAnnouncementLink>()
            .TagWith("CertificateAnnouncementLinkDbRepository.GetAll")
            .WhereNotDeleted(_softDeleteManager));

        _getAllAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext) => dbContext
            .Set<CertificateAnnouncementLink>()
            .TagWith("CertificateAnnouncementLinkDbRepository.GetAllAsync")
            .WhereNotDeleted(_softDeleteManager));
    }
    
    public Func<DbContext, int, CertificateAnnouncementLink> GetGetObjectQuery() => _getObjectQuery;
    public Func<DbContext, int, CancellationToken, Task<CertificateAnnouncementLink>> GetGetObjectAsyncQuery() => _getObjectAsyncQuery;
    public Func<DbContext, int[], IEnumerable<CertificateAnnouncementLink>> GetGetObjectsQuery() => _getObjectsQuery;
    public Func<DbContext, int[], IAsyncEnumerable<CertificateAnnouncementLink>> GetGetObjectsAsyncQuery() => _getObjectsAsyncQuery;
    public Func<DbContext, IAsyncEnumerable<CertificateAnnouncementLink>> GetGetAllAsyncQuery() => _getAllAsyncQuery;
    public Func<DbContext, IEnumerable<CertificateAnnouncementLink>> GetGetAllQuery() => _getAllQuery;

}