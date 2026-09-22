using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Oftp4Net.Services.Api;

namespace Oftp4Net.Server.Api;

/// <summary>
/// Authenticates REST API requests with <c>Authorization: Bearer &lt;token&gt;</c> against the tokens in the database.
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiTokenService tokenService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "ApiToken";

    /// <summary>Name of the token that authenticated the request.</summary>
    public const string TokenNameClaim = "oftp4net:token";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
            return AuthenticateResult.NoResult();

        var value = header.ToString();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Only the Bearer scheme is supported.");

        var token = await tokenService.ValidateAsync(value["Bearer ".Length..].Trim());
        if (token is null)
        {
            Logger.LogWarning("Rejected API request from {RemoteIp}: invalid token", Context.Connection.RemoteIpAddress);
            return AuthenticateResult.Fail("Invalid or expired token.");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, token.Name), new Claim(TokenNameClaim, token.Name)], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
