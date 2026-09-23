using System.Security.Cryptography.X509Certificates;
using Havit.Data.Patterns.UnitOfWorks;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services.Pdx;

/// <summary>Our datasheet, written and checked against the schema.</summary>
public sealed record PdxExport(byte[] Content, PdxDocument Document, IReadOnlyList<string> Warnings)
{
    public string FileName => $"OftpCommunicationSetup_{Document.Session.Ssid}.xml";
}

/// <summary>
/// Creates our own OFTP2 Communication Setup (PDX) for an identity, to be downloaded (e.g. sent by e-mail) or sent to
/// a partner over OFTP (Odette OP08 part 3, "Group 1": creating and sending datasheets).
/// </summary>
public class PdxExporter(
    IIdentityRepository identities,
    IListenerRepository listeners,
    ICertificateRepository certificates,
    IUnitOfWork unitOfWork,
    GlobalSettingsService settingsService,
    OutboxStorage outbox,
    TslService tsl,
    ILogger<PdxExporter> logger)
{
    public async Task<PdxExport> ExportAsync(int identityId, DateTimeOffset? validFrom = null, CancellationToken cancellationToken = default)
    {
        var identity = await identities.GetObjectAsync(identityId, cancellationToken);
        var settings = await settingsService.GetGlobalSettingsAsync();
        var profile = settings.StationProfile;

        var subStations = (await identities.GetAllAsync(cancellationToken))
            .Where(i => i.Id != identity.Id && SameCode(i.SSID, identity.SSID) && !SameCode(i.SFID, i.SSID))
            .ToList();

        var listener = profile.ListenerId is { } listenerId
            ? (await listeners.GetEnabledWithRefsAsync(cancellationToken)).FirstOrDefault(l => l.Id == listenerId)
            : null;

        var loaded = new List<X509Certificate2>();
        try
        {
            var (document, warnings) = PdxDatasheetBuilder.Build(new OwnStation
            {
                Identity = identity,
                SubStations = subStations,
                Profile = profile,
                Listener = listener,
                TlsCertificate = listener is { UseTls: true, Certificate: { } tlsCertificate } ? Load(tlsCertificate, loaded) : null,
                FileCertificate = await LoadAsync(settings.FileSecurityCertificateId, loaded, cancellationToken),
                ClientCertificate = await LoadAsync(settings.OftpClientCertificateId, loaded, cancellationToken),
                TrustAnchors = tsl.TrustAnchors,
                ValidFrom = validFrom,
            });

            var content = PdxWriter.Write(document);

            // What we publish has to be readable by everybody: it is checked like a datasheet of a partner.
            var check = PdxParser.Parse(content);
            if (!check.Success)
                throw new InvalidOperationException("The datasheet does not match the schema: " + string.Join(" ", check.Errors));

            if (profile.ListenerId is not null && listener is null)
                warnings = [.. warnings, "The listener selected in the station profile is not enabled."];

            return new PdxExport(content, document, warnings);
        }
        finally
        {
            foreach (var certificate in loaded)
                certificate.Dispose();
        }
    }

    /// <summary>
    /// Puts our datasheet into the send queue for <paramref name="partner"/> (virtual file OFTP_COMMUNICATION_SETUP).
    /// It is sent unencrypted, signed when we have a file security certificate, without a signed EERP.
    /// </summary>
    public async Task<(SendQueueItem Item, PdxExport Export)> QueueAsync(Partner partner, int identityId, DateTimeOffset? validFrom,
        CancellationToken cancellationToken = default)
    {
        var export = await ExportAsync(identityId, validFrom, cancellationToken);
        var item = new SendQueueItem
        {
            PartnerId = partner.Id,
            IdentityId = identityId,
            VirtualFileName = PartnerSetupService.VirtualFileName,
            FilePath = await outbox.SaveAsync(export.Content, export.FileName, cancellationToken),
            Description = "OFTP2 Communication Setup",
            Status = SendStatus.NEW,
        };

        unitOfWork.AddForInsert(item);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("OFTP2 Communication Setup {DocId} of {Ssid} queued for {Partner}, valid from {ValidFrom}",
            export.Document.DocId, export.Document.Session.Ssid, partner.Name, export.Document.ValidFrom);
        return (item, export);
    }

    private async Task<X509Certificate2?> LoadAsync(int? id, List<X509Certificate2> loaded, CancellationToken cancellationToken) =>
        id is { } certificateId ? Load(await certificates.GetObjectAsync(certificateId, cancellationToken), loaded) : null;

    private static X509Certificate2 Load(Certificate certificate, List<X509Certificate2> loaded)
    {
        var x509 = CertificateLoader.Load(certificate);
        loaded.Add(x509);
        return x509;
    }

    private static bool SameCode(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
