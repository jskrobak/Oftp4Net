using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Microsoft.Extensions.Logging;
using Oftp4Net.Core.Protocol;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.TransferEvents;
using Oftp4Net.Services.Tsl;

namespace Oftp4Net.Services.Security;

/// <summary>What happened with a received certificate file.</summary>
/// <param name="Accepted">The certificate was taken over; the partner gets an EERP.</param>
/// <param name="Message">What was done, or why the certificate was refused (it then goes into the NERP).</param>
/// <param name="Answer">
/// Our certificate queued as an answer to an ODETTE_CERTIFICATE_REQUEST; it can still be sent in this session.
/// </param>
public sealed record CertificateExchangeResult(bool Accepted, string Message, SendQueueItem? Answer = null);

/// <summary>
/// Automatic exchange of certificates over OFTP (Odette OP08 2.5): the virtual files ODETTE_CERTIFICATE_REQUEST,
/// ODETTE_CERTIFICATE_DELIVER and ODETTE_CERTIFICATE_REPLACE carry one certificate in DER, unsecured. A received
/// certificate is checked (validity, chain of trust, revocation) and assigned to the partner it belongs to; the
/// assignment is based on the Certificate Logical Identification Data, either taken from SFIDDESC (the certificate
/// that is being replaced) or from the certificate itself. What cannot be assigned is answered with a NERP and has
/// to be sorted out by the administrator, as the specification requires.
/// </summary>
public class CertificateExchangeService(
    ICertificateRepository certificates,
    IUnitOfWork unitOfWork,
    GlobalSettingsService settingsService,
    OutboxStorage outbox,
    TslService tsl,
    ITimeService timeService,
    ITransferEventLog events,
    ILogger<CertificateExchangeService> logger)
{
    /// <summary>NERP texts are limited (NERPREASL has three digits).</summary>
    private const int ReasonTextLength = 999;

    /// <summary>
    /// Puts one of our certificates into the send queue for <paramref name="partner"/>. Only the certificate is
    /// sent, never the private key.
    /// </summary>
    /// <param name="replacedCertificateId">
    /// Our certificate the partner has so far and that the new one replaces; its logical identification data goes
    /// into SFIDDESC, so that the partner can assign the new certificate even when its subject or issuer changed.
    /// </param>
    /// <param name="destinationSfid">Sub-station of the partner the certificate is meant for (SFIDDEST).</param>
    public async Task<SendQueueItem> QueueAsync(Partner partner, int identityId, CertificateExchangeKind kind,
        int certificateId, int? replacedCertificateId = null, string? destinationSfid = null,
        CancellationToken cancellationToken = default)
    {
        using var certificate = CertificateLoader.Load(await certificates.GetObjectAsync(certificateId, cancellationToken));
        var description = replacedCertificateId is { } replacedId ? await DescribeAsync(replacedId, cancellationToken) : null;

        var item = new SendQueueItem
        {
            PartnerId = partner.Id,
            IdentityId = identityId,
            DestinationSfid = destinationSfid,
            VirtualFileName = CertificateExchange.FileName(kind),
            // The certificate itself, in DER, without the private key.
            FilePath = await outbox.SaveAsync(certificate.RawData, $"{CertificateExchange.FileName(kind)}.cer", cancellationToken),
            Description = description,
            Format = FileFormats.Unstructured,
            Status = SendStatus.NEW,
        };

        unitOfWork.AddForInsert(item);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Certificate {Subject} queued for {Partner} as {VirtualFileName}{Replaces}",
            certificate.Subject, partner.Name, item.VirtualFileName,
            description is null ? "" : " (replacing the certificate named in SFIDDESC)");
        return item;
    }

    /// <summary>
    /// Takes over a certificate a partner sent us and sets the state of <paramref name="record"/>: RECEIVED when it
    /// was taken over (an EERP follows), NOT_DELIVERED with the reason otherwise (a NERP follows). The caller
    /// commits the changes together with the file.
    /// </summary>
    /// <param name="identityId">Our identity the file was addressed to; an answer is sent under it.</param>
    public async Task<CertificateExchangeResult> ReceiveAsync(Partner partner, int? identityId, ReceivedFile record,
        byte[] content, CancellationToken cancellationToken)
    {
        var kind = CertificateExchange.KindOf(record.VirtualFileName)
            ?? throw new ArgumentException($"'{record.VirtualFileName}' is not a certificate exchange.", nameof(record));
        var settings = await settingsService.GetGlobalSettingsAsync();

        if (settings.CertificateExchange == CertificateExchangeMode.Off)
            return Refuse(partner, record, kind,
                "The automatic exchange of certificates is switched off (setting CertificateExchange).");

        X509Certificate2 certificate;
        try
        {
            certificate = Read(content);
        }
        catch (CryptographicException ex)
        {
            return Refuse(partner, record, kind, "The file does not contain a certificate: " + ex.Message);
        }

        using (certificate)
        {
            var now = timeService.GetCurrentTime();
            if (now < certificate.NotBefore || now > certificate.NotAfter)
                return Refuse(partner, record, kind,
                    $"The certificate {certificate.Subject} is valid from {certificate.NotBefore:g} to {certificate.NotAfter:g} only.");

            var stored = await GetPartnerCertificatesAsync(partner, cancellationToken);
            var replaced = Match(certificate, record.Description, stored);
            var trust = CertificateTrust.Check(certificate, [], tsl.TrustAnchors, out var problem,
                settings.RevocationPolicy, tsl.VerificationRoots);

            if (trust == CertificateTrustSource.None)
            {
                // A self signed certificate is only accepted as the renewal of one that is already configured here
                // (OP08 1.10 C); anything else has to be checked by the administrator.
                if (replaced is null || !IsSelfSigned(certificate))
                    return Refuse(partner, record, kind,
                        $"The certificate {certificate.Subject} is not trusted: {problem}");
            }
            else if (replaced is null && settings.CertificateExchange != CertificateExchangeMode.Trusted)
            {
                return Refuse(partner, record, kind,
                    $"The certificate {certificate.Subject} does not belong to any certificate configured for " +
                    $"{partner.Name} and is not taken over (setting CertificateExchange).");
            }

            var entity = await StoreAsync(partner, certificate, kind, cancellationToken);
            var changes = Assign(partner, entity, kind, replaced);
            unitOfWork.AddForUpdate(partner);

            var answer = kind == CertificateExchangeKind.Request
                ? await AnswerAsync(partner, identityId, record, settings, cancellationToken)
                : null;

            var message = $"Certificate {certificate.Subject} of {partner.Name} taken over " +
                          $"({string.Join(", ", changes)}), trusted by the {Describe(trust)}" +
                          (answer is null ? "" : "; our certificate is sent back");
            record.Status = ReceiveStatus.RECEIVED;
            Record(partner, record, TransferEventType.CertificateReceived, TransferEventLevel.Information, message);
            logger.LogInformation("{Message}", message);
            return new CertificateExchangeResult(true, message, answer);
        }
    }

    /// <summary>The logical identification data of a certificate, as it goes into SFIDDESC.</summary>
    public async Task<string> DescribeAsync(int certificateId, CancellationToken cancellationToken = default)
    {
        using var certificate = CertificateLoader.Load(await certificates.GetObjectAsync(certificateId, cancellationToken));
        return CertificateLogicalId.From(certificate).ToFileDescription();
    }

    /// <summary>DER, or PEM from implementations that send the certificate in text form.</summary>
    private static X509Certificate2 Read(byte[] content)
    {
        var text = content.Length < 64 * 1024 ? Encoding.UTF8.GetString(content) : null;
        return text is not null && text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(text)
            : X509CertificateLoader.LoadCertificate(content);
    }

    /// <summary>
    /// The certificate of the partner the received one replaces: the one named in SFIDDESC, or the one with the
    /// same logical identification data (same owner and purpose).
    /// </summary>
    internal static Certificate? Match(X509Certificate2 received, string? fileDescription,
        IReadOnlyList<(Certificate Entity, X509Certificate2 Loaded)> stored)
    {
        if (CertificateLogicalId.Parse(fileDescription) is { } named)
        {
            var match = stored.FirstOrDefault(s => named.IsInstance(s.Loaded) || named.Identifies(s.Loaded));
            if (match.Entity is not null)
                return match.Entity;
        }

        var own = CertificateLogicalId.From(received);
        return stored.FirstOrDefault(s => own.Identifies(s.Loaded)).Entity;
    }

    /// <summary>
    /// Assigns the certificate to the partner. A delivered certificate replaces the one it belongs to and the old
    /// one stays valid for the roll-over period (OP08 2.5 F); a replacement takes the old one out of use at once.
    /// </summary>
    internal static List<string> Assign(Partner partner, Certificate entity, CertificateExchangeKind kind, Certificate? replaced)
    {
        var changes = new List<string>();
        var replacedId = replaced?.Id;
        var previousSecurityId = partner.SecurityCertificateId;

        // The certificate takes the place of the one it replaces; when nothing is configured yet, it fills the
        // empty place (partners commonly start with one certificate for everything).
        if (replacedId is null || partner.SecurityCertificateId is null ||
            replacedId == partner.SecurityCertificateId || replacedId == partner.PreviousSecurityCertificateId)
        {
            if (partner.SecurityCertificateId != entity.Id)
            {
                // A replaced certificate must not be used any more, a rolled over one still may be.
                partner.PreviousSecurityCertificateId = kind == CertificateExchangeKind.Replace
                    ? null
                    : partner.SecurityCertificateId;
                partner.PreviousSecurityCertificate = null;
                partner.SecurityCertificateId = entity.Id;
                partner.SecurityCertificate = null;
                changes.Add(kind == CertificateExchangeKind.Replace
                    ? "file security, the previous certificate is no longer used"
                    : "file security, the previous certificate stays valid");
            }
        }

        // Partners commonly use one certificate for everything, TLS included.
        if (partner.TrustedCertificateId is null || partner.TrustedCertificateId == replacedId ||
            partner.TrustedCertificateId == previousSecurityId)
        {
            if (partner.TrustedCertificateId != entity.Id)
            {
                partner.TrustedCertificateId = entity.Id;
                partner.TrustedCertificate = null;
                changes.Add("TLS");
            }
        }

        if (changes.Count == 0)
            changes.Add("it was already configured");

        return changes;
    }

    /// <summary>The certificate as a record of the database; an identical one that is already stored is reused.</summary>
    private async Task<Certificate> StoreAsync(Partner partner, X509Certificate2 certificate, CertificateExchangeKind kind,
        CancellationToken cancellationToken)
    {
        var data = Convert.ToBase64String(certificate.RawData);
        var existing = (await certificates.GetAllAsync(cancellationToken))
            .FirstOrDefault(c => !c.HasPrivateKey && c.Base64Data == data);
        if (existing is not null)
            return existing;

        var entity = new Certificate
        {
            Name = Name(partner, certificate, kind),
            Base64Data = data,
            ValidFrom = certificate.NotBefore,
            ValidTo = certificate.NotAfter,
            HasPrivateKey = false,
        };

        unitOfWork.AddForInsert(entity);
        // The identifier is needed to assign the certificate to the partner in the same commit.
        await unitOfWork.CommitAsync(cancellationToken);
        return entity;
    }

    private static string Name(Partner partner, X509Certificate2 certificate, CertificateExchangeKind kind)
    {
        var common = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var origin = kind switch
        {
            CertificateExchangeKind.Replace => "replacement",
            CertificateExchangeKind.Request => "requested",
            _ => "delivered",
        };
        var name = $"{partner.Name} {(string.IsNullOrEmpty(common) ? certificate.Subject : common)} " +
                   $"({origin}, valid to {certificate.NotAfter:yyyy-MM-dd})";
        return name.Length <= 200 ? name : name[..200];
    }

    /// <summary>Answers an ODETTE_CERTIFICATE_REQUEST with our own certificate (OP08 2.5 D).</summary>
    private async Task<SendQueueItem?> AnswerAsync(Partner partner, int? identityId, ReceivedFile record,
        GlobalSettings settings, CancellationToken cancellationToken)
    {
        if (settings.FileSecurityCertificateId is not { } ownId)
        {
            logger.LogWarning("The certificate request of {Partner} cannot be answered: no certificate for file " +
                              "security is configured (setting FileSecurityCertificateId)", partner.Name);
            return null;
        }

        if (identityId is not { } identity)
        {
            logger.LogWarning("The certificate request of {Partner} cannot be answered: {Destination} is not one of " +
                              "our identities", partner.Name, record.Destination);
            return null;
        }

        // The answer goes back to the station that asked for it.
        var destination = string.Equals(record.Originator.Trim(), partner.SFID.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : record.Originator;

        return await QueueAsync(partner, identity, CertificateExchangeKind.Deliver, ownId,
            destinationSfid: destination, cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<(Certificate Entity, X509Certificate2 Loaded)>> GetPartnerCertificatesAsync(
        Partner partner, CancellationToken cancellationToken)
    {
        var result = new List<(Certificate, X509Certificate2)>();
        foreach (var id in new[] { partner.SecurityCertificateId, partner.PreviousSecurityCertificateId, partner.TrustedCertificateId }
                     .OfType<int>().Distinct())
        {
            var entity = await certificates.GetObjectAsync(id, cancellationToken);
            try
            {
                result.Add((entity, CertificateLoader.Load(entity)));
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or FormatException)
            {
                logger.LogWarning(ex, "Certificate {Name} of {Partner} cannot be read", entity.Name, partner.Name);
            }
        }

        return result;
    }

    private static bool IsSelfSigned(X509Certificate2 certificate) =>
        certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);

    private static string Describe(CertificateTrustSource trust) => trust switch
    {
        CertificateTrustSource.Tsl => "Odette trust list",
        CertificateTrustSource.System => "trust store of the system",
        _ => "certificate already configured for the partner",
    };

    private CertificateExchangeResult Refuse(Partner partner, ReceivedFile record, CertificateExchangeKind kind, string reason)
    {
        record.Status = ReceiveStatus.NOT_DELIVERED;
        record.NotDeliveredReasonCode = AnswerReasonCodes.UnspecifiedReason;
        record.LastError = reason.Length > ReasonTextLength ? reason[..(ReasonTextLength - 1)] + "…" : reason;

        var message = $"{CertificateExchange.FileName(kind)} from {partner.Name} refused: {reason}";
        Record(partner, record, TransferEventType.CertificateRejected, TransferEventLevel.Warning, message);
        logger.LogWarning("{Message}", message);
        return new CertificateExchangeResult(false, reason);
    }

    private void Record(Partner partner, ReceivedFile record, TransferEventType type, TransferEventLevel level, string message) =>
        events.Record(new TransferEvent
        {
            Timestamp = timeService.GetCurrentTime(),
            Category = TransferEventCategory.Incoming,
            Type = type,
            Level = level,
            Message = message,
            PartnerId = partner.Id,
            PartnerName = partner.Name,
            ReceivedFileId = record.Id,
            VirtualFileName = record.VirtualFileName,
            FileDate = record.FileDate,
            FileTime = record.FileTime,
        });
}
