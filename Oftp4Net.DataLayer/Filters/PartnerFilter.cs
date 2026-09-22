using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Filters;

public class PartnerFilter: IFilter<Partner>
{
    public string? Name { get; set; }
    
    public IQueryable<Partner> Apply(IQueryable<Partner> data)
    {
        // Case-insensitive comparison is provided by the database collation;
        // Contains(string, StringComparison) cannot be translated to SQL.
        if (!string.IsNullOrEmpty(Name))
            data = data.Where(x => x.Name.Contains(Name));
            
        return data;
    }
}
