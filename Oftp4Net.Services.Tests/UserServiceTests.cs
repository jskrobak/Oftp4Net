using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Microsoft.Extensions.Logging.Abstractions;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Tests;

/// <summary>
/// Who is let into the administration: a password of the local user list, or an identity from Microsoft Entra ID
/// that is assigned to one of those users.
/// </summary>
public class UserServiceTests
{
    private static (UserService Service, FakeUserRepository Users) Create(params User[] users)
    {
        var repository = new FakeUserRepository(users);
        return (new UserService(repository, new FakeUnitOfWork(repository), new FakeTimeService(),
            NullLogger<UserService>.Instance), repository);
    }

    [Fact]
    public async Task IdentityFromEntraIsMatchedToItsUser()
    {
        var (service, _) = Create(new User { Id = 1, UserName = "jana", Email = "Jana.Novakova@example.com" });

        var user = await service.SignInWithEntraAsync("jana.novakova@EXAMPLE.com");

        Assert.NotNull(user);
        Assert.Equal("jana", user.UserName);
        Assert.NotNull(user.LastLogin);
    }

    [Fact]
    public async Task IdentityWithoutAUserIsNotLetIn()
    {
        var (service, _) = Create(new User { Id = 1, UserName = "jana", Email = "jana@example.com" });

        // Somebody of the tenant who is not configured here.
        Assert.Null(await service.SignInWithEntraAsync("petr@example.com"));
        Assert.Null(await service.SignInWithEntraAsync(""));
    }

    [Fact]
    public async Task UserWithoutAPasswordCannotSignInWithOne()
    {
        var (service, _) = Create(new User { Id = 1, UserName = "jana", Email = "jana@example.com", PasswordHash = "" });

        Assert.Null(await service.ValidateCredentialsAsync("jana", "anything"));
    }

    [Fact]
    public async Task UserWithoutAPasswordNeedsAnAddress()
    {
        var (service, users) = Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync("jana", "", mustChangePassword: false));
        Assert.Contains("e-mail address", ex.Message);

        await service.CreateAsync("jana", "", mustChangePassword: true, "jana@example.com");
        var created = Assert.Single(users.All);
        Assert.Equal("jana@example.com", created.Email);
        Assert.Empty(created.PasswordHash);
        // Changing a password that does not exist is not asked for.
        Assert.False(created.MustChangePassword);
    }

    [Fact]
    public async Task AnAddressBelongsToOneUserOnly()
    {
        var (service, _) = Create(new User { Id = 1, UserName = "jana", Email = "jana@example.com" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync("petr", "password123", mustChangePassword: false, " Jana@example.com "));
        Assert.Contains("already used", ex.Message);
    }

    [Fact]
    public async Task TheLastWayInIsNotTakenAway()
    {
        var entraOnly = new User { Id = 1, UserName = "jana", Email = "jana@example.com", PasswordHash = "" };
        var (service, _) = Create(entraOnly);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetEmailAsync(1, ""));
        Assert.Contains("could not sign in", ex.Message);
        Assert.Equal("jana@example.com", entraOnly.Email);
    }

    private sealed class FakeUserRepository(params User[] users) : IUserRepository
    {
        public List<User> All { get; } = [.. users];

        public Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default) =>
            Task.FromResult(All.FirstOrDefault(u => u.UserName == userName.Trim().ToLowerInvariant()));

        public Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default) =>
            Task.FromResult(email.Trim().Length == 0
                ? null
                : All.FirstOrDefault(u => string.Equals(u.Email, email.Trim(), StringComparison.OrdinalIgnoreCase)));

        public Task<bool> AnyAsync(CancellationToken cancellationToken = default) => Task.FromResult(All.Count > 0);

        public User GetObject(int id) => All.First(u => u.Id == id);
        public Task<User> GetObjectAsync(int id, CancellationToken cancellationToken = default) => Task.FromResult(GetObject(id));
        public List<User> GetObjects(params int[] ids) => [.. All.Where(u => ids.Contains(u.Id))];
        public Task<List<User>> GetObjectsAsync(int[] ids, CancellationToken cancellationToken = default) =>
            Task.FromResult(GetObjects(ids));
        public List<User> GetAll() => All;
        public Task<List<User>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(All);
    }

    /// <summary>A new user lands in the repository, everything else is changed on the entity itself.</summary>
    private sealed class FakeUnitOfWork(FakeUserRepository users) : IUnitOfWork
    {
        public void AddForInsert<TEntity>(TEntity entity) where TEntity : class
        {
            if (entity is User user)
                users.All.Add(user);
        }

        public void AddRangeForInsert<TEntity>(IEnumerable<TEntity> entities) where TEntity : class
        {
            foreach (var entity in entities)
                AddForInsert(entity);
        }
        public ValueTask AddForInsertAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default) where TEntity : class
        {
            AddForInsert(entity);
            return ValueTask.CompletedTask;
        }
        public ValueTask AddRangeForInsertAsync<TEntity>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default) where TEntity : class
        {
            AddRangeForInsert(entities);
            return ValueTask.CompletedTask;
        }
        public void AddForUpdate<TEntity>(TEntity entity) where TEntity : class { }
        public void AddRangeForUpdate<TEntity>(IEnumerable<TEntity> entities) where TEntity : class { }
        public void AddForDelete<TEntity>(TEntity entity) where TEntity : class { }
        public void AddRangeForDelete<TEntity>(IEnumerable<TEntity> entities) where TEntity : class { }
        public void Commit() { }
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void RegisterAfterCommitAction(Action action) { }
        public void RegisterAfterCommitAction(Func<CancellationToken, Task> action) { }
        public void Clear() { }
    }

    private sealed class FakeTimeService : ITimeService
    {
        public DateTime GetCurrentTime() => new(2026, 9, 23, 12, 0, 0);
        public DateTime GetCurrentDate() => GetCurrentTime().Date;
    }
}
