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
/// <para>
/// Our own certificate is replaced the same way at the time announced to the partners: wherever we use the replaced
/// one for the purpose. The replaced one still decrypts for partners that have not taken over the new one yet, and
/// the new one decrypts already before the change for partners that use it early.
/// </para>
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
    GlobalSettingsService settingsService,
    IIdentityRepository identityRepository,
    IListenerRepository listenerRepository,
    ILogger<CertificateChangeService> logger)
{
    /// <summary>Imports the certificate file (unless it is stored already) and schedules it for the partner.</summary>
    public async Task<CertificateChange> ScheduleFileAsync(int partnerId, byte[] data, string fileName, PartnerCertificateUsage usage,
        DateTime activateAt, string? note, string? createdBy, CancellationToken cancellationToken = default)
    {
        using var loaded = LoadPublicCertificate(data, fileName);
        var partner = await partnerRepository.GetObjectAsync(partnerId, cancellationToken);
        var (certificate, _) = await StoreAsync($"{partner.Name}: {loaded.GetNameInfo(X509NameType.SimpleName, forIssuer: false)}", loaded, cancellationToken);
        return await ScheduleAsync(partnerId, certificate.Id, usage, activateAt, note, createdBy, cancellationToken);
    }

    /// <summary>Stores a public certificate, unless the same one is stored already; says whether it was added.</summary>
    /// <param name="name">Name of the record; the subject of the certificate when empty.</param>
    public async Task<(Certificate Certificate, bool Created)> StoreFileAsync(byte[] data, string fileName, string? name,
        CancellationToken cancellationToken = default)
    {
        using var loaded = LoadPublicCertificate(data, fileName);
        return await StoreAsync(string.IsNullOrWhiteSpace(name) ? loaded.Subject : name.Trim(), loaded, cancellationToken);
    }

    /// <summary>The certificate of a file (.cer, .crt, .pem); a file with a private key is refused.</summary>
    internal static X509Certificate2 LoadPublicCertificate(byte[] data, string fileName)
    {
        // A PEM file may carry the key next to the certificate, and the loader would just skip it.
        if (data.AsSpan().IndexOf("PRIVATE KEY"u8) >= 0)
            throw new InvalidOperationException("A certificate of a partner is its public part (.cer, .crt, .pem) without a private key.");

        X509Certificate2 loaded;
        try
        {
            loaded = X509CertificateLoader.LoadCertificate(data);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException($"The file {fileName} is not a certificate (.cer, .crt, .pem): {ex.Message}", ex);
        }

        if (loaded.HasPrivateKey)
        {
            loaded.Dispose();
            throw new InvalidOperationException("A certificate of a partner is its public part (.cer, .crt, .pem) without a private key.");
        }

        return loaded;
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

    /// <summary>
    /// Schedules our certificate <paramref name="certificateId"/> to replace our certificate
    /// <paramref name="replacedCertificateId"/> for the purpose at <paramref name="activateAt"/>. A time that has
    /// passed already applies it right away.
    /// </summary>
    /// <param name="keepPreviousUntil">
    /// Until when the replaced certificate still decrypts files and challenges of partners that have not taken over
    /// the new one; <c>null</c> until it expires.
    /// </param>
    public async Task<CertificateChange> ScheduleOwnAsync(int certificateId, int replacedCertificateId, PartnerCertificateUsage usage,
        DateTime activateAt, DateTime? keepPreviousUntil, string? note, string? createdBy, CancellationToken cancellationToken = default)
    {
        if (certificateId == replacedCertificateId)
            throw new InvalidOperationException("The new certificate is the one it is to replace.");

        var certificate = await certificateRepository.GetObjectAsync(certificateId, cancellationToken);
        var replaced = await certificateRepository.GetObjectAsync(replacedCertificateId, cancellationToken);
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException($"Certificate {certificate.Name} has no private key; our certificate needs one.");
        if (certificate.ValidTo < activateAt)
            throw new InvalidOperationException($"The certificate expires on {certificate.ValidTo:g}, before it is to be used.");
        if (certificate.ValidFrom > activateAt)
            throw new InvalidOperationException($"The certificate is valid from {certificate.ValidFrom:g} only, after it is to be used.");
        if (keepPreviousUntil is { } until && until < activateAt)
            throw new InvalidOperationException("The replaced certificate cannot be kept until a time before the change.");

        var change = new CertificateChange
        {
            Target = CertificateChangeTarget.Own,
            PartnerName = string.Empty,
            CertificateId = certificate.Id,
            ReplacedCertificateId = replaced.Id,
            Usage = usage,
            ActivateAt = activateAt,
            KeepPreviousUntil = keepPreviousUntil,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedBy = Truncate(createdBy, 50),
            Created = timeService.GetCurrentTime(),
        };
        unitOfWork.AddForInsert(change);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Our certificate {Certificate} scheduled to replace {Replaced} ({Usage}) at {ActivateAt}",
            certificate.Name, replaced.Name, usage, activateAt);
        Record(TransferEventType.CertificateChangeScheduled, TransferEventLevel.Information, null, string.Empty,
            $"Our certificate {certificate.Name} (valid to {certificate.ValidTo:d}) scheduled to replace {replaced.Name} for " +
            $"{DescribeOwn(usage)} at {activateAt:g}; until then it already decrypts files of partners that use it");

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
            $"The change of {Whose(change)} at {change.ActivateAt:g} was cancelled" +
            (cancelledBy is null ? "" : $" by {cancelledBy}"));
    }

    /// <summary>Applies the changes whose time has come; returns their number.</summary>
    public async Task<int> ApplyDueAsync(CancellationToken cancellationToken = default)
    {
        var now = timeService.GetCurrentTime();
        var due = await changeRepository.GetDueAsync(now, cancellationToken);
        var tls = false;
        var listeners = false;
        foreach (var change in due)
        {
            if (change.Target == CertificateChangeTarget.Own)
                listeners |= await ApplyOwnAsync(change, now, cancellationToken);
            else
                tls |= await ApplyAsync(change, now, cancellationToken);
        }

        // The listeners check the certificates of partners that call us against the trusted ones.
        if (tls)
            await listenerService.RefreshTrustedCertificatesAsync();
        // They present the new server certificate from the next connection on; running sessions go on.
        if (listeners)
            await listenerService.ReloadServerCertificatesAsync();
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

    /// <summary>Applies one change of our certificate; returns whether the server certificate of a listener changed.</summary>
    private async Task<bool> ApplyOwnAsync(CertificateChange change, DateTime now, CancellationToken cancellationToken)
    {
        if (change.CertificateId is not { } certificateId || change.ReplacedCertificateId is not { } replacedId)
        {
            change.Status = CertificateChangeStatus.Failed;
            change.LastError = change.CertificateId is null ? "The certificate was deleted." : "The replaced certificate was deleted.";
            unitOfWork.AddForUpdate(change);
            await unitOfWork.CommitAsync(cancellationToken);
            Record(TransferEventType.CertificateChangeFailed, TransferEventLevel.Error, null, string.Empty,
                $"The change of our certificate at {change.ActivateAt:g} could not be applied: {change.LastError}");
            return false;
        }

        var settings = await settingsService.GetGlobalSettingsAsync();
        var identities = await identityRepository.GetAllAsync(cancellationToken);
        var listeners = await listenerRepository.GetAllAsync(cancellationToken);
        var result = ApplyOwn(settings, identities, listeners, certificateId, replacedId, change.Usage,
            change.KeepPreviousUntil ?? change.ReplacedCertificate?.ValidTo);

        // The settings first: should the rest fail, the change stays scheduled and is applied again, which finds
        // the settings done already.
        if (result.Settings)
            await settingsService.SetGlobalSettingsAsync(settings);
        foreach (var identity in result.Identities)
            unitOfWork.AddForUpdate(identity);
        foreach (var listener in result.Listeners)
            unitOfWork.AddForUpdate(listener);
        change.Status = CertificateChangeStatus.Applied;
        change.AppliedAt = now;
        unitOfWork.AddForUpdate(change);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation("Our certificate {Certificate} replaces {Replaced} for {Usage} from now on",
            change.Certificate?.Name, change.ReplacedCertificate?.Name, change.Usage);
        Record(TransferEventType.CertificateChangeApplied,
            result.Changed.Count == 0 ? TransferEventLevel.Warning : TransferEventLevel.Information, null, string.Empty,
            $"Our certificate {change.Certificate?.Name} replaces {change.ReplacedCertificate?.Name} for {DescribeOwn(change.Usage)} from now on" +
            (result.Changed.Count == 0
                ? ", but the replaced certificate was not used for it anywhere."
                : $": {string.Join(", ", result.Changed)}."));
        return result.Listeners.Count > 0;
    }

    /// <summary>What a change of our certificate changed.</summary>
    public sealed record OwnChangeResult(List<string> Changed, bool Settings, List<Identity> Identities, List<Listener> Listeners);

    /// <summary>
    /// Puts our certificate in place of the replaced one wherever that is used for the purpose: for file security
    /// in the settings and at our identities, the replaced one being kept as the previous one until
    /// <paramref name="keepPreviousUntil"/>; for TLS as client certificate and at the listeners.
    /// </summary>
    public static OwnChangeResult ApplyOwn(GlobalSettings settings, IEnumerable<Identity> identities, IEnumerable<Listener> listeners,
        int certificateId, int replacedId, PartnerCertificateUsage usage, DateTime? keepPreviousUntil)
    {
        var result = new OwnChangeResult([], false, [], []);
        var settingsChanged = false;

        if (usage is PartnerCertificateUsage.Security or PartnerCertificateUsage.SecurityAndTls)
        {
            var replacedForSecurity = false;
            if (settings.FileSecurityCertificateId == replacedId)
            {
                settings.FileSecurityCertificateId = certificateId;
                result.Changed.Add("file security certificate");
                replacedForSecurity = true;
            }

            foreach (var identity in identities)
            {
                var assignments = identity.Certificates.Where(a => a.CertificateId == replacedId).ToList();
                foreach (var assignment in assignments)
                {
                    assignment.PreviousCertificateId = replacedId;
                    assignment.CertificateId = certificateId;
                    result.Changed.Add($"{assignment.Usage} certificate of identity {identity.Name}" +
                                       (string.IsNullOrEmpty(assignment.Sfid) ? "" : $" ({assignment.Sfid})"));
                }

                if (assignments.Count > 0)
                {
                    result.Identities.Add(identity);
                    replacedForSecurity = true;
                }
            }

            if (replacedForSecurity)
            {
                settings.PreviousFileSecurityCertificateId = replacedId;
                settings.PreviousFileSecurityCertificateUntil = keepPreviousUntil;
                settingsChanged = true;
            }
        }

        if (usage is PartnerCertificateUsage.Tls or PartnerCertificateUsage.SecurityAndTls)
        {
            if (settings.OftpClientCertificateId == replacedId)
            {
                settings.OftpClientCertificateId = certificateId;
                result.Changed.Add("TLS client certificate");
                settingsChanged = true;
            }

            foreach (var listener in listeners.Where(l => l.CertificateId == replacedId))
            {
                listener.CertificateId = certificateId;
                listener.Certificate = null;
                result.Listeners.Add(listener);
                result.Changed.Add($"server certificate of listener {listener.Name}");
            }
        }

        return result with { Settings = settingsChanged };
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

    /// <summary>What the purpose means for our own certificate.</summary>
    public static string DescribeOwn(PartnerCertificateUsage usage) => usage switch
    {
        PartnerCertificateUsage.Security => "file security (signing, decryption, authentication)",
        PartnerCertificateUsage.Tls => "TLS (client certificate and listeners)",
        PartnerCertificateUsage.SecurityAndTls => "file security and TLS",
        _ => usage.ToString(),
    };

    /// <summary>Whose certificate a change replaces, for messages.</summary>
    public static string Whose(CertificateChange change) =>
        change.Target == CertificateChangeTarget.Own ? "our certificate" : $"the certificate of partner {change.PartnerName}";

    public static string Describe(PartnerCertificateUsage usage) => usage switch
    {
        PartnerCertificateUsage.Security => "file security",
        PartnerCertificateUsage.Tls => "TLS trust",
        PartnerCertificateUsage.SecurityAndTls => "file security and TLS trust",
        _ => usage.ToString(),
    };

    /// <summary>Stores the certificate in DER, or returns the stored one with the same thumbprint.</summary>
    private async Task<(Certificate Certificate, bool Created)> StoreAsync(string name, X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        foreach (var stored in (await certificateRepository.GetAllAsync(cancellationToken)).Where(c => !c.HasPrivateKey))
        {
            try
            {
                using var existing = CertificateLoader.Load(stored);
                if (existing.Thumbprint == certificate.Thumbprint)
                    return (stored, false);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException)
            {
                // A stored record that cannot be read is no match.
            }
        }

        var entity = new Certificate
        {
            Name = name,
            Base64Data = Convert.ToBase64String(certificate.RawData),
            ValidFrom = certificate.NotBefore,
            ValidTo = certificate.NotAfter,
            HasPrivateKey = false,
        };
        unitOfWork.AddForInsert(entity);
        // The identifier is needed for the change.
        await unitOfWork.CommitAsync(cancellationToken);
        return (entity, true);
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
