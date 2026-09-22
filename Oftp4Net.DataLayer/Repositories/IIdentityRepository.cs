using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.Patterns.Repositories;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface IIdentityRepository: IRepository<Identity, int>
{
    Task<DataFragment<Identity>> GetFragmentAsync(IdentityFilter filter, GridDataProviderRequest<Identity> request,
        CancellationToken cancellationToken = default);

    Task<Identity?> FindBySfidAsync(string sfid, CancellationToken cancellationToken = default);
}