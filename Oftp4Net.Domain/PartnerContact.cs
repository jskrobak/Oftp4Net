namespace Oftp4Net.Domain;

/// <summary>A contact of a partner, usually for problems with the OFTP connection (stored with the partner).</summary>
public class PartnerContact
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Area of responsibility of the contact.</summary>
    public string? Description { get; set; }

    public List<string> Phones { get; set; } = [];
    public List<string> Emails { get; set; } = [];
    public string? Url { get; set; }
}
