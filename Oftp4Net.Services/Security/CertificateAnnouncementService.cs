using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Havit.Data.Patterns.UnitOfWorks;
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

/// <summary>Our certificate sent to a partner over OFTP and what became of it.</summary>
public sealed record OftpDelivery(string PartnerName, SendStatus Status, DateTime? DeliveredAt);

/// <summary>One e-mail of an announcement: the partners that share the same recipients get one message together.</summary>
/// <param name="Key">Identifies the message among the others of the change (the recipients).</param>
/// <param name="SentAt">When it was sent already, <c>null</c> when not yet.</param>
/// <param name="Questionnaire">The questionnaire of the message with the last answer.</param>
/// <param name="Deliveries">Our certificate sent to its partners over OFTP since the change was scheduled.</param>
public sealed record AnnouncementMail(
    string Key, IReadOnlyList<string> To, IReadOnlyList<AnnouncedConnection> Connections, string Subject, string Body, DateTime? SentAt,
    CertificateAnnouncementLink Questionnaire, IReadOnlyList<OftpDelivery> Deliveries);

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
    ICertificateAnnouncementLinkRepository linkRepository,
    ISendQueueItemRepository sendQueueRepository,
    IUnitOfWork unitOfWork,
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
    /// <summary>
    /// The messages of the announcement of change <paramref name="changeId"/>. Each one gets its questionnaire, created
    /// the first time the message is prepared and kept for the same recipients.
    /// </summary>
    /// <param name="pageUrl">Address of the public certificate page, <c>null</c> when it is not to be named.</param>
    /// <param name="questionnaireBaseUrl">
    /// Public address of the application the questionnaires are reached at (e.g. https://oftp.example.com/),
    /// <c>null</c> to leave them out of the text.
    /// </param>
    /// <param name="viaOftp">Whether the text says that the certificate is sent over OFTP as well.</param>
    public async Task<AnnouncementPlan> PlanAsync(int changeId, string? pageUrl, string? questionnaireBaseUrl, bool viaOftp,
        CancellationToken cancellationToken = default)
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
        var groups = Group(partners);
        var links = await EnsureQuestionnairesAsync(change, groups, cancellationToken);
        var deliveries = (await sendQueueRepository.GetCertificateDeliveriesAsync(partners.Select(p => p.Id).ToList(), change.Created, cancellationToken))
            .ToLookup(d => d.PartnerId);
        var subjectTemplate = string.IsNullOrWhiteSpace(settings.CertificateAnnouncementSubject)
            ? CertificateAnnouncementTemplate.DefaultSubject : settings.CertificateAnnouncementSubject;
        var bodyTemplate = string.IsNullOrWhiteSpace(settings.CertificateAnnouncementBody)
            ? CertificateAnnouncementTemplate.DefaultBody : settings.CertificateAnnouncementBody;

        var mails = new List<AnnouncementMail>();
        foreach (var group in groups)
        {
            var connections = group.Partners.Select(p =>
            {
                var identity = identities.FirstOrDefault(i => i.Id == p.InboundIdentityId) ?? defaultIdentity;
                return new AnnouncedConnection(p.Id, p.Name, p.SSID.Trim(), p.Host, p.Port, identity?.SSID.Trim() ?? "", identity?.Id);
            }).ToList();

            var key = Key(group.To);
            var link = links[key];
            var mailValues = new Dictionary<string, string?>(values)
            {
                ["Connections"] = Connections(connections),
                ["ConfirmUrl"] = string.IsNullOrWhiteSpace(questionnaireBaseUrl) ? null : QuestionnaireUrl(questionnaireBaseUrl, link.Token),
            };
            var sentAt = group.Partners.Select(p => sent.TryGetValue(p.Id, out var at) ? at : (DateTime?)null).Max();
            var oftp = group.Partners.SelectMany(p => deliveries[p.Id].Select(d => new OftpDelivery(p.Name, d.Status, d.DeliveredDate))).ToList();
            mails.Add(new AnnouncementMail(key, group.To, connections,
                CertificateAnnouncementTemplate.Render(subjectTemplate, mailValues),
                CertificateAnnouncementTemplate.Render(bodyTemplate, mailValues),
                sentAt, link, oftp));
        }

        var withoutContact = partners.Where(p => Recipients(p).Count == 0).Select(p => p.Name).Order().ToList();
        return new AnnouncementPlan(change, certificate.Subject, values["FileName"]!, mails, withoutContact);
    }

    /// <summary>Sends the messages with the given keys; a failure of one does not stop the others.</summary>
    public async Task<List<AnnouncementResult>> SendAsync(int changeId, string? pageUrl, string? questionnaireBaseUrl, bool viaOftp,
        IReadOnlyCollection<string> keys, string? actor, CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(changeId, pageUrl, questionnaireBaseUrl, viaOftp, cancellationToken);
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
    public async Task SendTestAsync(int changeId, string? pageUrl, string? questionnaireBaseUrl, bool viaOftp, string key, string address,
        CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(changeId, pageUrl, questionnaireBaseUrl, viaOftp, cancellationToken);
        var mail = plan.Mails.FirstOrDefault(m => m.Key == key) ?? throw new InvalidOperationException("The message is not part of the announcement.");
        var attachment = await AttachmentAsync(plan, cancellationToken);
        await mailService.SendAsync(new OutgoingMail([address.Trim()],
            $"[TEST for {string.Join(", ", mail.To)}] {mail.Subject}", mail.Body, [attachment]), cancellationToken);
    }

    /// <summary>The address of a questionnaire behind the public address of the application.</summary>
    public static string QuestionnaireUrl(string baseUrl, string token) => $"{baseUrl.TrimEnd('/')}/cert/confirm/{token}";

    /// <summary>A token nobody guesses: 32 random bytes in URL safe base64.</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>How long a questionnaire answers: until a month after the replaced certificate is given up.</summary>
    public static DateTime QuestionnaireExpiry(CertificateChange change) =>
        (change.KeepPreviousUntil is { } until && until > change.ActivateAt ? until : change.ActivateAt).AddMonths(1);

    /// <summary>
    /// Questionnaires are created one preparation at a time: a page is prepared twice when it is opened (prerendering
    /// and the interactive circuit), and both must not create one for the same recipients.
    /// </summary>
    private static readonly SemaphoreSlim QuestionnaireLock = new(1, 1);

    /// <summary>The questionnaire of every message, created when it is prepared for the first time.</summary>
    private async Task<Dictionary<string, CertificateAnnouncementLink>> EnsureQuestionnairesAsync(CertificateChange change,
        List<(IReadOnlyList<string> To, List<Partner> Partners)> groups, CancellationToken cancellationToken)
    {
        await QuestionnaireLock.WaitAsync(cancellationToken);
        try
        {
            return await EnsureQuestionnairesLockedAsync(change, groups, cancellationToken);
        }
        finally
        {
            QuestionnaireLock.Release();
        }
    }

    private async Task<Dictionary<string, CertificateAnnouncementLink>> EnsureQuestionnairesLockedAsync(CertificateChange change,
        List<(IReadOnlyList<string> To, List<Partner> Partners)> groups, CancellationToken cancellationToken)
    {
        // Should there be more for the same recipients, the one with an answer wins.
        var existing = (await linkRepository.GetByChangeAsync(change.Id, cancellationToken))
            .GroupBy(l => l.RecipientKey)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.RespondedAt.HasValue).ThenBy(l => l.Id).First());
        var expiresAt = QuestionnaireExpiry(change);
        var result = new Dictionary<string, CertificateAnnouncementLink>();
        var changed = false;
        foreach (var group in groups)
        {
            var key = Key(group.To);
            var partnerIds = group.Partners.Select(p => p.Id).Order().ToList();
            if (existing.TryGetValue(key, out var link))
            {
                if (!link.PartnerIds.Order().SequenceEqual(partnerIds) || link.ExpiresAt != expiresAt)
                {
                    link.PartnerIds = partnerIds;
                    link.ExpiresAt = expiresAt;
                    unitOfWork.AddForUpdate(link);
                    changed = true;
                }
            }
            else
            {
                link = new CertificateAnnouncementLink
                {
                    CertificateChangeId = change.Id,
                    RecipientKey = key,
                    PartnerIds = partnerIds,
                    Token = NewToken(),
                    Created = timeService.GetCurrentTime(),
                    ExpiresAt = expiresAt,
                };
                unitOfWork.AddForInsert(link);
                changed = true;
            }

            result[key] = link;
        }

        if (changed)
            await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>The recipients as one value: the key of the message and of its questionnaire.</summary>
    private static string Key(IReadOnlyList<string> to) => string.Join(",", to).ToLowerInvariant();

    /// <summary>The connections of the partners, as the announcement names them.</summary>
    public async Task<List<AnnouncedConnection>> ConnectionsAsync(IReadOnlyCollection<int> partnerIds, CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.GetGlobalSettingsAsync();
        var identities = await identityRepository.GetAllAsync(cancellationToken);
        var defaultIdentity = DefaultIdentity(settings, identities, await listenerRepository.GetAllAsync(cancellationToken));
        return (await partnerRepository.GetAllAsync(cancellationToken)).Where(p => partnerIds.Contains(p.Id)).OrderBy(p => p.Name).Select(p =>
        {
            var identity = identities.FirstOrDefault(i => i.Id == p.InboundIdentityId) ?? defaultIdentity;
            return new AnnouncedConnection(p.Id, p.Name, p.SSID.Trim(), p.Host, p.Port, identity?.SSID.Trim() ?? "", identity?.Id);
        }).ToList();
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
