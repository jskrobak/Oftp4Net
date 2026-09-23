using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

public abstract class BaseParty
{
    public int Id { get; set; }

    [Required] 
    [StringLength(50)] 
    public string Name { get; set; } = string.Empty;
    
    [StringLength(200)]
    public string? Description { get; set; }
    
    /// <summary>ODETTE file identification (SFIDORIG / SFIDDEST).</summary>
    [Required]
    [StringLength(25)]
    public string SFID { get; set; } = string.Empty;
    
    /// <summary>ODETTE session identification (SSIDCODE).</summary>
    [Required]
    [StringLength(25)]
    public string SSID { get; set; } = string.Empty;
    
    [StringLength(8)]
    public string? Password { get; set; }

    /// <summary>
    /// Certificates of the party for one station and purpose, where one certificate does not serve everything
    /// (Odette OP08 2.5). What is not assigned here falls back to the single certificate of the party: the
    /// partner's certificate, or ours from the settings.
    /// </summary>
    public List<CertificateAssignment> Certificates { get; set; } = [];

    /// <summary>
    /// The assignment for a station and purpose: the one of the station itself, otherwise the one that applies to
    /// the whole party. <c>null</c> when nothing is assigned and the single certificate is to be used.
    /// </summary>
    public CertificateAssignment? FindCertificate(string? sfid, CertificateUsage usage)
    {
        if (Certificates.Count == 0)
            return null;

        var station = sfid?.Trim();
        return Certificates.FirstOrDefault(c => c.Usage == usage && !string.IsNullOrEmpty(c.Sfid) &&
                                                string.Equals(c.Sfid.Trim(), station, StringComparison.OrdinalIgnoreCase))
               ?? Certificates.FirstOrDefault(c => c.Usage == usage && string.IsNullOrEmpty(c.Sfid));
    }
}
