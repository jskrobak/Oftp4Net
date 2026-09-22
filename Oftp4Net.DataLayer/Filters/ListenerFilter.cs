using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Filters;

public class ListenerFilter: IFilter<Listener>
{
    public string? Name { get; set; }

    public IQueryable<Listener> Apply(IQueryable<Listener> data)
    {
        if (!string.IsNullOrEmpty(Name))
            data = data.Where(x => x.Name.Contains(Name));

        return data;
    }
}
