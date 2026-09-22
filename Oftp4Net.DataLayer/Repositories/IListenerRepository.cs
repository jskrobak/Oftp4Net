using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.Patterns.Repositories;
using Oftp4Net.DataLayer.Filters;
using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Repositories;

public interface IListenerRepository: IRepository<Listener, int>
{
    Task<DataFragment<Listener>> GetFragmentAsync(ListenerFilter filter, GridDataProviderRequest<Listener> request,
        CancellationToken cancellationToken = default);

    Task<List<Listener>> GetEnabledWithRefsAsync(CancellationToken cancellationToken = default);
}