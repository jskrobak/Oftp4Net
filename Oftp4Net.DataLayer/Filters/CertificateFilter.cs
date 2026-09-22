using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Filters;

public class CertificateFilter: IFilter<Certificate>
{
    public string? Name { get; set; }
    
    public IQueryable<Certificate> Apply(IQueryable<Certificate> data)
    {
        if (!string.IsNullOrEmpty(Name))
            data = data.Where(x => x.Name.Contains(Name));

        return data;
    }
}
