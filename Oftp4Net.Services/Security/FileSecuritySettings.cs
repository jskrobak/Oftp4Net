using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Core.Protocol;

namespace Oftp4Net.Services.Security;

/// <summary>What is applied to the content of a virtual file sent to a partner.</summary>
public sealed class FileSecuritySettings
{
    public bool Sign { get; init; }
    public bool Compress { get; init; }
    public bool Encrypt { get; init; }

    /// <summary>Suite used for signing, encryption and the hash of the file.</summary>
    public CipherSuite Suite { get; init; } = CipherSuite.Default;

    /// <summary>Our certificate with the private key, used for signing.</summary>
    public X509Certificate2? SigningCertificate { get; init; }

    /// <summary>The partner's certificate the file is encrypted for.</summary>
    public X509Certificate2? EncryptionCertificate { get; init; }

    public bool Any => Sign || Compress || Encrypt;

    /// <summary>Value of SFIDSEC.</summary>
    public string SecurityLevel => (Encrypt, Sign) switch
    {
        (true, true) => SecurityLevels.EncryptedAndSigned,
        (true, false) => SecurityLevels.Encrypted,
        (false, true) => SecurityLevels.Signed,
        _ => SecurityLevels.None,
    };

    /// <summary>Value of SFIDCOMP.</summary>
    public string Compression => Compress ? FileCompressionAlgorithms.Zlib : FileCompressionAlgorithms.None;

    /// <summary>Value of SFIDENV: the content is a CMS package whenever anything was applied to it.</summary>
    public string Enveloping => Any ? FileEnvelopingFormats.Cms : FileEnvelopingFormats.None;
}

/// <summary>What was applied to the content of a received virtual file, as announced in its SFID.</summary>
public sealed class FileSecurityDescriptor
{
    public required string SecurityLevel { get; init; }
    public required string Compression { get; init; }
    public required string Enveloping { get; init; }
    public required string CipherSuiteCode { get; init; }

    public bool Encrypted => SecurityLevel is SecurityLevels.Encrypted or SecurityLevels.EncryptedAndSigned;
    public bool Signed => SecurityLevel is SecurityLevels.Signed or SecurityLevels.EncryptedAndSigned;
    public bool Compressed => Compression == FileCompressionAlgorithms.Zlib;

    /// <summary>The content is secured, compressed or otherwise wrapped and has to be unpacked before it is stored.</summary>
    public bool Any => Encrypted || Signed || Compressed || Enveloping == FileEnvelopingFormats.Cms;

    public static FileSecurityDescriptor From(Core.Protocol.Commands.SFID header) => new()
    {
        SecurityLevel = header.SecurityLevel,
        Compression = header.Compression,
        Enveloping = header.Enveloping,
        CipherSuiteCode = header.CipherSuite,
    };
}
