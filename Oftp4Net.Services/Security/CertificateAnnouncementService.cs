using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Havit.Services.TimeServices;
using Microsoft.Extensions.Logging;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Mail;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.TransferEvents;

namespace Oftp4Net.Services.Security;

/// <summary>One partner connection named in an announcement.</summary>
public sealed record AnnouncedConnection(int PartnerId, string PartnerName, string PartnerSsid, string Host, int Port, string OurSsid, int? IdentityId);

/// <summary>One e-mail of an announcement: the partners that share the same recipients get one message together.</summary>
/// <param name="Key">Identifies the message among the others of the change (the recipients).</param>
/// <param name="SentAt">When it was sent already, <c>null</c> when not yet.</param>
public sealed record AnnouncementMail(
    string Key, IReadOnlyList<string> To, IReadOnlyList<AnnouncedConnection> Connections, string Subject, string Body, DateTime? SentAt);

/// <summary>The announcement of a scheduled change of our certificate.</summary>
public sealed record AnnouncementPlan(
    CertificateChange Change, string CertificateSubject, string FileName, IReadOnlyList<AnnouncementMail> Mails,
    IReadOnlyList<string> PartnersWithoutContact);

/// <summary>Result of sending one message.</summary>
public sealed record AnnouncementResult(string Key, IReadOnlyList<string> To, string? Error);

/// <summary>
/// Announces a scheduled new certificate of ours to the partners by e-mail (<see cref="MailService"/>), from the
/// template in the settings (<see cref="CertificateAnnouncementTemplate"/>): one message per set of recipients, with
/// the partner's connections, the time of the change, the fingerprints and the certificate attached; on request the
/// certificate goes over OFTP as a delivery as well. Every message sent is written to Logs → Certificates.
/// </summary>
public class CertificateAnnouncementService(
    ICertificateChangeRepository changeRepository,
    ICertificateRepository certificateRepository,
    IPartnerRepository partnerRepository,
    IIdentityRepository identityRepository,
    IListenerRepository listenerRepository,
    ITransferEventRepository eventRepository,
    GlobalSettingsService settingsService,
    MailService mailService,
    CertificateExchangeService exchangeService,
    ApplicationTimeService time,
    ITimeService timeService,
    ITransferEventLog eventLog,
    ILogger<CertificateAnnouncementService> logger)
{
    /// <summary>The messages of the announcement of change <paramref name="changeId"/>.</summary>
    /// <param name="pageUrl">Address of the public certificate page, <c>null</c> when it is not to be named.</param>
    /// <param name="viaOftp">Whether the text says that the certificate is sent over OFTP as well.</param>
    public async Task<AnnouncementPlan> PlanAsync(int changeId, string? pageUrl, bool viaOftp, CancellationToken cancellationToken = default)
    {
        var change = await LoadChangeAsync(changeId, cancellationToken);
        var settings = await settingsService.GetGlobalSettingsAsync();
        using var certificate = await LoadAsync(change.CertificateId!.Value, cancellationToken);
        using var replaced = change.ReplacedCertificateId is { } replacedId ? await LoadAsync(replacedId, cancellationToken) : null;

        var partners = (await partnerRepository.GetAllAsync(cancellationToken)).Where(p => !p.Draft).ToList();
        var identities = await identityRepository.GetAllAsync(cancellationToken);
        var listeners = await listenerRepository.GetAllAsync(cancellationToken);
        var defaultIdentity = DefaultIdentity(settings, identities, listeners);
        var sent = (await eventRepository.GetByDetailsAsync(TransferEventType.CertificateAnnounced, Details(changeId), cancellationToken))
            .GroupBy(e => e.PartnerId).ToDictionary(g => g.Key ?? 0, g => g.Max(e => e.Timestamp));

        var values = Values(settings, change, certificate, replaced, pageUrl, viaOftp, defaultIdentity);
        var subjectTemplate = string.IsNullOrWhiteSpace(settings.CertificateAnnouncementSubject)
            ? CertificateAnnouncementTemplate.DefaultSubject : settings.CertificateAnnouncementSubject;
        var bodyTemplate = string.IsNullOrWhiteSpace(settings.CertificateAnnouncementBody)
            ? CertificateAnnouncementTemplate.DefaultBody : settings.CertificateAnnouncementBody;

        var mails = new List<AnnouncementMail>();
        foreach (var group in Group(partners))
        {
            var connections = group.Partners.Select(p =>
            {
                var identity = identities.FirstOrDefault(i => i.Id == p.InboundIdentityId) ?? defaultIdentity;
                return new AnnouncedConnection(p.Id, p.Name, p.SSID.Trim(), p.Host, p.Port, identity?.SSID.Trim() ?? "", identity?.Id);
            }).ToList();

            var mailValues = new Dictionary<string, string?>(values) { ["Connections"] = Connections(connections) };
            var sentAt = group.Partners.Select(p => sent.TryGetValue(p.Id, out var at) ? at : (DateTime?)null).Max();
            mails.Add(new AnnouncementMail(string.Join(",", group.To), group.To, connections,
                CertificateAnnouncementTemplate.Render(subjectTemplate, mailValues),
                CertificateAnnouncementTemplate.Render(bodyTemplate, mailValues),
                sentAt));
        }

        var withoutContact = partners.Where(p => Recipients(p).Count == 0).Select(p => p.Name).Order().ToList();
        return new AnnouncementPlan(change, certificate.Subject, values["FileName"]!, mails, withoutContact);
    }

    /// <summary>Sends the messages with the given keys; a failure of one does not stop the others.</summary>
    public async Task<List<AnnouncementResult>> SendAsync(int changeId, string? pageUrl, bool viaOftp, IReadOnlyCollection<string> keys,
        string? actor, CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(changeId, pageUrl, viaOftp, cancellationToken);
        var attachment = await AttachmentAsync(plan, cancellationToken);
        var partners = (await partnerRepository.GetAllAsync(cancellationToken)).ToDictionary(p => p.Id);
        var results = new List<AnnouncementResult>();

        foreach (var mail in plan.Mails.Where(m => keys.Contains(m.Key)))
        {
            try
            {
                await mailService.SendAsync(new OutgoingMail(mail.To, mail.Subject, mail.Body, [attachment]), cancellationToken);
            }
            catch (MailException ex)
            {
                results.Add(new AnnouncementResult(mail.Key, mail.To, ex.Message));
                foreach (var connection in mail.Connections)
                    Record(TransferEventLevel.Error, connection, changeId,
                        $"Our certificate {plan.CertificateSubject} could not be announced to {string.Join(", ", mail.To)}: {ex.Message}");
                continue;
            }

            results.Add(new AnnouncementResult(mail.Key, mail.To, null));
            foreach (var connection in mail.Connections)
            {
                var oftp = "";
                if (viaOftp && connection.IdentityId is { } identityId && partners.TryGetValue(connection.PartnerId, out var partner))
                {
                    await exchangeService.QueueAsync(partner, identityId, CertificateExchangeKind.Deliver,
                        plan.Change.CertificateId!.Value, plan.Change.ReplacedCertificateId, cancellationToken: cancellationToken);
                    oftp = " and queued for delivery over OFTP";
                }

                Record(TransferEventLevel.Information, connection, changeId,
                    $"Our certificate {plan.CertificateSubject} (used from {plan.Change.ActivateAt:g}) announced by e-mail to " +
                    $"{string.Join(", ", mail.To)}{oftp}" + (actor is null ? "" : $" by {actor}"));
            }
        }

        logger.LogInformation("Announcement of certificate change {ChangeId}: {Sent} sent, {Failed} failed", changeId,
            results.Count(r => r.Error is null), results.Count(r => r.Error is not null));
        return results;
    }

    /// <summary>Sends one message of the announcement to <paramref name="address"/> only, to check it; nothing is recorded.</summary>
    public async Task SendTestAsync(int changeId, string? pageUrl, bool viaOftp, string key, string address,
        CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(changeId, pageUrl, viaOftp, cancellationToken);
        var mail = plan.Mails.FirstOrDefault(m => m.Key == key) ?? throw new InvalidOperationException("The message is not part of the announcement.");
        var attachment = await AttachmentAsync(plan, cancellationToken);
        await mailService.SendAsync(new OutgoingMail([address.Trim()],
            $"[TEST for {string.Join(", ", mail.To)}] {mail.Subject}", mail.Body, [attachment]), cancellationToken);
    }

    /// <summary>
    /// The partners grouped by the e-mail addresses of their contacts: one message per set of recipients. Partners
    /// without an address are left out.
    /// </summary>
    public static List<(IReadOnlyList<string> To, List<Partner> Partners)> Group(IEnumerable<Partner> partners) => partners
        .Select(p => (To: Recipients(p), Partner: p))
        .Where(x => x.To.Count > 0)
        .GroupBy(x => string.Join(",", x.To).ToLowerInvariant())
        .Select(g => ((IReadOnlyList<string>)g.First().To, g.Select(x => x.Partner).OrderBy(p => p.Name).ToList()))
        .OrderBy(g => g.Item2[0].Name)
        .ToList();

    /// <summary>The distinct e-mail addresses of the partner's contacts, sorted.</summary>
    public static List<string> Recipients(Partner partner) => partner.Contacts
        .SelectMany(c => c.Emails)
        .Select(e => e.Trim())
        .Where(e => e.Contains('@'))
        .DistinctBy(e => e.ToLowerInvariant())
        .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>One line per connection: partner – its Odette ID (host) ⇄ our Odette ID.</summary>
    public static string Connections(IEnumerable<AnnouncedConnection> connections) => string.Join("\n", connections.Select(c =>
        $"  {c.PartnerName} – {c.PartnerSsid} ({c.Host}:{c.Port}) ⇄ {c.OurSsid}"));

    /// <summary>The identity that answers partners without an inbound identity: that of the listener partners call.</summary>
    private static Identity? DefaultIdentity(GlobalSettings settings, IReadOnlyList<Identity> identities, IReadOnlyList<Listener> listeners)
    {
        var listener = listeners.FirstOrDefault(l => l.Id == settings.StationProfile.ListenerId)
                       ?? listeners.Where(l => l.Enabled).OrderBy(l => l.Id).FirstOrDefault();
        return identities.FirstOrDefault(i => i.Id == listener?.IdentityId) ?? identities.OrderBy(i => i.Id).FirstOrDefault();
    }

    private Dictionary<string, string?> Values(GlobalSettings settings, CertificateChange change, X509Certificate2 certificate,
        X509Certificate2? replaced, string? pageUrl, bool viaOftp, Identity? defaultIdentity)
    {
        var profile = settings.StationProfile;
        var previousUntil = change.KeepPreviousUntil ?? replaced?.NotAfter;
        return new Dictionary<string, string?>
        {
            // A text without a sender reads oddly: the identity partners know us by when no company is set.
            ["Company"] = string.IsNullOrWhiteSpace(profile.CompanyName) ? defaultIdentity?.Name : profile.CompanyName.Trim(),
            ["Host"] = profile.PublicHost,
            ["Port"] = profile.PublicPort?.ToString(),
            ["ActivateAt"] = Time(change.ActivateAt),
            ["PreviousUntil"] = previousUntil is { } until ? Time(until) : null,
            ["Subject"] = certificate.Subject,
            ["Issuer"] = certificate.Issuer,
            ["IssuerName"] = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: true) is { Length: > 0 } issuer ? issuer : certificate.Issuer,
            ["SerialNumber"] = certificate.SerialNumber,
            ["ValidFrom"] = certificate.NotBefore.ToUniversalTime().ToString("yyyy-MM-dd"),
            ["ValidTo"] = certificate.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd"),
            ["Sha256"] = Fingerprint(SHA256.HashData(certificate.RawData)),
            ["Sha1"] = Fingerprint(SHA1.HashData(certificate.RawData)),
            ["ReplacedSubject"] = replaced?.Subject,
            ["ReplacedSerialNumber"] = replaced?.SerialNumber,
            ["FileName"] = FileName(certificate),
            ["PageUrl"] = string.IsNullOrWhiteSpace(pageUrl) ? null : pageUrl.Trim(),
            ["Oftp"] = viaOftp ? "yes" : null,
            ["Pem"] = certificate.ExportCertificatePem(),
        };
    }

    /// <summary>A time of the application as partners elsewhere read it: with its offset to UTC.</summary>
    private string Time(DateTime local) => time.ToInstant(local).ToString("yyyy-MM-dd HH:mm 'UTC'zzz");

    private static string FileName(X509Certificate2 certificate)
    {
        var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        var safe = string.Concat((string.IsNullOrWhiteSpace(name) ? "certificate" : name).Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_'));
        return $"{safe}-{certificate.NotBefore.ToUniversalTime():yyyy}.cer";
    }

    private static string Fingerprint(byte[] hash) => string.Join(':', hash.Select(b => b.ToString("X2")));

    private static string Details(int changeId) => $"certificate-change:{changeId}";

    private async Task<CertificateChange> LoadChangeAsync(int changeId, CancellationToken cancellationToken)
    {
        var change = (await changeRepository.GetScheduledAsync(cancellationToken)).FirstOrDefault(c => c.Id == changeId)
                     ?? throw new InvalidOperationException("Only a scheduled change of our certificate can be announced.");
        if (change.Target != CertificateChangeTarget.Own || change.CertificateId is null)
            throw new InvalidOperationException("Only a scheduled change of our certificate can be announced.");
        return change;
    }

    private async Task<X509Certificate2> LoadAsync(int certificateId, CancellationToken cancellationToken)
    {
        using var loaded = CertificateLoader.Load(await certificateRepository.GetObjectAsync(certificateId, cancellationToken));
        // The public part only: nothing of the private key goes further.
        return X509CertificateLoader.LoadCertificate(loaded.RawData);
    }

    private async Task<MailAttachment> AttachmentAsync(AnnouncementPlan plan, CancellationToken cancellationToken)
    {
        using var certificate = await LoadAsync(plan.Change.CertificateId!.Value, cancellationToken);
        return new MailAttachment(plan.FileName, "application/pkix-cert", certificate.RawData);
    }

    private void Record(TransferEventLevel level, AnnouncedConnection connection, int changeId, string message) =>
        eventLog.Record(new TransferEvent
        {
            Timestamp = timeService.GetCurrentTime(),
            Category = TransferEventCategory.Certificate,
            Type = TransferEventType.CertificateAnnounced,
            Level = level,
            PartnerId = connection.PartnerId,
            PartnerName = connection.PartnerName,
            Message = message.Length > 1000 ? message[..1000] : message,
            Details = level == TransferEventLevel.Information ? Details(changeId) : null,
        });
}
