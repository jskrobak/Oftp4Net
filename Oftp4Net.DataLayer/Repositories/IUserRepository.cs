using Havit.Data.Patterns.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface IUserRepository : IRepository<User, int>
{
    Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default);

    /// <summary>The user who signs in with this e-mail address or user principal name through Entra ID.</summary>
    Task<User?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);
}
