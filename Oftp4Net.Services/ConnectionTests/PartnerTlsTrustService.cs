using System.Security.Cryptography.X509Certificates;
using Havit.Services.TimeServices;
using Microsoft.Extensions.Logging;
using Oftp4Net.Domain;

namespace Oftp4Net.Services.ConnectionTests;

/// <summary>
/// Settles a TLS certificate of a partner that a connection test refused, from the page of the tests: the certificate
/// the partner presented is trusted (pinned), or the partner is set to accept an invalid certificate. Works with the
/// partner of the caller's scope, so that the page sees the change without loading the partner again.
/// </summary>
public sealed class PartnerTlsTrustService(
    IDataService dataService,
    ListenerService listenerService,
    ITimeService timeService,
    ILogger<PartnerTlsTrustService> logger)
{
    /// <summary>
    /// Makes the certificate the partner presented its trusted certificate. A pinned certificate is accepted by
    /// itself, regardless of the address that was called and of its chain, as long as it is valid; an identical
    /// certificate that is stored already is reused. Accepting an invalid certificate is turned off, the pinned one
    /// is what has to be presented now.
    /// </summary>
    public async Task<Certificate> TrustPresentedCertificateAsync(Partner partner, byte[] presented, string? user)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(presented);
        var now = timeService.GetCurrentTime();
        if (certificate.NotBefore > now || certificate.NotAfter < now)
            throw new InvalidOperationException(
                $"The certificate is valid from {certificate.NotBefore:d} to {certificate.NotAfter:d}, not today; a pinned certificate has to be valid.");

        var data = Convert.ToBase64String(certificate.RawData);
        var all = await dataService.GetAllCertificatesAsync();
        var previous = all.FirstOrDefault(c => c.Id == partner.TrustedCertificateId)?.Name;
        var stored = all.FirstOrDefault(c => !c.HasPrivateKey && c.Base64Data == data);
        if (stored is null)
        {
            stored = new Certificate
            {
                Name = Name(partner, certificate),
                Base64Data = data,
                ValidFrom = certificate.NotBefore,
                ValidTo = certificate.NotAfter,
            };
            // The identifier is needed to assign the certificate to the partner.
            await dataService.SaveCertificateAsync(stored);
        }

        partner.TrustedCertificateId = stored.Id;
        partner.TrustedCertificate = stored;
        partner.AcceptInvalidTlsCertificate = false;
        await dataService.SavePartnerAsync(partner);
        await listenerService.RefreshTrustedCertificatesAsync();

        logger.LogInformation("{User} trusted the TLS certificate {Certificate} of partner {Partner} (before: {Previous})",
            user ?? "unknown", certificate.Subject, partner.Name, previous ?? "system trust store");
        return stored;
    }

    /// <summary>Lets the partner be called although its certificate is not valid (as OS4X does).</summary>
    public async Task AcceptInvalidCertificateAsync(Partner partner, string? user)
    {
        partner.AcceptInvalidTlsCertificate = true;
        await dataService.SavePartnerAsync(partner);

        logger.LogInformation("{User} set partner {Partner} to accept an invalid TLS certificate", user ?? "unknown", partner.Name);
    }

    private static string Name(Partner partner, X509Certificate2 certificate)
    {
        var common = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var name = $"{partner.Name} {(string.IsNullOrEmpty(common) ? certificate.Subject : common)} " +
                   $"(TLS, valid to {certificate.NotAfter:yyyy-MM-dd})";
        return name.Length <= 200 ? name : name[..200];
    }
}
