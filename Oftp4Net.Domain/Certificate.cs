using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

public class Certificate
{
    public int Id { get; set; }
    
    [Required]
    public string Name { get; set; } = string.Empty;
    
    public string? Base64Data { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    public bool HasPrivateKey { get; set; } = false;
    
    [StringLength(50)]
    public string Password { get; set; } = string.Empty;
}