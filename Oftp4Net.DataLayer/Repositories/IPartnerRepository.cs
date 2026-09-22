using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.Patterns.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.DataLayer.Filters;

namespace Oftp4Net.DataLayer.Repositories;

public interface IPartnerRepository : IRepository<Partner, int>
{
    Task<DataFragment<Partner>> GetFragmentAsync(PartnerFilter filter, GridDataProviderRequest<Partner> request,
        CancellationToken cancellationToken = default);

    Task<Partner?> FindBySsidAsync(string ssid, CancellationToken cancellationToken = default);
}