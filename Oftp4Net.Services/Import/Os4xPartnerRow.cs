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

    /// <summary>
    /// How OS4X reaches the partner (<c>addresstype</c>): 1 TCP/IP, 2 ISDN, 3 TCP/IP with TLS. The <c>use_tls</c>
    /// column is not what OS4X goes by: its user interface writes TLS into the address type only.
    /// </summary>
    public int AddressType { get; init; }

    public bool UseTls => AddressType == Os4xAddressTypes.Tls;

    /// <summary>
    /// <c>idx</c> of the partner whose connection a sub-station uses (<c>substation_reference</c>), -1 for a partner
    /// with a connection of its own. A sub-station has no SSID, only its SFID.
    /// </summary>
    public long SubStationOf { get; init; } = -1;

    /// <summary>
    /// Active certificates of the partner in PEM (<c>cipher_variable_values</c> of the remote certificate
    /// variables): OS4X keeps one certificate per partner, stored for every purpose and cipher suite.
    /// </summary>
    public IReadOnlyList<string> Certificates { get; init; } = [];

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

/// <summary>Values of the <c>addresstype</c> column of the OS4X partner table.</summary>
public static class Os4xAddressTypes
{
    public const int TcpIp = 1;
    public const int Isdn = 2;
    public const int Tls = 3;
}
