using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Oftp4Net.Domain;
using Oftp4Net.Services;

namespace Oftp4Net.Server;

/// <summary>
/// The sign in cookie: what it carries about the user and whether it is still valid. A cookie stays valid only
/// while the user exists with the security stamp it was issued with, so deleting a user or changing their
/// password signs them out everywhere.
/// </summary>
public static class AuthSessions
{
    public static ClaimsPrincipal CreatePrincipal(User user, bool signedInWithEntra = false)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.Name, user.UserName),
            new(AuthClaims.SecurityStamp, user.SecurityStamp),
        ];
        if (user.MustChangePassword && !signedInWithEntra)
            claims.Add(new Claim(AuthClaims.MustChangePassword, "true"));
        if (signedInWithEntra)
            claims.Add(new Claim(AuthClaims.SignedInWithEntra, "true"));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    /// <summary>Cookies from before the security stamp carry none and are refused, as are those of a changed user.</summary>
    public static async Task<bool> IsValidAsync(ClaimsPrincipal principal, UserService users,
        CancellationToken cancellationToken = default)
    {
        var userName = principal.Identity?.Name;
        var stamp = principal.FindFirst(AuthClaims.SecurityStamp)?.Value;
        return !string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(stamp) &&
               await users.IsSignInValidAsync(userName, stamp, cancellationToken);
    }
}
