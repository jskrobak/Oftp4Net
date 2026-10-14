using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.TransferEvents;

namespace Oftp4Net.Services.Security;

/// <summary>
/// Certificates of partners uploaded in advance: each replaces the partner's current certificate of its purpose at
/// the time the partner starts using it. The file security certificate it replaces is kept as the previous one, so
/// that files and End to End Responses signed shortly before the change are still verified.
/// </summary>
public class CertificateChangeService(
    ICertificateChangeRepository changeRepository,
    IPartnerRepository partnerRepository,
    ICertificateRepository certificateRepository,
    IUnitOfWork unitOfWork,
    ITimeService timeService,
    ITransferEventLog eventLog,
    CertificateChangeScheduler scheduler,
    ListenerService listenerService,
    ILogger<CertificateChangeService> logger)
{
    /// <summary>Imports the certificate file (unless it is stored already) and schedules it for the partner.</summary>
    public async Task<CertificateChange> ScheduleFileAsync(int partnerId, byte[] data, string fileName, PartnerCertificateUsage usage,
        DateTime activateAt, string? note, string? createdBy, CancellationToken cancellationToken = default)
    {
        X509Certificate2 loaded;
        try
        {
            loaded = X509CertificateLoader.LoadCertificate(data);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"The file {fileName} is not a certificate (.cer, .crt, .pem): {ex.Message}", ex);
        }

        using (loaded)
        {
            if (loaded.HasPrivateKey)
                throw new InvalidOperationException("A certificate of a partner is its public part (.cer, .crt, .pem) without a private key.");

            var partner = await partnerRepository.GetObjectAsync(partnerId, cancellationToken);
            var certificate = await StoreAsync(partner, loaded, cancellationToken);
            return await ScheduleAsync(partnerId, certificate.Id, usage, activateAt, note, createdBy, cancellationToken);
        }
    }

    /// <summary>Schedules a stored certificate for the partner. A time that has passed already applies it right away.</summary>
    public async Task<CertificateChange> ScheduleAsync(int partnerId, int certificateId, PartnerCertificateUsage usage,
        DateTime activateAt, string? note, string? createdBy, CancellationToken cancellationToken = default)
    {
        var partner = await partnerRepository.GetObjectAsync(partnerId, cancellationToken);
        var certificate = await certificateRepository.GetObjectAsync(certificateId, cancellationToken);

        if (certificate.HasPrivateKey)
            throw new InvalidOperationException("A certificate of a partner is its public part without a private key.");
        if (certificate.ValidTo < activateAt)
            throw new InvalidOperationException($"The certificate expires on {certificate.ValidTo:g}, before it is to be used.");

        var change = new CertificateChange
        {
            PartnerId = partner.Id,
            PartnerName = partner.Name,
            CertificateId = certificate.Id,
            Usage = usage,
            ActivateAt = activateAt,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedBy = Truncate(createdBy, 50),
            Created = timeService.GetCurrentTime(),
        };
        unitOfWork.AddForInsert(change);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Certificate {Certificate} scheduled for partner {Partner} ({Usage}) at {ActivateAt}",
            certificate.Name, partner.Name, usage, activateAt);
        Record(TransferEventType.CertificateChangeScheduled, TransferEventLevel.Information, partner.Id, partner.Name,
            $"Certificate {certificate.Name} (valid to {certificate.ValidTo:d}) scheduled for {Describe(usage)} of partner {partner.Name} at {activateAt:g}");

        // The scheduler sleeps until the next change it knows of; this one may be earlier.
        scheduler.Trigger();
        return change;
    }

    public async Task CancelAsync(int changeId, string? cancelledBy, CancellationToken cancellationToken = default)
    {
        var change = await changeRepository.GetObjectAsync(changeId, cancellationToken);
        if (change.Status != CertificateChangeStatus.Scheduled)
            throw new InvalidOperationException($"The change is {change.Status.ToString().ToLowerInvariant()} already.");

        change.Status = CertificateChangeStatus.Cancelled;
        change.LastError = cancelledBy is null ? null : Truncate($"Cancelled by {cancelledBy}.", 1000);
        unitOfWork.AddForUpdate(change);
        await unitOfWork.CommitAsync(cancellationToken);

        Record(TransferEventType.CertificateChangeCancelled, TransferEventLevel.Information, change.PartnerId, change.PartnerName,
            $"The certificate change of partner {change.PartnerName} at {change.ActivateAt:g} was cancelled" +
            (cancelledBy is null ? "" : $" by {cancelledBy}"));
    }

    /// <summary>Applies the changes whose time has come; returns their number.</summary>
    public async Task<int> ApplyDueAsync(CancellationToken cancellationToken = default)
    {
        var now = timeService.GetCurrentTime();
        var due = await changeRepository.GetDueAsync(now, cancellationToken);
        var tls = false;
        foreach (var change in due)
            tls |= await ApplyAsync(change, now, cancellationToken);

        // The listeners check the certificates of partners that call us against the trusted ones.
        if (tls)
            await listenerService.RefreshTrustedCertificatesAsync();
        return due.Count;
    }

    /// <summary>Applies one change; returns whether the TLS trust of a partner changed.</summary>
    private async Task<bool> ApplyAsync(CertificateChange change, DateTime now, CancellationToken cancellationToken)
    {
        var partner = change.Partner;
        if (partner is null || change.CertificateId is not { } certificateId)
        {
            change.Status = CertificateChangeStatus.Failed;
            change.LastError = partner is null ? "The partner was deleted." : "The certificate was deleted.";
            unitOfWork.AddForUpdate(change);
            await unitOfWork.CommitAsync(cancellationToken);
            Record(TransferEventType.CertificateChangeFailed, TransferEventLevel.Error, change.PartnerId, change.PartnerName,
                $"The certificate change of partner {change.PartnerName} at {change.ActivateAt:g} could not be applied: {change.LastError}");
            return false;
        }

        var changed = Apply(partner, certificateId, change.Usage);
        change.Status = CertificateChangeStatus.Applied;
        change.AppliedAt = now;
        unitOfWork.AddForUpdate(partner);
        unitOfWork.AddForUpdate(change);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Certificate {Certificate} is used for {Usage} of partner {Partner} from now on",
            change.Certificate?.Name, change.Usage, partner.Name);
        Record(TransferEventType.CertificateChangeApplied, TransferEventLevel.Information, partner.Id, partner.Name,
            $"Certificate {change.Certificate?.Name} is used for {Describe(change.Usage)} of partner {partner.Name} from now on" +
            (changed.Count == 0 ? " (it was in use already)." : $": {string.Join(", ", changed)}."));
        return change.Usage is PartnerCertificateUsage.Tls or PartnerCertificateUsage.SecurityAndTls;
    }

    /// <summary>
    /// Puts the certificate in place for the purpose and returns what changed. The file security certificate it
    /// replaces becomes the previous one, still accepted for signatures, and so does every certificate of a station
    /// or purpose that was the same one.
    /// </summary>
    public static List<string> Apply(Partner partner, int certificateId, PartnerCertificateUsage usage)
    {
        var changed = new List<string>();
        if (usage is PartnerCertificateUsage.Security or PartnerCertificateUsage.SecurityAndTls
            && partner.SecurityCertificateId != certificateId)
        {
            if (partner.SecurityCertificateId is { } replaced)
            {
                foreach (var assignment in partner.Certificates.Where(a => a.CertificateId == replaced))
                {
                    assignment.PreviousCertificateId = replaced;
                    assignment.CertificateId = certificateId;
                    changed.Add($"{assignment.Usage} certificate" + (string.IsNullOrEmpty(assignment.Sfid) ? "" : $" of {assignment.Sfid}"));
                }
            }

            partner.PreviousSecurityCertificateId = partner.SecurityCertificateId;
            partner.PreviousSecurityCertificate = null;
            partner.SecurityCertificateId = certificateId;
            partner.SecurityCertificate = null;
            changed.Insert(0, "file security certificate");
        }

        if (usage is PartnerCertificateUsage.Tls or PartnerCertificateUsage.SecurityAndTls
            && partner.TrustedCertificateId != certificateId)
        {
            partner.TrustedCertificateId = certificateId;
            partner.TrustedCertificate = null;
            changed.Add("trusted TLS certificate");
        }

        return changed;
    }

    public static string Describe(PartnerCertificateUsage usage) => usage switch
    {
        PartnerCertificateUsage.Security => "file security",
        PartnerCertificateUsage.Tls => "TLS trust",
        PartnerCertificateUsage.SecurityAndTls => "file security and TLS trust",
        _ => usage.ToString(),
    };

    /// <summary>Stores the certificate in DER, or returns the stored one with the same thumbprint.</summary>
    private async Task<Certificate> StoreAsync(Partner partner, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        foreach (var stored in (await certificateRepository.GetAllAsync(cancellationToken)).Where(c => !c.HasPrivateKey))
        {
            try
            {
                using var existing = CertificateLoader.Load(stored);
                if (existing.Thumbprint == certificate.Thumbprint)
                    return stored;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException)
            {
                // A stored record that cannot be read is no match.
            }
        }

        var entity = new Certificate
        {
            Name = $"{partner.Name}: {certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)}",
            Base64Data = Convert.ToBase64String(certificate.RawData),
            ValidFrom = certificate.NotBefore,
            ValidTo = certificate.NotAfter,
            HasPrivateKey = false,
        };
        unitOfWork.AddForInsert(entity);
        // The identifier is needed for the change.
        await unitOfWork.CommitAsync(cancellationToken);
        return entity;
    }

    private void Record(TransferEventType type, TransferEventLevel level, int? partnerId, string partnerName, string message) =>
        eventLog.Record(new TransferEvent
        {
            Timestamp = timeService.GetCurrentTime(),
            Category = TransferEventCategory.Certificate,
            Type = type,
            Level = level,
            PartnerId = partnerId,
            PartnerName = partnerName,
            Message = Truncate(message, 1000)!,
        });

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
