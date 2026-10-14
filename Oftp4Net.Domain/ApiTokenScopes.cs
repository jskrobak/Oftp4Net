namespace Oftp4Net.Domain;

/// <summary>
/// What an API token may do besides reading: partners, identities, certificate changes, the transfer log, the
/// status, our partner setup (PDX) and the health checks are open to every token.
/// </summary>
[Flags]
public enum ApiTokenScopes
{
    None = 0,

    /// <summary>Putting files into the send queue, reading, retrying and removing them.</summary>
    Outbox = 1,

    /// <summary>Fetching received files; the inbox webhook of the token is called only with this scope.</summary>
    Inbox = 2,

    /// <summary>
    /// Scheduling and cancelling certificate changes of partners and testing the connection to a partner. Nothing else
    /// of a partner can be changed.
    /// </summary>
    Certificates = 4,

    /// <summary>What every token could do before tokens had scopes.</summary>
    Default = Outbox | Inbox | Certificates,
}

public static class ApiTokenScopesExtensions
{
    public static readonly IReadOnlyList<ApiTokenScopes> All = [ApiTokenScopes.Outbox, ApiTokenScopes.Inbox, ApiTokenScopes.Certificates];

    /// <summary>The individual scopes, without <see cref="ApiTokenScopes.None"/> and <see cref="ApiTokenScopes.Default"/>.</summary>
    public static IEnumerable<ApiTokenScopes> Each(this ApiTokenScopes scopes) =>
        All.Where(scope => scopes.HasFlag(scope));
}
