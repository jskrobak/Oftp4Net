using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Core.Protocol;

namespace Oftp4Net.Services.Security;

/// <summary>
/// File level security of RFC 5024, section 6: the content of a virtual file is signed, compressed and
/// encrypted in this order (and unpacked in the reverse order), each step producing a CMS package.
/// Everything is done in memory, the caller limits the size of the files it offers here.
/// </summary>
public static class FileSecurity
{
    /// <summary>Applies signing, compression and encryption to the content of a file that is about to be sent.</summary>
    public static byte[] Protect(byte[] content, FileSecuritySettings settings)
    {
        var result = content;

        if (settings.Sign)
        {
            if (settings.SigningCertificate is null)
                throw new FileSecurityException("No certificate with a private key is configured for signing files.");
            result = Sign(result, settings.SigningCertificate, settings.Suite);
        }

        if (settings.Compress)
            result = CmsCompression.Compress(result);

        if (settings.Encrypt)
        {
            if (settings.EncryptionCertificate is null)
                throw new FileSecurityException("The partner has no certificate configured to encrypt files for.");
            result = Encrypt(result, settings.EncryptionCertificate, settings.Suite);
        }

        return result;
    }

    /// <summary>
    /// Removes encryption, compression and the signature from the content of a received file.
    /// <paramref name="decryptionCertificate"/> is our certificate with the private key the partner encrypted for,
    /// <paramref name="partnerCertificate"/> the partner's certificate the signature is verified against.
    /// </summary>
    public static byte[] Unprotect(byte[] content, FileSecurityDescriptor descriptor,
        X509Certificate2? decryptionCertificate, X509Certificate2? partnerCertificate) =>
        Unprotect(content, descriptor, decryptionCertificate, partnerCertificate, strictSignature: true, out _);

    /// <summary>
    /// Like <see cref="Unprotect(byte[], FileSecurityDescriptor, X509Certificate2?, X509Certificate2?)"/>, but a
    /// signature that cannot be verified does not fail: the signed content is returned and
    /// <paramref name="signatureProblem"/> says what is wrong with the signature. Used where the content decides
    /// itself how much it trusts an unverified sender, e.g. a datasheet signed with a new certificate.
    /// </summary>
    public static byte[] Unprotect(byte[] content, FileSecurityDescriptor descriptor,
        X509Certificate2? decryptionCertificate, X509Certificate2? partnerCertificate, out string? signatureProblem) =>
        Unprotect(content, descriptor, decryptionCertificate, partnerCertificate, strictSignature: false, out signatureProblem);

    private static byte[] Unprotect(byte[] content, FileSecurityDescriptor descriptor,
        X509Certificate2? decryptionCertificate, X509Certificate2? partnerCertificate, bool strictSignature,
        out string? signatureProblem)
    {
        signatureProblem = null;
        var suite = CipherSuite.Get(descriptor.CipherSuiteCode);
        if ((descriptor.Encrypted || descriptor.Signed) && suite is null)
            throw new FileSecurityException(AnswerReasonCodes.CipherSuiteNotSupported,
                $"Cipher suite '{descriptor.CipherSuiteCode}' is not supported.");

        var result = content;

        if (descriptor.Encrypted)
        {
            if (decryptionCertificate is null)
                throw new FileSecurityException(AnswerReasonCodes.FileDecryptionFailure,
                    "No certificate with a private key is configured to decrypt files with.");
            result = Decrypt(result, decryptionCertificate);
        }

        if (descriptor.Compressed)
        {
            try
            {
                result = CmsCompression.Decompress(result);
            }
            catch (FileSecurityException ex)
            {
                throw new FileSecurityException(AnswerReasonCodes.FileDecompressionFailure, ex.Message, ex);
            }
        }

        if (descriptor.Signed)
        {
            try
            {
                if (partnerCertificate is null)
                    throw new FileSecurityException(AnswerReasonCodes.InvalidFileSignature,
                        "The partner has no certificate configured to verify the file signature with.");
                result = VerifySignature(result, partnerCertificate);
            }
            catch (FileSecurityException ex) when (!strictSignature)
            {
                signatureProblem = ex.Message;
                result = GetSignedContent(result);
            }
        }

        return result;
    }

    /// <summary>Hash of the transferred content used in EERPHSH / NERPHSH.</summary>
    public static byte[] ComputeHash(byte[] content, CipherSuite suite) => suite.ComputeHash(content);

    /// <summary>Creates the CMS SignedData of an End to End Response (EERPSIG / NERPSIG).</summary>
    public static byte[] SignEndResponse(byte[] signedContent, X509Certificate2 certificate, CipherSuite suite) =>
        Sign(signedContent, certificate, suite);

    /// <summary>
    /// Verifies the signature of an End to End Response: the signature must be valid, made by
    /// <paramref name="partnerCertificate"/> and cover exactly <paramref name="expectedContent"/>.
    /// </summary>
    public static void VerifyEndResponse(byte[] signature, byte[] expectedContent, X509Certificate2 partnerCertificate)
    {
        var content = VerifySignature(signature, partnerCertificate);

        if (!CryptographicOperations.FixedTimeEquals(content, expectedContent))
            throw new FileSecurityException("The signature of the end response does not match its content.");
    }

    /// <summary>
    /// Secure authentication: encrypts the challenge as a CMS envelope for the peer's certificate
    /// (RFC 5024, section 5.3.17).
    /// </summary>
    public static byte[] EncryptChallenge(byte[] challenge, X509Certificate2 certificate, CipherSuite suite) =>
        Encrypt(challenge, certificate, suite);

    /// <summary>Secure authentication: decrypts a challenge received from the peer with our own private key.</summary>
    public static byte[] DecryptChallenge(byte[] challenge, X509Certificate2 certificate) =>
        Decrypt(challenge, certificate);

    private static byte[] Sign(byte[] content, X509Certificate2 certificate, CipherSuite suite)
    {
        if (!certificate.HasPrivateKey)
            throw new FileSecurityException($"Certificate '{certificate.Subject}' has no private key to sign with.");

        var cms = new SignedCms(new ContentInfo(content), detached: false);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, certificate,
            privateKey: null, suite.SignaturePadding)
        {
            DigestAlgorithm = suite.DigestAlgorithm,
            IncludeOption = X509IncludeOption.EndCertOnly,
        };

        try
        {
            cms.ComputeSignature(signer, silent: true);
        }
        catch (CryptographicException ex)
        {
            throw new FileSecurityException("Signing failed: " + ex.Message, ex);
        }

        return cms.Encode();
    }

    /// <summary>Verifies an attached CMS SignedData and returns the signed content.</summary>
    private static byte[] VerifySignature(byte[] content, X509Certificate2 partnerCertificate)
    {
        var cms = new SignedCms();
        try
        {
            cms.Decode(content);
        }
        catch (CryptographicException ex)
        {
            throw new FileSecurityException(AnswerReasonCodes.InvalidFileSignature,
                "The signed content is not a valid CMS package: " + ex.Message, ex);
        }

        try
        {
            // The certificate is verified against the one configured for the partner, not against a trust chain:
            // OFTP partners commonly exchange self signed certificates.
            cms.CheckSignature(new X509Certificate2Collection(partnerCertificate), verifySignatureOnly: true);
        }
        catch (CryptographicException ex)
        {
            throw new FileSecurityException(AnswerReasonCodes.InvalidFileSignature,
                "The signature is not valid: " + ex.Message, ex);
        }

        if (!cms.SignerInfos.Cast<SignerInfo>().Any(s => partnerCertificate.Equals(s.Certificate)))
            throw new FileSecurityException(AnswerReasonCodes.InvalidFileSignature,
                $"The content is not signed by the certificate configured for the partner ('{partnerCertificate.Subject}').");

        return cms.ContentInfo.Content;
    }

    /// <summary>The content of an attached CMS SignedData, without verifying the signature.</summary>
    private static byte[] GetSignedContent(byte[] content)
    {
        var cms = new SignedCms();
        try
        {
            cms.Decode(content);
        }
        catch (CryptographicException ex)
        {
            throw new FileSecurityException(AnswerReasonCodes.InvalidFileSignature,
                "The signed content is not a valid CMS package: " + ex.Message, ex);
        }

        return cms.ContentInfo.Content;
    }

    private static byte[] Encrypt(byte[] content, X509Certificate2 certificate, CipherSuite suite)
    {
        var enveloped = new EnvelopedCms(new ContentInfo(content), new AlgorithmIdentifier(suite.SymmetricAlgorithm));
        try
        {
            enveloped.Encrypt(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, certificate,
                suite.EncryptionPadding));
        }
        catch (CryptographicException ex)
        {
            throw new FileSecurityException("Encryption failed: " + ex.Message, ex);
        }

        return enveloped.Encode();
    }

    private static byte[] Decrypt(byte[] content, X509Certificate2 certificate)
    {
        if (!certificate.HasPrivateKey)
            throw new FileSecurityException(AnswerReasonCodes.FileDecryptionFailure,
                $"Certificate '{certificate.Subject}' has no private key to decrypt with.");

        var enveloped = new EnvelopedCms();
        try
        {
            enveloped.Decode(content);
            enveloped.Decrypt(new X509Certificate2Collection(certificate));
        }
        catch (CryptographicException ex)
        {
            throw new FileSecurityException(AnswerReasonCodes.FileDecryptionFailure,
                "The content could not be decrypted: " + ex.Message, ex);
        }

        return enveloped.ContentInfo.Content;
    }
}
