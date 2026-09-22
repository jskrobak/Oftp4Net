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
}
