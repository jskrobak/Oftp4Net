using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oftp4Net.Services.Security;

/// <summary>A revocation list that cannot be read or does not belong to its issuer.</summary>
public class CrlException(string message) : Exception(message);

/// <summary>
/// A certificate revocation list (RFC 5280, section 5). .NET can write one but not read one, so the structure is
/// read here: the times it is valid for, the serial numbers it revokes and the signature of its issuer.
/// </summary>
public sealed class CertificateRevocationList
{
    private readonly HashSet<string> _revoked;

    private CertificateRevocationList(X500DistinguishedName issuer, DateTimeOffset thisUpdate,
        DateTimeOffset? nextUpdate, HashSet<string> revoked)
    {
        Issuer = issuer;
        ThisUpdate = thisUpdate;
        NextUpdate = nextUpdate;
        _revoked = revoked;
    }

    public X500DistinguishedName Issuer { get; }

    /// <summary>When the list was issued.</summary>
    public DateTimeOffset ThisUpdate { get; }

    /// <summary>When the issuer promises the next list; missing in a list that is written on demand.</summary>
    public DateTimeOffset? NextUpdate { get; }

    public int Count => _revoked.Count;

    /// <summary>Whether the list revokes a certificate; it is identified by its serial number (OP08 1.9 C).</summary>
    public bool Revokes(X509Certificate2 certificate) =>
        _revoked.Contains(certificate.SerialNumber.TrimStart('0').ToLowerInvariant());

    /// <summary>
    /// Reads a list in DER or PEM and checks that <paramref name="issuer"/> signed it. A list of somebody else is
    /// refused: it would otherwise hide a revocation.
    /// </summary>
    public static CertificateRevocationList Read(byte[] content, X509Certificate2 issuer)
    {
        var der = IsPem(content) ? Pem(content) : content;
        try
        {
            var list = new AsnReader(der, AsnEncodingRules.BER).ReadSequence();
            // The part that is signed, exactly as it is encoded.
            var signed = list.ReadEncodedValue();
            var algorithm = list.ReadSequence();
            var algorithmOid = algorithm.ReadObjectIdentifier();
            var signature = list.ReadBitString(out _);

            Verify(signed.ToArray(), algorithmOid, signature, issuer);

            var body = new AsnReader(signed, AsnEncodingRules.BER).ReadSequence();
            if (body.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
                body.ReadInteger();
            body.ReadSequence();

            var issuerName = new X500DistinguishedName(body.ReadEncodedValue().ToArray());
            if (!issuerName.RawData.AsSpan().SequenceEqual(issuer.SubjectName.RawData))
                throw new CrlException($"The list was issued by {issuerName.Name}, not by {issuer.Subject}.");

            var thisUpdate = ReadTime(body);
            DateTimeOffset? nextUpdate = IsTime(body) ? ReadTime(body) : null;

            var revoked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (body.HasData && body.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                var entries = body.ReadSequence();
                while (entries.HasData)
                {
                    var entry = entries.ReadSequence();
                    revoked.Add(Serial(entry.ReadInteger()));
                    // The revocation date and the entry extensions do not say whether it is revoked.
                }
            }

            return new CertificateRevocationList(issuerName, thisUpdate, nextUpdate, revoked);
        }
        catch (AsnContentException ex)
        {
            throw new CrlException("The revocation list cannot be read: " + ex.Message);
        }
    }

    private static void Verify(byte[] signed, string algorithmOid, byte[] signature, X509Certificate2 issuer)
    {
        var hash = algorithmOid switch
        {
            "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
            "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
            "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
            "1.2.840.113549.1.1.5" or "1.2.840.10045.4.1" => HashAlgorithmName.SHA1,
            _ => throw new CrlException($"The revocation list is signed with the unsupported algorithm {algorithmOid}."),
        };

        var valid = algorithmOid.StartsWith("1.2.840.10045", StringComparison.Ordinal)
            ? issuer.GetECDsaPublicKey() is { } ecdsa &&
              ecdsa.VerifyData(signed, signature, hash, DSASignatureFormat.Rfc3279DerSequence)
            : issuer.GetRSAPublicKey() is { } rsa &&
              rsa.VerifyData(signed, signature, hash, RSASignaturePadding.Pkcs1);

        if (!valid)
            throw new CrlException($"The signature of the revocation list of {issuer.Subject} is not valid.");
    }

    private static bool IsTime(AsnReader reader) =>
        reader.HasData && (reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime) ||
                           reader.PeekTag().HasSameClassAndValue(Asn1Tag.GeneralizedTime));

    private static DateTimeOffset ReadTime(AsnReader reader) =>
        reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime)
            ? reader.ReadUtcTime()
            : reader.ReadGeneralizedTime();

    /// <summary>The serial number as it is compared: hexadecimal, without leading zeros.</summary>
    private static string Serial(BigInteger value) =>
        value.ToString("x").TrimStart('0') is { Length: > 0 } text ? text : "0";

    private static bool IsPem(byte[] content) =>
        content.Length > 10 && content[0] == '-' && content[1] == '-';

    private static byte[] Pem(byte[] content)
    {
        var text = System.Text.Encoding.ASCII.GetString(content);
        var start = text.IndexOf("-----BEGIN X509 CRL-----", StringComparison.Ordinal);
        var end = text.IndexOf("-----END X509 CRL-----", StringComparison.Ordinal);
        if (start < 0 || end < start)
            throw new CrlException("The file contains no revocation list in PEM.");

        var body = text[(start + "-----BEGIN X509 CRL-----".Length)..end];
        return Convert.FromBase64String(body.Replace("\r", "").Replace("\n", ""));
    }

    /// <summary>
    /// The addresses of the revocation lists of a certificate (extension 2.5.29.31, RFC 5280 section 4.2.1.13);
    /// only those that can be fetched over HTTP.
    /// </summary>
    public static IReadOnlyList<string> DistributionPoints(X509Certificate2 certificate)
    {
        var extension = certificate.Extensions["2.5.29.31"];
        if (extension is null)
            return [];

        var urls = new List<string>();
        try
        {
            var points = new AsnReader(extension.RawData, AsnEncodingRules.BER).ReadSequence();
            while (points.HasData)
            {
                var point = points.ReadSequence();
                if (!point.HasData || !point.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                    continue;

                // distributionPoint [0] { fullName [0] GeneralNames }
                var name = point.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                if (!name.HasData || !name.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
                    continue;

                var names = name.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
                while (names.HasData)
                {
                    // uniformResourceIdentifier [6] IA5String
                    var tag = names.PeekTag();
                    if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6)))
                    {
                        var url = names.ReadCharacterString(UniversalTagNumber.IA5String,
                            new Asn1Tag(TagClass.ContextSpecific, 6));
                        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            urls.Add(url);
                    }
                    else
                    {
                        names.ReadEncodedValue();
                    }
                }
            }
        }
        catch (AsnContentException)
        {
            // A malformed extension names no list we could read.
        }

        return urls;
    }
}
