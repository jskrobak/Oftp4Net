namespace Oftp4Net.Domain;

/// <summary>
/// Another destination (SFID) reached through the connection of a partner, e.g. a plant. The file settings are
/// taken over from the partner unless the sub-station overrides them (a <c>null</c> value means "as the partner").
/// </summary>
public class PartnerSubStation
{
    public string Name { get; set; } = string.Empty;

    /// <summary>ODETTE file identification of the sub-station (SFIDORIG / SFIDDEST).</summary>
    public string SFID { get; set; } = string.Empty;

    public bool? SignFiles { get; set; }
    public bool? EncryptFiles { get; set; }
    public bool? CompressFiles { get; set; }
    public bool? RequestSignedEndResponse { get; set; }
    public bool? RequireSignedFiles { get; set; }
    public bool? RequireEncryptedFiles { get; set; }
    public bool? RequireCompressedFiles { get; set; }
    public string? FileCipherSuite { get; set; }

    public List<PartnerContact> Contacts { get; set; } = [];

    /// <summary>Virtual file names the sub-station receives (files we send to it).</summary>
    public List<PartnerDsnPattern> InboundDsnPatterns { get; set; } = [];

    /// <summary>Virtual file names the sub-station sends to us.</summary>
    public List<PartnerDsnPattern> OutboundDsnPatterns { get; set; } = [];
}
