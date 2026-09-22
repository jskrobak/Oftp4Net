using System.ComponentModel.DataAnnotations;

namespace Oftp4Net.Domain;

/// <summary>User of the administration UI.</summary>
public class User
{
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    public string UserName { get; set; } = string.Empty;

    /// <summary>Password hash created by ASP.NET Core Identity's PasswordHasher.</summary>
    [Required]
    [StringLength(500)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>The user has to set a new password after signing in (e.g. the default admin account).</summary>
    public bool MustChangePassword { get; set; }

    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime? LastLogin { get; set; }
}
