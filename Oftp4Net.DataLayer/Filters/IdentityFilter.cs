using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Filters;

public class IdentityFilter: IFilter<Identity>
{
    public string? Name { get; set; }
    
    public IQueryable<Identity> Apply(IQueryable<Identity> data)
    {
        if (!string.IsNullOrEmpty(Name))
            data = data.Where(x => x.Name.Contains(Name));

        return data;
    }
}
