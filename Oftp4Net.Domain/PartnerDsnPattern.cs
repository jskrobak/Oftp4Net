namespace Oftp4Net.Domain;

/// <summary>
/// Virtual file name (SFIDDSN) a partner uses for a business process, as announced in its OFTP2 Communication
/// Setup. The pattern may be a regular expression. It is informational: nothing is checked against it.
/// </summary>
public class PartnerDsnPattern
{
    public string Pattern { get; set; } = string.Empty;

    /// <summary>File format (SFIDFMT): F, V, U or T.</summary>
    public string FileFormat { get; set; } = "U";

    public int? MaximumRecordSize { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? LongDescription { get; set; }
}
