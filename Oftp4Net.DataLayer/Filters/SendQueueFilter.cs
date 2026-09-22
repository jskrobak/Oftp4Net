using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Filters;

public class SendQueueFilter: IFilter<SendQueueItem>
{
    public string? Name { get; set; }
    public SendStatus? Status { get; set; }
    public string? PartnerSsid { get; set; }
    public string? Reference { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    public IQueryable<SendQueueItem> Apply(IQueryable<SendQueueItem> data)
    {
        if (!string.IsNullOrEmpty(Name))
            data = data.Where(x => x.VirtualFileName.Contains(Name));
        if (Status is { } status)
            data = data.Where(x => x.Status == status);
        if (!string.IsNullOrEmpty(PartnerSsid))
            data = data.Where(x => x.Partner.SSID == PartnerSsid);
        if (!string.IsNullOrEmpty(Reference))
            data = data.Where(x => x.Reference == Reference);
        if (From is { } from)
            data = data.Where(x => x.Created >= from);
        if (To is { } to)
            data = data.Where(x => x.Created < to);

        return data;
    }
}
