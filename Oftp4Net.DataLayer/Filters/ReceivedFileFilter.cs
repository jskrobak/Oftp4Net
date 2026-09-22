using Oftp4Net.Domain;

namespace Oftp4Net.DataLayer.Filters;

public class ReceivedFileFilter: IFilter<ReceivedFile>
{
    public string? Name { get; set; }
    public ReceiveStatus? Status { get; set; }
    public string? PartnerSsid { get; set; }
    public bool OnlyNotFetched { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    public IQueryable<ReceivedFile> Apply(IQueryable<ReceivedFile> data)
    {
        if (!string.IsNullOrEmpty(Name))
            data = data.Where(x => x.VirtualFileName.Contains(Name));
        if (Status is { } status)
            data = data.Where(x => x.Status == status);
        if (!string.IsNullOrEmpty(PartnerSsid))
            data = data.Where(x => x.Partner != null && x.Partner.SSID == PartnerSsid);
        if (OnlyNotFetched)
            data = data.Where(x => x.FetchedDate == null);
        if (From is { } from)
            data = data.Where(x => x.Created >= from);
        if (To is { } to)
            data = data.Where(x => x.Created < to);

        return data;
    }
}
