using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

/// <summary>User of the administration UI.</summary>
public class User
{
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Password hash created by ASP.NET Core Identity's PasswordHasher. Empty for a user who signs in through
    /// Microsoft Entra ID only.
    /// </summary>
    [StringLength(500)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// E-mail address or user principal name the user signs in with through Microsoft Entra ID. Empty for a user
    /// who signs in with a password only.
    /// </summary>
    [StringLength(200)]
    public string? Email { get; set; }

    /// <summary>The user has to set a new password after signing in (e.g. the default admin account).</summary>
    public bool MustChangePassword { get; set; }

    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastLogin { get; set; }
}
