using Oftp4Net.Core.Protocol;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.Import;

/// <summary>What the import would do with one row of the partner table of an OS4X installation.</summary>
public sealed class Os4xPartnerCandidate
{
    public required Os4xPartnerRow Source { get; init; }

    /// <summary>The partner as it would be created; <c>null</c> for a sub-station or a row that cannot be imported.</summary>
    public Partner? Partner { get; init; }

    /// <summary>The sub-station a row without SSID becomes on the partner of <see cref="Parent"/>.</summary>
    public PartnerSubStation? SubStation { get; init; }

    /// <summary>For a sub-station: the row of the partner whose connection it uses.</summary>
    public Os4xPartnerCandidate? Parent { get; set; }

    /// <summary>Identity the partner is used with, taken from the <c>my_*</c> columns of the same row.</summary>
    public string IdentitySsid { get; init; } = "";
    public string IdentitySfid { get; init; } = "";
    public string IdentityPassword { get; init; } = "";

    /// <summary>
    /// Name of that identity here: the part of the OS4X name before "__" (<c>ARTIPA</c> of <c>ARTIPA__VW</c>), unless
    /// another identity has it already.
    /// </summary>
    public string IdentityName { get; set; } = "";

    /// <summary>Why the row is skipped, and what the import does beyond the plain mapping.</summary>
    public List<string> Notes { get; init; } = [];

    /// <summary>A partner with the same SSID is already in the database, so the row is left alone.</summary>
    public bool AlreadyExists { get; set; }

    /// <summary>Left out although it could be mapped (e.g. the same sub-station twice); the reason is in <see cref="Notes"/>.</summary>
    public bool Skipped { get; set; }

    public bool IsSubStation => Source.SubStationOf >= 0 && string.IsNullOrWhiteSpace(Source.HisSsid);

    public bool CanImport => !AlreadyExists && !Skipped &&
                             (Partner is not null || SubStation is not null && Parent is { CanImport: true });

    /// <summary>Selected for import in the user interface.</summary>
    public bool Selected { get; set; }

    /// <summary>The name in OS4X.</summary>
    public string Name => Source.ShortName;

    public string Ssid => Partner?.SSID ?? (SubStation is not null ? $"SFID {SubStation.SFID}" : Source.HisSsid);

    /// <summary>What the row becomes here.</summary>
    public string Target => Partner is not null
        ? $"partner {Partner.Name}"
        : SubStation is not null
            ? $"sub-station {SubStation.Name} of {Parent?.Partner?.Name ?? "?"}"
            : "-";

    public string Identity => Partner is null || string.IsNullOrEmpty(IdentitySsid) ? "-" : $"{IdentityName} ({IdentitySsid})";

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
/// not taken over. OS4X names a partner <c>IDENTITY__PARTNER</c>: the partner gets the part after "__", our
/// identity the part before it.
/// </summary>
public static class Os4xPartnerMapper
{
    /// <summary>
    /// The identity and the partner of an OS4X name <c>IDENTITY__PARTNER</c>; the identity is <c>null</c> when the
    /// name has no "__".
    /// </summary>
    public static (string? Identity, string Partner) SplitName(string? shortName)
    {
        var name = Trim(shortName);
        var separator = name.IndexOf("__", StringComparison.Ordinal);
        return separator > 0 && separator < name.Length - 2
            ? (name[..separator], name[(separator + 2)..])
            : (null, name);
    }

    public static Os4xPartnerCandidate Map(Os4xPartnerRow row)
    {
        var (identityName, partnerName) = SplitName(row.ShortName);

        if (row.SubStationOf >= 0 && string.IsNullOrWhiteSpace(row.HisSsid))
            return MapSubStation(row, partnerName);

        if (row.OftpVersion < 1)
            return Refuse(row, $"OFTP release {row.OftpVersion:0.#} is not known.");

        if (string.IsNullOrWhiteSpace(row.HisSsid))
            return Refuse(row, "The partner has no ODETTE identification code.");

        if (row.AddressType == Os4xAddressTypes.Isdn)
            return Refuse(row, "The partner is reached over ISDN, only TCP/IP is supported.");

        var port = row.UseTls ? row.PortTls : row.Port;
        if (port is < 1 or > 65535)
            return Refuse(row, $"Port {port} of the partner is not valid.");

        var partner = new Partner
        {
            Name = Truncate(Fallback(partnerName, row.HisSsid), 50),
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
        candidate.IdentityName = Truncate(identityName ?? candidate.IdentitySsid, 50);

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

        if (partnerName.Length > 50 || Trim(row.LongName).Length > 200)
            candidate.Notes.Add("The name was shortened.");

        if (Trim(row.MyPassword).Length > 8)
            candidate.Notes.Add("The identity password was shortened to 8 characters.");

        if (string.IsNullOrWhiteSpace(candidate.IdentitySsid))
            candidate.Notes.Add("The row has no own identification code, assign an identity after the import.");

        return candidate;
    }

    /// <summary>
    /// A row without SSID that uses the connection of another partner (<c>substation_reference</c>): a plant or a
    /// customer behind a clearing centre, addressed by its SFID only.
    /// </summary>
    private static Os4xPartnerCandidate MapSubStation(Os4xPartnerRow row, string name)
    {
        var sfid = Truncate(row.HisSfid, 25);
        if (sfid.Length == 0)
            return Refuse(row, "The sub-station has no SFID.");

        var candidate = new Os4xPartnerCandidate
        {
            Source = row,
            SubStation = new PartnerSubStation { Name = Truncate(Fallback(name, sfid), 50), SFID = sfid },
        };

        if (!row.Active)
            candidate.Notes.Add("Marked as inactive in OS4X.");

        return candidate;
    }

    /// <summary>
    /// Notes where our password in the row differs from the one of <paramref name="identity"/> with the same SSID
    /// and SFID: one already here (<paramref name="sourceRow"/> <c>null</c>), or the one another row of OS4X
    /// creates. The partner then gets the password of its row as our password for it.
    /// </summary>
    public static void CompareIdentity(Os4xPartnerCandidate candidate, Identity identity, string? sourceRow)
    {
        var other = sourceRow is null ? "here" : $"in row {sourceRow}";

        if ((identity.Password ?? "") != candidate.IdentityPassword)
            candidate.Notes.Add($"Our identity {candidate.IdentitySsid} has another password {other} than in this row; " +
                                "the partner gets the password of this row as our password for it.");
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
