using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Core.Protocol;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Tests;

public class FileSecurityTests
{
    private static readonly X509Certificate2 Ours = CreateCertificate("CN=Us");
    private static readonly X509Certificate2 Partner = CreateCertificate("CN=Partner");
    private static readonly byte[] Content = "UNB+UNOA:2+SENDER+RECEIVER+260922:1200+1'"u8.ToArray();

    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // On Windows the private key has to be persisted through an export to be usable for CMS operations.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.Exportable);
    }

    private static FileSecurityDescriptor Descriptor(FileSecuritySettings settings) => new()
    {
        SecurityLevel = settings.SecurityLevel,
        Compression = settings.Compression,
        Enveloping = settings.Enveloping,
        CipherSuiteCode = settings.Suite.Code,
    };

    public static TheoryData<bool, bool, bool> Combinations()
    {
        var data = new TheoryData<bool, bool, bool>();
        foreach (var sign in new[] { false, true })
        foreach (var compress in new[] { false, true })
        foreach (var encrypt in new[] { false, true })
            data.Add(sign, compress, encrypt);
        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void ProtectedContentIsRestored(bool sign, bool compress, bool encrypt)
    {
        var settings = new FileSecuritySettings
        {
            Sign = sign,
            Compress = compress,
            Encrypt = encrypt,
            Suite = CipherSuite.Default,
            SigningCertificate = Ours,
            EncryptionCertificate = Partner,
        };

        var protectedContent = FileSecurity.Protect(Content, settings);

        Assert.Equal(sign || compress || encrypt, !protectedContent.SequenceEqual(Content));
        // From the partner's point of view: it decrypts with its own key and verifies our certificate.
        Assert.Equal(Content, FileSecurity.Unprotect(protectedContent, Descriptor(settings), Partner, Ours));
    }

    [Theory]
    [InlineData(CipherSuites.TripleDesSha1)]
    [InlineData(CipherSuites.Aes256Sha1)]
    [InlineData(CipherSuites.TripleDesSha256)]
    [InlineData(CipherSuites.Aes256Sha256)]
    [InlineData(CipherSuites.TripleDesSha512)]
    [InlineData(CipherSuites.Aes256Sha512)]
    [InlineData(CipherSuites.Aes256Sha3512)]
    [InlineData(CipherSuites.Aes256PssOaepSha256)]
    [InlineData(CipherSuites.Aes256PssOaepSha512)]
    [InlineData(CipherSuites.Aes256PssOaepSha3512)]
    public void EveryCipherSuiteSignsAndEncrypts(string code)
    {
        // SHA3 is provided by the platform only on some systems (not on macOS).
        if (CipherSuite.Get(code) is null)
        {
            Assert.False(CipherSuite.All.Single(s => s.Code == code).IsSupported);
            return;
        }

        var settings = new FileSecuritySettings
        {
            Sign = true,
            Encrypt = true,
            Suite = CipherSuite.Get(code)!,
            SigningCertificate = Ours,
            EncryptionCertificate = Partner,
        };

        var protectedContent = FileSecurity.Protect(Content, settings);

        Assert.Equal(Content, FileSecurity.Unprotect(protectedContent, Descriptor(settings), Partner, Ours));
    }

    [Fact]
    public void CompressionMakesRepetitiveContentSmaller()
    {
        var content = new byte[100_000];
        Array.Fill(content, (byte)'A');

        var compressed = CmsCompression.Compress(content);

        Assert.True(compressed.Length < 1000, $"Compressed to {compressed.Length} bytes.");
        Assert.Equal(content, CmsCompression.Decompress(compressed));
    }

    [Fact]
    public void UnverifiedSignatureCanBeAccepted()
    {
        var settings = new FileSecuritySettings { Sign = true, Suite = CipherSuite.Default, SigningCertificate = Ours };
        var signed = FileSecurity.Protect(Content, settings);
        var stranger = CreateCertificate("CN=Stranger");

        var content = FileSecurity.Unprotect(signed, Descriptor(settings), null, stranger, out var problem);

        Assert.Equal(Content, content);
        Assert.NotNull(problem);

        Assert.Equal(Content, FileSecurity.Unprotect(signed, Descriptor(settings), null, Ours, out problem));
        Assert.Null(problem);
    }

    [Fact]
    public void SignatureOfAnotherCertificateIsRefused()
    {
        var settings = new FileSecuritySettings { Sign = true, SigningCertificate = Ours };
        var signed = FileSecurity.Protect(Content, settings);
        var stranger = CreateCertificate("CN=Stranger");

        var exception = Assert.Throws<FileSecurityException>(
            () => FileSecurity.Unprotect(signed, Descriptor(settings), null, stranger));

        Assert.Equal(AnswerReasonCodes.InvalidFileSignature, exception.ReasonCode);
    }

    [Fact]
    public void ContentEncryptedForSomebodyElseCannotBeDecrypted()
    {
        var settings = new FileSecuritySettings { Encrypt = true, EncryptionCertificate = Partner };
        var encrypted = FileSecurity.Protect(Content, settings);

        var exception = Assert.Throws<FileSecurityException>(
            () => FileSecurity.Unprotect(encrypted, Descriptor(settings), Ours, null));

        Assert.Equal(AnswerReasonCodes.FileDecryptionFailure, exception.ReasonCode);
    }

    [Fact]
    public void DamagedContentIsRefused()
    {
        var settings = new FileSecuritySettings { Compress = true };
        var compressed = FileSecurity.Protect(Content, settings);
        compressed[^1] ^= 0xFF;

        var exception = Assert.Throws<FileSecurityException>(
            () => FileSecurity.Unprotect(compressed, Descriptor(settings), null, null));

        Assert.Equal(AnswerReasonCodes.FileDecompressionFailure, exception.ReasonCode);
    }

    [Fact]
    public void UnknownCipherSuiteIsReported()
    {
        var descriptor = new FileSecurityDescriptor
        {
            SecurityLevel = SecurityLevels.Encrypted,
            Compression = FileCompressionAlgorithms.None,
            Enveloping = FileEnvelopingFormats.Cms,
            CipherSuiteCode = "42",
        };

        var exception = Assert.Throws<FileSecurityException>(() => FileSecurity.Unprotect(Content, descriptor, Ours, null));

        Assert.Equal(AnswerReasonCodes.CipherSuiteNotSupported, exception.ReasonCode);
    }

    [Theory]
    [InlineData(CipherSuites.Aes256PssOaepSha256)]
    [InlineData(CipherSuites.Aes256PssOaepSha512)]
    [InlineData(CipherSuites.Aes256PssOaepSha3512)]
    public void NewerSuitesUsePssSignaturesAndOaepKeyTransport(string code)
    {
        if (CipherSuite.Get(code) is not { } suite)
            return;

        var signed = FileSecurity.Protect(Content, new FileSecuritySettings
        {
            Sign = true, Suite = suite, SigningCertificate = Ours,
        });

        var signedCms = new SignedCms();
        signedCms.Decode(signed);
        // RSASSA-PSS, 1.2.840.113549.1.1.10.
        Assert.Equal("1.2.840.113549.1.1.10", signedCms.SignerInfos[0].SignatureAlgorithm.Value);

        var encrypted = FileSecurity.Protect(Content, new FileSecuritySettings
        {
            Encrypt = true, Suite = suite, EncryptionCertificate = Partner,
        });

        var envelopedCms = new EnvelopedCms();
        envelopedCms.Decode(encrypted);
        // RSAES-OAEP, 1.2.840.113549.1.1.7.
        Assert.Equal("1.2.840.113549.1.1.7", envelopedCms.RecipientInfos[0].KeyEncryptionAlgorithm.Oid.Value);
    }

    [Fact]
    public void Sha3PackagesAreSignedEncryptedAndCheckedByOurOwnCode()
    {
        if (CipherSuite.Get(CipherSuites.Aes256PssOaepSha3512) is not { } suite)
            return;

        Assert.True(Sha3Cms.IsHandled(suite));

        var signed = Sha3Cms.Sign(Content, Ours, suite);

        // The package is a CMS SignedData with an RSASSA-PSS signature over the included content.
        var cms = new SignedCms();
        cms.Decode(signed);
        Assert.Equal("1.2.840.113549.1.1.10", cms.SignerInfos[0].SignatureAlgorithm.Value);
        Assert.Equal(Content, cms.ContentInfo.Content);

        Assert.Equal(Content, Sha3Cms.Verify(signed, Ours, suite));
        // Another certificate does not verify it.
        Assert.Throws<FileSecurityException>(() => Sha3Cms.Verify(signed, Partner, suite));

        // A changed content is caught by the digest in the signed attributes.
        var tampered = (byte[])signed.Clone();
        tampered[^1] ^= 0xFF;
        Assert.Throws<FileSecurityException>(() => Sha3Cms.Verify(tampered, Ours, suite));

        // The envelope is written and read here as well.
        var envelope = Sha3Cms.Encrypt(Content, Partner, suite);
        var envelopedCms = new EnvelopedCms();
        envelopedCms.Decode(envelope);
        Assert.Equal("1.2.840.113549.1.1.7", envelopedCms.RecipientInfos[0].KeyEncryptionAlgorithm.Oid.Value);
        Assert.Equal(Content, Sha3Cms.Decrypt(envelope, Partner, suite));
        Assert.Throws<FileSecurityException>(() => Sha3Cms.Decrypt(envelope, Ours, suite));
    }

    [Fact]
    public void AuthenticationChallengeIsEncryptedForTheOtherSide()
    {
        var challenge = Oftp4Net.Core.Protocol.SecureAuthentication.CreateChallenge();

        // We challenge the partner: only the holder of the partner's private key can answer.
        var envelope = FileSecurity.EncryptChallenge(challenge, Partner, CipherSuite.Default);

        Assert.NotEqual(challenge, envelope);
        Assert.Equal(challenge, FileSecurity.DecryptChallenge(envelope, Partner));
        Assert.Throws<FileSecurityException>(() => FileSecurity.DecryptChallenge(envelope, Ours));
    }

    public static TheoryData<string> SupportedSuites()
    {
        var data = new TheoryData<string>();
        foreach (var suite in CipherSuite.Supported)
            data.Add(suite.Code);
        return data;
    }

    /// <summary>
    /// A challenge carries no cipher suite, so the partner may use any of them (Odette test case 5.2 requires
    /// PKCS#1 v1.5 as well as v2.2). What .NET cannot read is unwrapped by hand.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedSuites))]
    public void AuthenticationChallengeIsDecryptedWithoutKnowingTheCipherSuite(string code)
    {
        var suite = CipherSuite.Get(code)!;
        var challenge = Oftp4Net.Core.Protocol.SecureAuthentication.CreateChallenge();

        var envelope = FileSecurity.EncryptChallenge(challenge, Partner, suite);

        Assert.Equal(challenge, FileSecurity.DecryptChallenge(envelope, Partner));
        Assert.Throws<FileSecurityException>(() => FileSecurity.DecryptChallenge(envelope, Ours));
    }

    [Fact]
    public void EndResponseSignatureIsVerifiedAgainstItsContent()
    {
        var content = "EERP content"u8.ToArray();

        var signature = FileSecurity.SignEndResponse(content, Ours, CipherSuite.Default);

        FileSecurity.VerifyEndResponse(signature, content, Ours);
        Assert.Throws<FileSecurityException>(() => FileSecurity.VerifyEndResponse(signature, "other"u8.ToArray(), Ours));
        Assert.Throws<FileSecurityException>(() => FileSecurity.VerifyEndResponse(signature, content, Partner));
    }
}
