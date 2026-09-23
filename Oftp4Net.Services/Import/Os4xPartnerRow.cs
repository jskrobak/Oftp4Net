namespace Oftp4Net.Services.Import;

/// <summary>
/// One row of the OS4X partner table. OS4X keeps both sides of the relation together: the <c>his_*</c> columns
/// describe the partner, the <c>my_*</c> columns the identity used with it.
/// </summary>
public sealed class Os4xPartnerRow
{
    public long Idx { get; init; }
    public string ShortName { get; init; } = "";
    public string LongName { get; init; } = "";

    public string HisSsid { get; init; } = "";
    public string HisSfid { get; init; } = "";
    public string HisPassword { get; init; } = "";

    public string MySsid { get; init; } = "";
    public string MySfid { get; init; } = "";
    public string MyPassword { get; init; } = "";

    public string Address { get; init; } = "";
    public int Port { get; init; }
    public int PortTls { get; init; }
    public bool UseTls { get; init; }

    /// <summary>OFTP release of the partner; only 2 can be imported.</summary>
    public double OftpVersion { get; init; }

    /// <summary>Cipher suite as a number (SFIDCIPH), 0 when no file security is configured.</summary>
    public int CipherSuite { get; init; }

    public bool Sign { get; init; }
    public bool Encrypt { get; init; }
    public int CompressionLevel { get; init; }
    public bool SecureAuthentication { get; init; }
    public bool RequestSignedEerp { get; init; }
    public bool Active { get; init; }
}
