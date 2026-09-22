using Havit.Data.Patterns.Repositories;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface IApiTokenRepository : IRepository<ApiToken, int>
{
    Task<ApiToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken = default);
}
