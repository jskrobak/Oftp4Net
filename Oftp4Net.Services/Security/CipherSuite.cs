using System.Security.Cryptography;
using Oftp4Net.Core.Protocol;

namespace Oftp4Net.Services.Security;

/// <summary>
/// Cipher suite of file level security (SFIDCIPH). Suites 01 and 02 are mandatory in RFC 5024, section 10.2,
/// the rest are the commonly used extensions with stronger hashes.
/// </summary>
public sealed record CipherSuite(string Code, string Name, HashAlgorithmName HashAlgorithm, Oid SymmetricAlgorithm)
{
    /// <summary>3DES-EDE-CBC, OID 1.2.840.113549.3.7.</summary>
    private const string TripleDesCbc = "1.2.840.113549.3.7";

    /// <summary>AES-256-CBC, OID 2.16.840.1.101.3.4.1.42.</summary>
    private const string Aes256Cbc = "2.16.840.1.101.3.4.1.42";

    public static readonly IReadOnlyList<CipherSuite> All =
    [
        new(CipherSuites.TripleDesSha1, "3DES-EDE-CBC, RSA, SHA-1", HashAlgorithmName.SHA1, new Oid(TripleDesCbc)),
        new(CipherSuites.Aes256Sha1, "AES-256-CBC, RSA, SHA-1", HashAlgorithmName.SHA1, new Oid(Aes256Cbc)),
        new(CipherSuites.TripleDesSha256, "3DES-EDE-CBC, RSA, SHA-256", HashAlgorithmName.SHA256, new Oid(TripleDesCbc)),
        new(CipherSuites.Aes256Sha256, "AES-256-CBC, RSA, SHA-256", HashAlgorithmName.SHA256, new Oid(Aes256Cbc)),
        new(CipherSuites.TripleDesSha512, "3DES-EDE-CBC, RSA, SHA-512", HashAlgorithmName.SHA512, new Oid(TripleDesCbc)),
        new(CipherSuites.Aes256Sha512, "AES-256-CBC, RSA, SHA-512", HashAlgorithmName.SHA512, new Oid(Aes256Cbc)),
    ];

    /// <summary>The suite recommended when nothing else is agreed with the partner.</summary>
    public static CipherSuite Default => Get(CipherSuites.Aes256Sha256)!;

    public static CipherSuite? Get(string? code) =>
        string.IsNullOrEmpty(code) ? null : All.FirstOrDefault(s => s.Code == code);

    public byte[] ComputeHash(byte[] data) => HashAlgorithm.Name switch
    {
        nameof(HashAlgorithmName.SHA1) => SHA1.HashData(data),
        nameof(HashAlgorithmName.SHA256) => SHA256.HashData(data),
        nameof(HashAlgorithmName.SHA512) => SHA512.HashData(data),
        _ => throw new NotSupportedException($"Hash algorithm {HashAlgorithm.Name} is not supported.")
    };

    public async Task<byte[]> ComputeHashAsync(Stream content, CancellationToken cancellationToken) => HashAlgorithm.Name switch
    {
        nameof(HashAlgorithmName.SHA1) => await SHA1.HashDataAsync(content, cancellationToken),
        nameof(HashAlgorithmName.SHA256) => await SHA256.HashDataAsync(content, cancellationToken),
        nameof(HashAlgorithmName.SHA512) => await SHA512.HashDataAsync(content, cancellationToken),
        _ => throw new NotSupportedException($"Hash algorithm {HashAlgorithm.Name} is not supported.")
    };

    public override string ToString() => $"{Code} ({Name})";
}
