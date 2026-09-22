using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Filters;

public class SendQueueFilter: IFilter<SendQueueItem>
{
    public string? Name { get; set; }

    public IQueryable<SendQueueItem> Apply(IQueryable<SendQueueItem> data)
    {
        if (!string.IsNullOrEmpty(Name))
            data = data.Where(x => x.VirtualFileName.Contains(Name));

        return data;
    }
}
