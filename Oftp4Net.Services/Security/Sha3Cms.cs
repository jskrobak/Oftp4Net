using System.Formats.Asn1;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Oftp4Net.Services.Security;

/// <summary>
/// CMS packages of cipher suite 10 (RSA-PSS and RSA-OAEP with a SHA3-512 digest), built here because the CMS
/// classes of .NET know no signature algorithm for RSA-PSS with SHA3 and no key transport with OAEP and SHA3,
/// although RSA itself does both. The structures are the ones
/// <see cref="System.Security.Cryptography.Pkcs.SignedCms"/> and
/// <see cref="System.Security.Cryptography.Pkcs.EnvelopedCms"/> produce otherwise (RFC 5652 and RFC 4055):
/// the content is included, and the signer and the recipient are identified by issuer and serial number.
/// </summary>
public static class Sha3Cms
{
    private const string SignedData = "1.2.840.113549.1.7.2";
    private const string Data = "1.2.840.113549.1.7.1";
    private const string ContentType = "1.2.840.113549.1.9.3";
    private const string MessageDigest = "1.2.840.113549.1.9.4";
    private const string RsassaPss = "1.2.840.113549.1.1.10";
    private const string Mgf1 = "1.2.840.113549.1.1.8";
    private const string EnvelopedData = "1.2.840.113549.1.7.3";
    private const string RsaesOaep = "1.2.840.113549.1.1.7";
    private const string Aes256Cbc = "2.16.840.1.101.3.4.1.42";

    /// <summary>The signature of this package can only be made and checked here.</summary>
    public static bool IsHandled(CipherSuite suite) =>
        suite.SignaturePadding == RSASignaturePadding.Pss && suite.HashAlgorithm == HashAlgorithmName.SHA3_512;

    public static byte[] Sign(byte[] content, X509Certificate2 certificate, CipherSuite suite)
    {
        using var key = certificate.GetRSAPrivateKey()
            ?? throw new FileSecurityException($"Certificate '{certificate.Subject}' has no RSA private key to sign with.");

        var digest = suite.ComputeHash(content);
        // The attributes are signed with the tag of a set and stored with the implicit tag [0].
        var signedAttributes = WriteSignedAttributes(digest, tag: null);
        var signature = key.SignData(signedAttributes, suite.HashAlgorithm, RSASignaturePadding.Pss);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(SignedData);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                // Version 1: the signer is identified by issuer and serial number and the content is id-data.
                writer.WriteInteger(1);

                using (writer.PushSetOf())
                    WriteDigestAlgorithm(writer, suite);

                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(Data);
                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                        writer.WriteOctetString(content);
                }

                using (writer.PushSetOf(new Asn1Tag(TagClass.ContextSpecific, 0)))
                    writer.WriteEncodedValue(certificate.RawData);

                using (writer.PushSetOf())
                    WriteSignerInfo(writer, certificate, suite,
                        WriteSignedAttributes(digest, new Asn1Tag(TagClass.ContextSpecific, 0)), signature);
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// Checks the signature of a package written by <see cref="Sign"/> and returns the signed content. The signer
    /// has to be <paramref name="expectedSigner"/>, as in the rest of the file level security.
    /// </summary>
    public static byte[] Verify(byte[] signedData, X509Certificate2 expectedSigner, CipherSuite suite)
    {
        try
        {
            var reader = new AsnReader(signedData, AsnEncodingRules.BER).ReadSequence();
            if (reader.ReadObjectIdentifier() != SignedData)
                throw new FileSecurityException("The content is not a CMS SignedData package.");

            var signed = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            signed.ReadInteger();
            signed.ReadSetOf();

            var encapsulated = signed.ReadSequence();
            encapsulated.ReadObjectIdentifier();
            var content = encapsulated.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadOctetString();

            // Certificates and revocation lists are optional and not needed: the signer is known.
            while (signed.PeekTag().TagClass == TagClass.ContextSpecific)
                signed.ReadEncodedValue();

            var signerInfos = signed.ReadSetOf();
            var signer = signerInfos.ReadSequence();
            signer.ReadInteger();
            signer.ReadEncodedValue();
            signer.ReadSequence();

            var attributes = signer.ReadSetOf(new Asn1Tag(TagClass.ContextSpecific, 0));
            // The signature is made over the attributes with the tag of a set, not with the one used here.
            var signedAttributes = ReEncodeAsSet(attributes, out var messageDigest);
            CheckMessageDigest(messageDigest, content, suite);

            signer.ReadSequence();
            var signature = signer.ReadOctetString();

            using var key = expectedSigner.GetRSAPublicKey()
                ?? throw new FileSecurityException($"Certificate '{expectedSigner.Subject}' has no RSA public key.");

            if (!key.VerifyData(signedAttributes, signature, suite.HashAlgorithm, RSASignaturePadding.Pss))
                throw new FileSecurityException(Core.Protocol.AnswerReasonCodes.InvalidFileSignature,
                    $"The signature is not valid or was not made by '{expectedSigner.Subject}'.");

            return content;
        }
        catch (AsnContentException ex)
        {
            throw new FileSecurityException(Core.Protocol.AnswerReasonCodes.InvalidFileSignature,
                "The signed content is not a valid CMS package: " + ex.Message, ex);
        }
    }

    private static void WriteDigestAlgorithm(AsnWriter writer, CipherSuite suite)
    {
        using (writer.PushSequence())
            writer.WriteObjectIdentifier(suite.DigestAlgorithm.Value!);
    }

    private static void WriteSignerInfo(AsnWriter writer, X509Certificate2 certificate, CipherSuite suite,
        byte[] storedAttributes, byte[] signature)
    {
        using (writer.PushSequence())
        {
            writer.WriteInteger(1);

            WriteIssuerAndSerialNumber(writer, certificate);
            WriteDigestAlgorithm(writer, suite);

            writer.WriteEncodedValue(storedAttributes);

            WriteSignatureAlgorithm(writer, suite);
            writer.WriteOctetString(signature);
        }
    }

    /// <summary>RSASSA-PSS with MGF1 and a salt as long as the hash (RFC 4055, section 3.1).</summary>
    private static void WriteSignatureAlgorithm(AsnWriter writer, CipherSuite suite)
    {
        var hash = suite.DigestAlgorithm.Value!;
        var saltLength = suite.ComputeHash([]).Length;

        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(RsassaPss);
            using (writer.PushSequence())
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                using (writer.PushSequence())
                    writer.WriteObjectIdentifier(hash);

                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(Mgf1);
                    using (writer.PushSequence())
                        writer.WriteObjectIdentifier(hash);
                }

                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 2)))
                    writer.WriteInteger(saltLength);
            }
        }
    }

    /// <summary>The attributes as they are signed: content type and the digest of the content, in a set.</summary>
    private static byte[] WriteSignedAttributes(byte[] digest, Asn1Tag? tag)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (tag is { } set ? writer.PushSetOf(set) : writer.PushSetOf())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(ContentType);
                using (writer.PushSetOf())
                    writer.WriteObjectIdentifier(Data);
            }

            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(MessageDigest);
                using (writer.PushSetOf())
                    writer.WriteOctetString(digest);
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// Reads the attributes of the package again, writes them with the tag they are signed with and returns the
    /// digest they carry.
    /// </summary>
    private static byte[] ReEncodeAsSet(AsnReader attributes, out byte[]? messageDigest)
    {
        messageDigest = null;
        var writer = new AsnWriter(AsnEncodingRules.DER);

        using (writer.PushSetOf())
        {
            while (attributes.HasData)
            {
                var encoded = attributes.ReadEncodedValue();
                writer.WriteEncodedValue(encoded.Span);

                var attribute = new AsnReader(encoded, AsnEncodingRules.BER).ReadSequence();
                var type = attribute.ReadObjectIdentifier();
                var values = attribute.ReadSetOf();

                if (type == MessageDigest)
                    messageDigest = values.ReadOctetString();
                else if (type == ContentType && values.ReadObjectIdentifier() != Data)
                    throw new FileSecurityException(Core.Protocol.AnswerReasonCodes.InvalidFileSignature,
                        "The signature is made over another content type than data.");
            }
        }

        return writer.Encode();
    }

    /// <summary>Checks that the digest the signature covers belongs to the content.</summary>
    private static void CheckMessageDigest(byte[]? messageDigest, byte[] content, CipherSuite suite)
    {
        if (messageDigest is null)
            throw new FileSecurityException(Core.Protocol.AnswerReasonCodes.InvalidFileSignature,
                "The signature carries no digest of the content.");

        if (!CryptographicOperations.FixedTimeEquals(messageDigest, suite.ComputeHash(content)))
            throw new FileSecurityException(Core.Protocol.AnswerReasonCodes.InvalidFileSignature,
                "The digest of the signature does not belong to the content.");
    }

    /// <summary>Wraps the content in a CMS EnvelopedData for the certificate of the recipient.</summary>
    public static byte[] Encrypt(byte[] content, X509Certificate2 certificate, CipherSuite suite)
    {
        using var key = certificate.GetRSAPublicKey()
            ?? throw new FileSecurityException($"Certificate '{certificate.Subject}' has no RSA public key.");

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateKey();
        aes.GenerateIV();

        var encryptedContent = aes.EncryptCbc(content, aes.IV);
        var encryptedKey = key.Encrypt(aes.Key, suite.EncryptionPadding);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(EnvelopedData);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                // Version 0: key transport recipients identified by issuer and serial number, no attributes.
                writer.WriteInteger(0);

                using (writer.PushSetOf())
                using (writer.PushSequence())
                {
                    writer.WriteInteger(0);
                    WriteIssuerAndSerialNumber(writer, certificate);
                    WriteKeyEncryptionAlgorithm(writer, suite);
                    writer.WriteOctetString(encryptedKey);
                }

                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(Data);
                    using (writer.PushSequence())
                    {
                        writer.WriteObjectIdentifier(Aes256Cbc);
                        writer.WriteOctetString(aes.IV);
                    }

                    writer.WriteOctetString(encryptedContent, new Asn1Tag(TagClass.ContextSpecific, 0));
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>Unwraps a package written by <see cref="Encrypt"/> with our own private key.</summary>
    public static byte[] Decrypt(byte[] envelope, X509Certificate2 certificate, CipherSuite suite)
    {
        using var key = certificate.GetRSAPrivateKey()
            ?? throw new FileSecurityException(Core.Protocol.AnswerReasonCodes.FileDecryptionFailure,
                $"Certificate '{certificate.Subject}' has no RSA private key to decrypt with.");

        try
        {
            var reader = new AsnReader(envelope, AsnEncodingRules.BER).ReadSequence();
            if (reader.ReadObjectIdentifier() != EnvelopedData)
                throw new FileSecurityException(Core.Protocol.AnswerReasonCodes.FileDecryptionFailure,
                    "The content is not a CMS EnvelopedData package.");

            var enveloped = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            enveloped.ReadInteger();

            var recipients = enveloped.ReadSetOf();
            byte[]? contentKey = null;
            while (recipients.HasData && contentKey is null)
            {
                var recipient = recipients.ReadSequence();
                recipient.ReadInteger();
                // The recipient is identified by issuer and serial number; we only have one certificate.
                recipient.ReadEncodedValue();
                recipient.ReadSequence();
                var encryptedKey = recipient.ReadOctetString();

                try
                {
                    contentKey = key.Decrypt(encryptedKey, suite.EncryptionPadding);
                }
                catch (CryptographicException)
                {
                    // Not our recipient info, try the next one.
                }
            }

            if (contentKey is null)
                throw new FileSecurityException(Core.Protocol.AnswerReasonCodes.FileDecryptionFailure,
                    $"The package is not encrypted for '{certificate.Subject}'.");

            var encryptedContentInfo = enveloped.ReadSequence();
            encryptedContentInfo.ReadObjectIdentifier();

            var algorithm = encryptedContentInfo.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != Aes256Cbc)
                throw new FileSecurityException(Core.Protocol.AnswerReasonCodes.CipherSuiteNotSupported,
                    "The content is not encrypted with AES-256-CBC.");

            var iv = algorithm.ReadOctetString();
            var encryptedContent = encryptedContentInfo.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));

            using var aes = Aes.Create();
            aes.Key = contentKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            return aes.DecryptCbc(encryptedContent, iv);
        }
        catch (AsnContentException ex)
        {
            throw new FileSecurityException(Core.Protocol.AnswerReasonCodes.FileDecryptionFailure,
                "The encrypted content could not be read: " + ex.Message, ex);
        }
        catch (CryptographicException ex)
        {
            throw new FileSecurityException(Core.Protocol.AnswerReasonCodes.FileDecryptionFailure,
                "The content could not be decrypted: " + ex.Message, ex);
        }
    }

    /// <summary>RSAES-OAEP with MGF1, both with the hash of the suite (RFC 4055, section 4.1).</summary>
    private static void WriteKeyEncryptionAlgorithm(AsnWriter writer, CipherSuite suite)
    {
        var hash = suite.DigestAlgorithm.Value!;

        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(RsaesOaep);
            using (writer.PushSequence())
            {
                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                using (writer.PushSequence())
                    writer.WriteObjectIdentifier(hash);

                using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 1)))
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(Mgf1);
                    using (writer.PushSequence())
                        writer.WriteObjectIdentifier(hash);
                }
            }
        }
    }

    private static void WriteIssuerAndSerialNumber(AsnWriter writer, X509Certificate2 certificate)
    {
        using (writer.PushSequence())
        {
            writer.WriteEncodedValue(certificate.IssuerName.RawData);
            writer.WriteInteger(new BigInteger(certificate.SerialNumberBytes.Span, isUnsigned: true, isBigEndian: true));
        }
    }
}
