using System.Security.Cryptography.X509Certificates;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;

namespace Oftp4Net.Services.Import;

/// <summary>
/// A certificate OS4X trusts for TLS that was added by hand (not from the Odette trust list, which Oftp4Net reads
/// itself): a certificate of a partner or a CA, trusted there for all partners.
/// </summary>
public sealed class Os4xCertificateCandidate
{
    public required string Subject { get; init; }
    public required string Name { get; init; }
    public DateTime ValidFrom { get; init; }
    public DateTime ValidTo { get; init; }

    /// <summary>The certificate in DER, base64.</summary>
    public required string Base64Data { get; init; }

    /// <summary>The same certificate is here already, the import leaves it alone.</summary>
    public bool AlreadyExists { get; init; }

    public bool Expired { get; init; }
    public List<string> Notes { get; init; } = [];
    public bool CanImport => !AlreadyExists;

    /// <summary>Selected for import in the user interface; expired certificates are not by default.</summary>
    public bool Selected { get; set; }

    public string Status => AlreadyExists ? "already exists" : Expired ? "expired" : "new";

    /// <summary>
    /// What the import would do with the PEM certificates of OS4X, given the certificates that are here already.
    /// A certificate that cannot be read is left out.
    /// </summary>
    public static List<Os4xCertificateCandidate> Plan(IEnumerable<string> pems, IEnumerable<Certificate> existing, DateTime now)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var certificate in existing)
        {
            try
            {
                using var loaded = CertificateLoader.Load(certificate);
                known.Add(loaded.Thumbprint);
            }
            catch (Exception)
            {
                // A certificate here that cannot be loaded cannot be a duplicate either.
            }
        }

        var candidates = new List<Os4xCertificateCandidate>();
        foreach (var pem in pems)
        {
            X509Certificate2 certificate;
            try
            {
                certificate = X509Certificate2.CreateFromPem(pem);
            }
            catch (Exception)
            {
                continue;
            }

            using (certificate)
            {
                var duplicate = !known.Add(certificate.Thumbprint);
                var expired = certificate.NotAfter < now;
                var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                var candidate = new Os4xCertificateCandidate
                {
                    Subject = certificate.Subject,
                    Name = string.IsNullOrWhiteSpace(name) ? certificate.Subject : name,
                    ValidFrom = certificate.NotBefore,
                    ValidTo = certificate.NotAfter,
                    Base64Data = Convert.ToBase64String(certificate.RawData),
                    AlreadyExists = duplicate,
                    Expired = expired,
                };
                if (duplicate)
                    candidate.Notes.Add("The certificate is here already; mark it as trusted for TLS of all partners when it should be.");
                if (expired)
                    candidate.Notes.Add($"Expired on {certificate.NotAfter:d}.");
                candidate.Selected = candidate.CanImport && !expired;
                candidates.Add(candidate);
            }
        }

        return candidates;
    }
}
