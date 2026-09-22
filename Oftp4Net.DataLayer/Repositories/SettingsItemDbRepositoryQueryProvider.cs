using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;
using DbContext = Havit.Data.EntityFrameworkCore.DbContext;

namespace Oftp4Net.DataLayer.Repositories;

public class SettingsItemDbRepositoryQueryProvider: IRepositoryQueryProvider<SettingsItem, string>
{
    private readonly ISoftDeleteManager _softDeleteManager;
        
    private readonly Func<DbContext, string, SettingsItem> _getObjectQuery;
    private readonly Func<DbContext, string, CancellationToken, Task<SettingsItem>> _getObjectAsyncQuery;
    private readonly Func<DbContext, string[], IEnumerable<SettingsItem>> _getObjectsQuery;
    private readonly Func<DbContext, string[], IAsyncEnumerable<SettingsItem>> _getObjectsAsyncQuery;
    private readonly Func<DbContext, IEnumerable<SettingsItem>> _getAllQuery;
    private readonly Func<DbContext, IAsyncEnumerable<SettingsItem>> _getAllAsyncQuery;


    public SettingsItemDbRepositoryQueryProvider(ISoftDeleteManager softDeleteManager)
    {
        _softDeleteManager = softDeleteManager;

        _getObjectQuery = EF.CompileQuery((DbContext dbContext, string id) => dbContext
            .Set<SettingsItem>()
            .TagWith("SettingsItemDbRepository.GetObject")
            .Where(entity => entity.Name == id)
            .FirstOrDefault())!;

        _getObjectAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, string id, CancellationToken cancellationToken) => dbContext
            .Set<SettingsItem>()
            .TagWith("SettingsItemDbRepository.GetObjectAsync")
            .Where(entity => entity.Name == id)
            .FirstOrDefault())!;

        _getObjectsQuery = EF.CompileQuery((DbContext dbContext, string[] ids) => dbContext
            .Set<SettingsItem>()
            .TagWith("SettingsItemDbRepository.GetObjects")
            .Where(entity => ids.Contains(entity.Name)));
       

        _getObjectsAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext, string[] ids) => dbContext
            .Set<SettingsItem>()
            .TagWith("SettingsItemDbRepository.GetObjectsAsync")
            .Where(entity => ids.Contains(entity.Name)));

        _getAllQuery = EF.CompileQuery((DbContext dbContext) => dbContext
            .Set<SettingsItem>()
            .TagWith("SettingsItemDbRepository.GetAll")
            .WhereNotDeleted(_softDeleteManager));

        _getAllAsyncQuery = EF.CompileAsyncQuery((DbContext dbContext) => dbContext
            .Set<SettingsItem>()
            .TagWith("SettingsItemDbRepository.GetAllAsync")
            .WhereNotDeleted(_softDeleteManager));
    }
    
    public Func<DbContext, string, SettingsItem> GetGetObjectQuery() => _getObjectQuery;
    public Func<DbContext, string, CancellationToken, Task<SettingsItem>> GetGetObjectAsyncQuery() => _getObjectAsyncQuery;
    public Func<DbContext, string[], IEnumerable<SettingsItem>> GetGetObjectsQuery() => _getObjectsQuery;
    public Func<DbContext, string[], IAsyncEnumerable<SettingsItem>> GetGetObjectsAsyncQuery() => _getObjectsAsyncQuery;
    public Func<DbContext, IAsyncEnumerable<SettingsItem>> GetGetAllAsyncQuery() => _getAllAsyncQuery;
    public Func<DbContext, IEnumerable<SettingsItem>> GetGetAllQuery() => _getAllQuery;

}