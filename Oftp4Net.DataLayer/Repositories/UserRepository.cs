using Havit.Data.EntityFrameworkCore;
using Havit.Data.EntityFrameworkCore.Patterns.Caching;
using Havit.Data.EntityFrameworkCore.Patterns.Repositories;
using Havit.Data.EntityFrameworkCore.Patterns.SoftDeletes;
using Havit.Data.Patterns.DataLoaders;
using Havit.Data.Patterns.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public class UserRepository(
    IDbContext dbContext,
    IEntityKeyAccessor<User, int> entityKeyAccessor,
    IDataLoader dataLoader,
    ISoftDeleteManager softDeleteManager,
    IEntityCacheManager entityCacheManager,
    IRepositoryQueryProvider<User, int> repositoryQueryProvider)
    : DbRepository<User, int>(dbContext, entityKeyAccessor, dataLoader, softDeleteManager, entityCacheManager,
        repositoryQueryProvider), IUserRepository
{
    public async Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default)
    {
        // User names are stored in lower case, see UserService.
        var normalized = userName.Trim().ToLowerInvariant();
        return await Data.FirstOrDefaultAsync(u => u.UserName == normalized, cancellationToken);
    }

    public async Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return normalized.Length == 0
            ? null
            : await Data.FirstOrDefaultAsync(u => u.Email != null && u.Email.ToLower() == normalized, cancellationToken);
    }

    public async Task<bool> AnyAsync(CancellationToken cancellationToken = default)
    {
        return await Data.AnyAsync(cancellationToken);
    }
}
