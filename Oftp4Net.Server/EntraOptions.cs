namespace Oftp4Net.Server;

/// <summary>
/// Signing in to the administration with Microsoft Entra ID (configuration section <c>Entra</c>). Without a tenant
/// and a client identifier nothing changes and users sign in with their password as before.
/// </summary>
public sealed class EntraOptions
{
    public const string SectionName = "Entra";
    public const string SchemeName = "Entra";

    /// <summary>Directory (tenant) identifier of the app registration, or <c>organizations</c>.</summary>
    public string? TenantId { get; set; }

    /// <summary>Application (client) identifier of the app registration.</summary>
    public string? ClientId { get; set; }

    /// <summary>Client secret of the app registration.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Address the answer of Entra ID comes back to, as registered there: this path behind the public address of
    /// the application.
    /// </summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>Text of the button on the sign in page.</summary>
    public string ButtonText { get; set; } = "Sign in with Microsoft";

    /// <summary>The whole authority address, for a cloud other than the public one.</summary>
    public string? Authority { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        (!string.IsNullOrWhiteSpace(TenantId) || !string.IsNullOrWhiteSpace(Authority));

    public string AuthorityUrl =>
        string.IsNullOrWhiteSpace(Authority)
            ? $"https://login.microsoftonline.com/{TenantId!.Trim()}/v2.0"
            : Authority.TrimEnd('/');
}
