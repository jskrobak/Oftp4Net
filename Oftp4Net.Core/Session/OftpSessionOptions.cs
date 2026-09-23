using Oftp4Net.Core.Protocol;

namespace Oftp4Net.Core.Session;

public enum OftpRole
{
    /// <summary>The side that opened the network connection. It starts as the speaker.</summary>
    Initiator,

    /// <summary>The side that accepted the network connection.</summary>
    Responder
}

public sealed class OftpSessionOptions
{
    public const int MinExchangeBufferSize = 128;

    public required OftpRole Role { get; init; }

    /// <summary>Initiator only: identification code (SSIDCODE) sent in the initiator's SSID.</summary>
    public string LocalCode { get; init; } = "";

    /// <summary>Initiator only: password (SSIDPSWD) sent in the initiator's SSID.</summary>
    public string LocalPassword { get; init; } = "";

    /// <summary>Proposed data exchange buffer size. The smaller of both proposals is used.</summary>
    public int ExchangeBufferSize { get; init; } = 65536;

    /// <summary>Proposed credit (number of DATA buffers sent before waiting for CDT). The smaller of both proposals is used.</summary>
    public int Credit { get; init; } = 64;

    /// <summary>
    /// Highest protocol release level we announce in SSID (<see cref="ProtocolLevels"/>). The session runs at the
    /// lower of the two announced levels, so a partner on ODETTE-FTP 1.x is served with its own command layout.
    /// </summary>
    public int ProtocolLevel { get; init; } = ProtocolLevels.Oftp2;

    /// <summary>
    /// Require secure authentication (SSIDAUTH). Both sides have to require it, otherwise the session is aborted;
    /// for a responder the requirement of the identified partner (<see cref="OftpAuthenticationResult"/>) wins.
    /// </summary>
    public bool SecureAuthentication { get; init; }

    /// <summary>
    /// Offer ODETTE-FTP buffer compression (SSIDCMPR). Runs of equal octets are compressed in the data exchange
    /// buffers when both sides offer it. Receiving compressed buffers is always supported.
    /// </summary>
    public bool BufferCompression { get; init; }

    /// <summary>
    /// Offer restart of interrupted transfers (SSIDREST). Used only when both sides offer it; the speaker then
    /// proposes a restart position in SFID and the listener answers with the position it really has.
    /// </summary>
    public bool Restart { get; init; }

    /// <summary>Send / receive capability announced in SSID.</summary>
    public string SendReceive { get; init; } = SendReceiveCapabilities.Both;

    /// <summary>Maximum time to wait for the next command from the peer.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMinutes(3);

    internal void Validate()
    {
        if (ExchangeBufferSize is < MinExchangeBufferSize or > OftpTransport.MaxExchangeBufferSize)
            throw new ArgumentOutOfRangeException(nameof(ExchangeBufferSize));
        if (Credit is < 1 or > 999)
            throw new ArgumentOutOfRangeException(nameof(Credit));
        if (!ProtocolLevels.IsSupported(ProtocolLevel))
            throw new ArgumentOutOfRangeException(nameof(ProtocolLevel));
        if (SecureAuthentication && !ProtocolLevels.HasOftp2Features(ProtocolLevel))
            throw new ArgumentException("Secure authentication requires protocol level 5 (OFTP 2.0).",
                nameof(SecureAuthentication));
    }
}
