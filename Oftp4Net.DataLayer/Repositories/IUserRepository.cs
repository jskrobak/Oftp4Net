using Havit.Data.Patterns.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface IUserRepository : IRepository<User, int>
{
    Task<User?> FindByUserNameAsync(string userName, CancellationToken cancellationToken = default);
    Task<bool> AnyAsync(CancellationToken cancellationToken = default);
}
