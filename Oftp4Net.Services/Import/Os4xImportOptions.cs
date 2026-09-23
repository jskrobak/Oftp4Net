using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Services.Import;

/// <summary>Connection to the database of an OS4X installation the partners are read from.</summary>
public class Os4xImportOptions
{
    [Required]
    [StringLength(200)]
    public string Host { get; set; } = "localhost";

    [Range(1, 65535)]
    public int Port { get; set; } = 3306;

    [Required]
    [StringLength(100)]
    public string Database { get; set; } = "os4x";

    [Required]
    [StringLength(100)]
    public string User { get; set; } = "os4x";

    [StringLength(100)]
    public string Password { get; set; } = "";

    /// <summary>Table prefix of the installation (TABLEPREFIX in os4x.conf).</summary>
    [Required]
    [StringLength(20)]
    public string TablePrefix { get; set; } = "os4x_";
}
