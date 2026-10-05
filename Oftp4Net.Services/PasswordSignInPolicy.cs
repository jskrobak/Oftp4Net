using System.Net;
using Microsoft.Extensions.Configuration;

namespace Oftp4Net.Services;

/// <summary>Where signing in with a user name and password is possible.</summary>
public enum PasswordSignInMode
{
    /// <summary>From anywhere.</summary>
    Everywhere,

    /// <summary>From localhost, private networks and the networks of <c>Authentication:PasswordSignInNetworks</c>.</summary>
    LocalNetworks,

    /// <summary>Not at all: Microsoft Entra ID only.</summary>
    Never,
}

/// <summary>
/// Where the password form may be used (configuration <c>Authentication:PasswordSignIn</c>). With Microsoft Entra ID
/// configured the default is <see cref="PasswordSignInMode.LocalNetworks"/>: from the internet only Entra ID is
/// offered and the password endpoint does not exist, so bots have nothing to try passwords on, while a password still
/// works through an SSH tunnel or from the internal network. Without Entra ID passwords are the only way in, so they
/// work from everywhere whatever is configured.
/// </summary>
public sealed class PasswordSignInPolicy
{
    public const string ModeKey = "Authentication:PasswordSignIn";
    public const string NetworksKey = "Authentication:PasswordSignInNetworks";

    /// <summary>Localhost and the private networks (RFC 1918, IPv6 unique local addresses).</summary>
    public static readonly IReadOnlyList<IPNetwork> LocalNetworks =
    [
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("fc00::/7"),
    ];

    private PasswordSignInPolicy(PasswordSignInMode mode, IReadOnlyList<IPNetwork> networks, string? warning)
    {
        Mode = mode;
        Networks = networks;
        Warning = warning;
    }

    public PasswordSignInMode Mode { get; }

    /// <summary>The networks a password is accepted from in <see cref="PasswordSignInMode.LocalNetworks"/>.</summary>
    public IReadOnlyList<IPNetwork> Networks { get; }

    /// <summary>Why the configured mode is not used, for the log.</summary>
    public string? Warning { get; }

    public static PasswordSignInPolicy Create(IConfiguration configuration, bool entraConfigured)
    {
        var configured = configuration[ModeKey];
        PasswordSignInMode mode;
        if (string.IsNullOrWhiteSpace(configured))
            mode = entraConfigured ? PasswordSignInMode.LocalNetworks : PasswordSignInMode.Everywhere;
        else if (!Enum.TryParse(configured.Trim(), ignoreCase: true, out mode) || !Enum.IsDefined(mode))
            throw new InvalidOperationException($"{ModeKey} '{configured}' is not Everywhere, LocalNetworks or Never.");

        string? warning = null;
        if (!entraConfigured && mode != PasswordSignInMode.Everywhere)
        {
            // Without Entra ID nobody could sign in at all.
            warning = $"{ModeKey} is {mode}, but Microsoft Entra ID is not configured: passwords work from everywhere.";
            mode = PasswordSignInMode.Everywhere;
        }

        return new PasswordSignInPolicy(mode, [.. LocalNetworks, .. ParseNetworks(configuration)], warning);
    }

    /// <summary>Networks as a list or separated by commas; a single address is a network of its own.</summary>
    private static IEnumerable<IPNetwork> ParseNetworks(IConfiguration configuration)
    {
        var section = configuration.GetSection(NetworksKey);
        var values = section.Value is { } value
            ? value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : section.GetChildren().Select(c => c.Value).OfType<string>();

        foreach (var text in values)
        {
            if (IPNetwork.TryParse(text, out var network))
                yield return network;
            else if (IPAddress.TryParse(text, out var address))
                yield return new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            else
                throw new InvalidOperationException($"{NetworksKey}: '{text}' is neither a network such as 203.0.113.0/24 nor an address.");
        }
    }

    /// <summary>
    /// Whether the client may sign in with a password. The address is the one after the forwarded headers, i.e. of
    /// the client only when the reverse proxy is trusted (<c>ReverseProxy:TrustAll</c>), otherwise of the proxy.
    /// </summary>
    public bool Allows(IPAddress? address)
    {
        switch (Mode)
        {
            case PasswordSignInMode.Everywhere:
                return true;
            case PasswordSignInMode.Never:
                return false;
        }

        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return Networks.Any(n => n.Contains(address));
    }

    public string Describe() => Mode switch
    {
        PasswordSignInMode.LocalNetworks => $"passwords only from {string.Join(", ", Networks)}",
        PasswordSignInMode.Never => "no passwords, Microsoft Entra ID only",
        _ => "passwords from everywhere",
    };
}
