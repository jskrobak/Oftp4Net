using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Oftp4Net.Services.Security;

/// <summary>
/// Certificate Logical Identification Data (CLID, Odette OP08 1.9): a certificate is identified logically by its
/// owner (subject, issuer) and its purpose (key usage, extended key usage), and physically by the issuer together
/// with the serial number. The certificate exchange carries it in SFIDDESC, where it names the certificate that is
/// being replaced (OP08 2.5 G), so that a new certificate can be assigned to a partner even when its subject or
/// issuer changed.
/// </summary>
public sealed record CertificateLogicalId
{
    /// <summary>Longest SFIDDESC (RFC 5024, section 5.3.4).</summary>
    public const int MaxLength = 999;

    /// <summary>Issuer as a string according to RFC 4514.</summary>
    public required string Issuer { get; init; }

    /// <summary>Serial number in hexadecimal; together with the issuer it identifies the physical certificate.</summary>
    public required string SerialNumber { get; init; }

    /// <summary>Authority key identifier in hexadecimal; the subject key identifier for a self signed certificate.</summary>
    public required string AuthorityKeyIdentifier { get; init; }

    /// <summary>Basic key usage as the octets of its bit string, e.g. <c>a0</c>.</summary>
    public required string KeyUsage { get; init; }

    /// <summary>Object identifiers of the extended key usage; empty when the certificate has none.</summary>
    public IReadOnlyList<string> ExtendedKeyUsage { get; init; } = [];

    /// <summary>Subject as a string according to RFC 4514; missing when it did not fit into SFIDDESC.</summary>
    public string? Subject { get; init; }

    public static CertificateLogicalId From(X509Certificate2 certificate) => new()
    {
        Issuer = Name(certificate.IssuerName),
        SerialNumber = certificate.SerialNumber.ToLowerInvariant(),
        AuthorityKeyIdentifier = GetAuthorityKeyIdentifier(certificate),
        KeyUsage = GetKeyUsage(certificate),
        ExtendedKeyUsage = GetExtendedKeyUsage(certificate),
        Subject = Name(certificate.SubjectName),
    };

    /// <summary>
    /// A distinguished name as RFC 4514 wants it: the most specific part first, which is the reverse of the order
    /// it is encoded in. What .NET returns in <see cref="X500DistinguishedName.Name"/> depends on the platform.
    /// </summary>
    private static string Name(X500DistinguishedName name) => name.Decode(X500DistinguishedNameFlags.Reversed);

    /// <summary>
    /// The value for SFIDDESC: <c>key=value</c> pairs separated by LF in the order of OP08 2.5 G. The extended key
    /// usage and the subject are left out when the value would be longer than <see cref="MaxLength"/> octets.
    /// </summary>
    public string ToFileDescription()
    {
        var head = string.Join('\n',
        [
            "I=" + Encode(Issuer),
            "N=" + SerialNumber,
            "A=" + AuthorityKeyIdentifier,
            "B=" + KeyUsage,
        ]);

        var withUsage = ExtendedKeyUsage.Count == 0 ? head : head + "\nE=" + string.Join(',', ExtendedKeyUsage);
        if (Length(withUsage) > MaxLength)
            return head;

        var withSubject = string.IsNullOrEmpty(Subject) ? withUsage : withUsage + "\nS=" + Encode(Subject);
        return Length(withSubject) > MaxLength ? withUsage : withSubject;
    }

    /// <summary>Reads a value of SFIDDESC; <c>null</c> when it is not a CLID.</summary>
    public static CertificateLogicalId? Parse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;

        var fields = new Dictionary<char, string>();
        foreach (var line in description.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var field = line.Trim('\r', ' ');
            if (field.Length > 2 && field[1] == '=')
                fields[field[0]] = field[2..];
        }

        if (!fields.TryGetValue('I', out var issuer) || !fields.TryGetValue('N', out var serial))
            return null;

        return new CertificateLogicalId
        {
            Issuer = Decode(issuer),
            SerialNumber = serial.ToLowerInvariant(),
            AuthorityKeyIdentifier = fields.GetValueOrDefault('A', "").ToLowerInvariant(),
            KeyUsage = fields.GetValueOrDefault('B', "").ToLowerInvariant(),
            ExtendedKeyUsage = fields.TryGetValue('E', out var usages)
                ? usages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [],
            Subject = fields.TryGetValue('S', out var subject) ? Decode(subject) : null,
        };
    }

    /// <summary>
    /// Whether both describe the same certificate of the same owner for the same purpose: subject, issuer and key
    /// usages have to agree. The serial number is left out, because a renewed certificate has another one.
    /// Distinguished names are compared without spaces and case, implementations write them differently.
    /// </summary>
    public bool Identifies(CertificateLogicalId other)
    {
        if (!SameName(Issuer, other.Issuer))
            return false;

        // The subject is not in every SFIDDESC (it is left out when the value would be too long).
        if (Subject is { Length: > 0 } && other.Subject is { Length: > 0 } && !SameName(Subject, other.Subject))
            return false;

        if (!string.Equals(KeyUsage, other.KeyUsage, StringComparison.OrdinalIgnoreCase))
            return false;

        return ExtendedKeyUsage.Count == 0 || other.ExtendedKeyUsage.Count == 0 ||
               ExtendedKeyUsage.Order().SequenceEqual(other.ExtendedKeyUsage.Order());
    }

    public bool Identifies(X509Certificate2 certificate) => Identifies(From(certificate));

    /// <summary>Whether this is the physical certificate: the issuer and the serial number identify it (OP08 1.9 C).</summary>
    public bool IsInstance(X509Certificate2 certificate) =>
        SameName(Issuer, Name(certificate.IssuerName)) &&
        string.Equals(SerialNumber, certificate.SerialNumber, StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"{Subject ?? "?"} issued by {Issuer} (serial {SerialNumber}, key usage {KeyUsage})";

    private static string GetAuthorityKeyIdentifier(X509Certificate2 certificate)
    {
        var authority = certificate.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault();
        if (authority?.KeyIdentifier is { } identifier)
            return Convert.ToHexStringLower(identifier.Span);

        // A self signed certificate usually has no authority key identifier; its own one identifies the signer.
        var subject = certificate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault();
        return subject?.SubjectKeyIdentifier?.ToLowerInvariant() ?? "";
    }

    /// <summary>The key usage as the octets of the DER bit string, which is how OP08 2.5 G writes it (e.g. a0).</summary>
    private static string GetKeyUsage(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (extension is null)
            return "";

        var flags = (int)extension.KeyUsages;
        var first = (byte)(flags & 0xff);
        // DecipherOnly is the only flag in the second octet of the bit string.
        return (flags & (int)X509KeyUsageFlags.DecipherOnly) != 0
            ? Convert.ToHexStringLower([first, 0x80])
            : Convert.ToHexStringLower([first]);
    }

    private static string[] GetExtendedKeyUsage(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault() is { } extension
            ? extension.EnhancedKeyUsages.OfType<System.Security.Cryptography.Oid>().Select(o => o.Value ?? "")
                .Where(v => v.Length > 0).ToArray()
            : [];

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            // Not encoded, e.g. written by hand.
            return value;
        }
    }

    private static int Length(string value) => Encoding.UTF8.GetByteCount(value);

    /// <summary>
    /// Compares two distinguished names by their parts. Implementations write them in different orders (RFC 4514
    /// reverses the encoded order, some write the encoded one) and with different spacing, so the parts are
    /// compared as a set, without spaces and without regard to case.
    /// </summary>
    private static bool SameName(string a, string b) => Parts(a).SequenceEqual(Parts(b));

    private static IEnumerable<string> Parts(string name) =>
        name.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => string.Concat(part.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant())
            .Order(StringComparer.Ordinal);
}
