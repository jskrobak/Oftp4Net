using Havit.Blazor.Components.Web.Bootstrap;
using Havit.Data.Patterns.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.DataLayer.Filters;

namespace Oftp4Net.DataLayer.Repositories;

public interface ISettingsItemRepository: IRepository<SettingsItem, string>
{
    
}