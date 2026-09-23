using Oftp4Net.Core.Protocol;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Import;

/// <summary>What the import would do with one partner of an OS4X installation.</summary>
public sealed class Os4xPartnerCandidate
{
    public required Os4xPartnerRow Source { get; init; }

    /// <summary>The partner as it would be created; <c>null</c> when the row cannot be imported.</summary>
    public Partner? Partner { get; init; }

    /// <summary>Identity the partner is used with, taken from the <c>my_*</c> columns of the same row.</summary>
    public string IdentitySsid { get; init; } = "";
    public string IdentitySfid { get; init; } = "";
    public string IdentityPassword { get; init; } = "";

    /// <summary>Why the row is skipped, and what the import does beyond the plain mapping.</summary>
    public List<string> Notes { get; init; } = [];

    /// <summary>A partner with the same SSID is already in the database, so the row is left alone.</summary>
    public bool AlreadyExists { get; set; }

    public bool CanImport => Partner is not null && !AlreadyExists;

    /// <summary>Selected for import in the user interface.</summary>
    public bool Selected { get; set; }

    public string Name => Partner?.Name ?? Source.ShortName;
    public string Ssid => Partner?.SSID ?? Source.HisSsid;

    public string Address => Partner is null ? "-" : $"{Partner.Host}:{Partner.Port} ({(Partner.UseTls ? "TLS" : "plain")})";

    public string Security => Partner is null
        ? "-"
        : string.Join(", ", new[]
        {
            Partner.SignFiles ? "signed" : null,
            Partner.CompressFiles ? "compressed" : null,
            Partner.EncryptFiles ? "encrypted" : null,
            Partner.SecureAuthentication ? "authenticated" : null,
            Partner.RequestSignedEndResponse ? "signed EERP" : null,
        }.Where(f => f is not null)) is { Length: > 0 } features
            ? features
            : "-";

    public string Status => AlreadyExists ? "already exists" : CanImport ? "new" : "cannot be imported";
}

/// <summary>
/// Maps the partner table of OS4X to our entities. OS4X has no separate identity table, so the identity of a
/// partner comes from its own row; buffer size and credit are per partner there and global here, so they are
/// not taken over.
/// </summary>
public static class Os4xPartnerMapper
{
    public static Os4xPartnerCandidate Map(Os4xPartnerRow row)
    {
        if (row.OftpVersion < 1)
            return Refuse(row, $"OFTP release {row.OftpVersion:0.#} is not known.");

        if (string.IsNullOrWhiteSpace(row.HisSsid))
            return Refuse(row, "The partner has no ODETTE identification code.");

        var port = row.UseTls ? row.PortTls : row.Port;
        if (port is < 1 or > 65535)
            return Refuse(row, $"Port {port} of the partner is not valid.");

        var partner = new Partner
        {
            Name = Truncate(Fallback(row.ShortName, row.HisSsid), 50),
            Description = Truncate(row.LongName, 200),
            SSID = Truncate(row.HisSsid, 25),
            SFID = Truncate(Fallback(row.HisSfid, row.HisSsid), 25),
            Password = Truncate(row.HisPassword, 8),
            Host = Truncate(row.Address, 100),
            Port = port,
            UseTls = row.UseTls,
            SignFiles = row.Sign,
            EncryptFiles = row.Encrypt,
            CompressFiles = row.CompressionLevel > 0,
            SecureAuthentication = row.SecureAuthentication,
            RequestSignedEndResponse = row.RequestSignedEerp,
            FileCipherSuite = CipherSuiteCode(row.CipherSuite) ?? CipherSuites.Aes256Sha1,
            // OS4X knows the release as 1 or 2; the older one is offered as revision 1.4.
            ProtocolLevel = row.OftpVersion >= 2 ? ProtocolLevels.Oftp2 : ProtocolLevels.Oftp14,
        };

        var candidate = new Os4xPartnerCandidate
        {
            Source = row,
            Partner = partner,
            IdentitySsid = Truncate(row.MySsid, 25),
            IdentitySfid = Truncate(Fallback(row.MySfid, row.MySsid), 25),
            IdentityPassword = Truncate(row.MyPassword, 8),
        };

        if (!row.Active)
            candidate.Notes.Add("Marked as inactive in OS4X.");

        if (partner.ProtocolLevel < ProtocolLevels.Oftp2)
        {
            candidate.Notes.Add($"OFTP {row.OftpVersion:0.#} partner, imported as ODETTE-FTP " +
                                $"{ProtocolLevels.Name(partner.ProtocolLevel)}; check the release in the partner.");
            partner.SignFiles = partner.EncryptFiles = partner.CompressFiles = false;
            partner.SecureAuthentication = partner.RequestSignedEndResponse = false;
        }

        if (row.CipherSuite > 0 && CipherSuiteCode(row.CipherSuite) is null)
            candidate.Notes.Add($"Cipher suite {row.CipherSuite} is not supported, {partner.FileCipherSuite} is used instead.");

        if (partner.SignFiles || partner.EncryptFiles || partner.SecureAuthentication)
            candidate.Notes.Add("Assign the partner's certificate after the import, it is not part of the OS4X database.");

        if (row.UseTls)
            candidate.Notes.Add("Check the trusted certificate of the TLS connection after the import.");

        if (Trim(row.ShortName).Length > 50 || Trim(row.LongName).Length > 200)
            candidate.Notes.Add("The name was shortened.");

        if (Trim(row.MyPassword).Length > 8)
            candidate.Notes.Add("The identity password was shortened to 8 characters.");

        if (string.IsNullOrWhiteSpace(candidate.IdentitySsid))
            candidate.Notes.Add("The row has no own identification code, assign an identity after the import.");

        return candidate;
    }

    /// <summary>OS4X stores the cipher suite as a number, we as the two digits of SFIDCIPH.</summary>
    private static string? CipherSuiteCode(int value) => value switch
    {
        1 => CipherSuites.TripleDesSha1,
        2 => CipherSuites.Aes256Sha1,
        3 => CipherSuites.TripleDesSha256,
        4 => CipherSuites.Aes256Sha256,
        5 => CipherSuites.TripleDesSha512,
        6 => CipherSuites.Aes256Sha512,
        _ => null,
    };

    private static Os4xPartnerCandidate Refuse(Os4xPartnerRow row, string reason) =>
        new() { Source = row, Partner = null, Notes = { reason } };

    private static string Fallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? Trim(fallback) : Trim(value);

    private static string Trim(string? value) => (value ?? "").Trim();

    private static string Truncate(string? value, int length)
    {
        var text = Trim(value);
        return text.Length <= length ? text : text[..length];
    }
}
