using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.EntityFrameworkCore;
using Havit.Data.EntityFrameworkCore.Patterns.Caching;
using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Havit.Data.Patterns.DataLoaders;
using Havit.Data.Patterns.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public class SettingsItemRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<SettingsItem, string> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<SettingsItem, string> repositoryQueryProvider)
    : DbRepository<SettingsItem, string>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager, repositoryQueryProvider), ISettingsItemRepository
{
    
}