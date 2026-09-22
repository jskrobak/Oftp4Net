using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Filters;

public class ReceivedFileFilter: IFilter<ReceivedFile>
{
    public string? Name { get; set; }

    public IQueryable<ReceivedFile> Apply(IQueryable<ReceivedFile> data)
    {
        if (!string.IsNullOrEmpty(Name))
            data = data.Where(x => x.VirtualFileName.Contains(Name));

        return data;
    }
}
