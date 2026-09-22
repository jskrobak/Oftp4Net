using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

/// <summary>
/// Bearer token of the REST API. Only the hash of the token is stored; the token itself is shown once when created.
/// </summary>
public class ApiToken
{
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Beginning of the token, so it can be recognised in the list.</summary>
    [Required]
    [StringLength(20)]
    public string Prefix { get; set; } = string.Empty;

    /// <summary>SHA-256 of the token, hex encoded.</summary>
    [Required]
    [StringLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsed { get; set; }

    /// <summary>Optional URL notified about files received from partners.</summary>
    [StringLength(500)]
    public string? InboxWebhookUrl { get; set; }

    /// <summary>Optional secret used to sign webhook requests (header X-Oftp4Net-Signature).</summary>
    [StringLength(200)]
    public string? WebhookSecret { get; set; }
}
