using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Havit.Data.Patterns.UnitOfWorks;
using Havit.Services.TimeServices;
using Oftp4Net.DataLayer.Repositories;
using Oftp4Net.Domain;
using Oftp4Net.Services.Oftp;
using Oftp4Net.Services.TransferEvents;

namespace Oftp4Net.Services.Security;

/// <summary>What the questionnaire shows a partner.</summary>
public sealed record QuestionnaireView(
    string? Company, string? Host, int? Port, IReadOnlyList<string> OurIps, IReadOnlyList<AnnouncedConnection> Connections,
    DateTimeOffset ActivateAt, DateTimeOffset? PreviousUntil, string CertificateSubject, string SerialNumber, string Sha256,
    string? CertificateKey, CertificateAnnouncementLink Answer);

/// <summary>A partner's answer as it came in, checked.</summary>
public sealed record QuestionnaireAnswer(
    AnnouncementTakeover Takeover, DateTime? PlannedAt, AnnouncementTlsTrust TlsTrust, bool? FiltersByIp,
    string? ContactName, string? ContactEmail, string? ContactPhone, string? Note);

/// <summary>
/// The questionnaire partners reach from the announcement of our new certificate: they confirm the change and tell us
/// how they check our TLS certificate, whether they filter by IP address and whom to contact. Answers come without
/// sign in, so they are only stored, shown and logged; a contact goes to the partners only when an administrator
/// takes it over.
/// </summary>
public class CertificateQuestionnaireService(
    ICertificateAnnouncementLinkRepository linkRepository,
    ICertificateRepository certificateRepository,
    IPartnerRepository partnerRepository,
    CertificateAnnouncementService announcements,
    GlobalSettingsService settingsService,
    ApplicationTimeService time,
    ITimeService timeService,
    IUnitOfWork unitOfWork,
    ITransferEventLog eventLog)
{
    /// <summary>The questionnaire, <c>null</c> when the token is unknown, expired or its change is no longer scheduled.</summary>
    public async Task<QuestionnaireView?> GetAsync(string token, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(token, cancellationToken) is not { } link)
            return null;

        var change = link.CertificateChange!;
        var settings = await settingsService.GetGlobalSettingsAsync();
        var profile = settings.StationProfile;
        using var loaded = CertificateLoader.Load(await certificateRepository.GetObjectAsync(change.CertificateId!.Value, cancellationToken));
        using var certificate = X509CertificateLoader.LoadCertificate(loaded.RawData);
        var replacedValidTo = change.ReplacedCertificateId is { } replacedId
            ? (await certificateRepository.GetObjectAsync(replacedId, cancellationToken)).ValidTo
            : (DateTime?)null;
        var previousUntil = change.KeepPreviousUntil ?? replacedValidTo;

        return new QuestionnaireView(
            profile.CompanyName, profile.PublicHost, profile.PublicPort, profile.OutboundIps,
            await announcements.ConnectionsAsync(link.PartnerIds, cancellationToken),
            time.ToInstant(change.ActivateAt), previousUntil is { } until ? time.ToInstant(until) : null,
            certificate.Subject, certificate.SerialNumber,
            string.Join(':', SHA256.HashData(certificate.RawData).Select(b => b.ToString("X2"))),
            settings.PublishCertificates ? PublicCertificateService.Key(certificate) : null,
            link);
    }

    /// <summary>Stores the answer; <c>false</c> when the questionnaire answers no more.</summary>
    public async Task<bool> SaveAsync(string token, QuestionnaireAnswer answer, string? remoteAddress, CancellationToken cancellationToken = default)
    {
        if (await FindAsync(token, cancellationToken) is not { } link)
            return false;

        link.RespondedAt = timeService.GetCurrentTime();
        link.RemoteAddress = Truncate(remoteAddress, 100);
        link.Takeover = answer.Takeover;
        link.PlannedAt = answer.Takeover == AnnouncementTakeover.WillImport ? answer.PlannedAt : null;
        link.TlsTrust = answer.TlsTrust;
        link.FiltersByIp = answer.FiltersByIp;
        link.ContactName = answer.ContactName;
        link.ContactEmail = answer.ContactEmail;
        link.ContactPhone = answer.ContactPhone;
        link.Note = answer.Note;
        link.ContactTakenOverAt = null;
        unitOfWork.AddForUpdate(link);
        await unitOfWork.CommitAsync(cancellationToken);

        var level = answer.Takeover == AnnouncementTakeover.NeedsHelp ? TransferEventLevel.Warning : TransferEventLevel.Information;
        foreach (var partner in (await partnerRepository.GetAllAsync(cancellationToken)).Where(p => link.PartnerIds.Contains(p.Id)))
            eventLog.Record(new TransferEvent
            {
                Timestamp = link.RespondedAt.Value,
                Category = TransferEventCategory.Certificate,
                Type = TransferEventType.CertificateAnnouncementAnswered,
                Level = level,
                PartnerId = partner.Id,
                PartnerName = partner.Name,
                RemoteEndPoint = link.RemoteAddress,
                Message = Truncate($"Partner {partner.Name} answered the questionnaire about our new certificate: {Describe(answer)}", 1000)!,
            });
        return true;
    }

    /// <summary>
    /// Adds the contact of the answer to the partners of the questionnaire, unless one of them has that e-mail address
    /// already; returns the names of the partners it was added to.
    /// </summary>
    public async Task<List<string>> TakeOverContactAsync(int linkId, string? actor, CancellationToken cancellationToken = default)
    {
        var link = await linkRepository.GetObjectAsync(linkId, cancellationToken);
        if (string.IsNullOrWhiteSpace(link.ContactEmail) && string.IsNullOrWhiteSpace(link.ContactPhone))
            throw new InvalidOperationException("The answer has no contact.");

        var added = new List<string>();
        foreach (var partner in (await partnerRepository.GetAllAsync(cancellationToken)).Where(p => link.PartnerIds.Contains(p.Id)))
        {
            if (link.ContactEmail is { } email &&
                partner.Contacts.SelectMany(c => c.Emails).Any(e => string.Equals(e.Trim(), email, StringComparison.OrdinalIgnoreCase)))
                continue;

            partner.Contacts = [.. partner.Contacts, new PartnerContact
            {
                Name = string.IsNullOrWhiteSpace(link.ContactName) ? "EDI" : link.ContactName,
                Description = $"From the questionnaire about our certificate, {link.RespondedAt:d}",
                Emails = link.ContactEmail is { } address ? [address] : [],
                Phones = link.ContactPhone is { } phone ? [phone] : [],
            }];
            unitOfWork.AddForUpdate(partner);
            added.Add(partner.Name);
        }

        link.ContactTakenOverAt = timeService.GetCurrentTime();
        unitOfWork.AddForUpdate(link);
        await unitOfWork.CommitAsync(cancellationToken);

        foreach (var name in added)
            eventLog.Record(new TransferEvent
            {
                Timestamp = link.ContactTakenOverAt.Value,
                Category = TransferEventCategory.Configuration,
                Type = TransferEventType.PartnerChanged,
                Level = TransferEventLevel.Information,
                PartnerName = name,
                Message = $"Contact {link.ContactName} <{link.ContactEmail}> of partner {name} taken over from its questionnaire" +
                          (actor is null ? "" : $" by {actor}"),
            });
        return added;
    }

    /// <summary>
    /// Reads and checks the form of the questionnaire. Returns the answer, or the problems when it cannot be stored.
    /// </summary>
    public static (QuestionnaireAnswer? Answer, List<string> Problems) Parse(IReadOnlyDictionary<string, string?> form)
    {
        var problems = new List<string>();
        string? Text(string name, int length)
        {
            var value = form.GetValueOrDefault(name)?.Trim();
            if (string.IsNullOrEmpty(value))
                return null;
            if (value.Length > length)
                problems.Add($"{name}: at most {length} characters.");
            return value.Length > length ? value[..length] : value;
        }

        AnnouncementTakeover? takeover = form.GetValueOrDefault("takeover") switch
        {
            "will" => AnnouncementTakeover.WillImport,
            "done" => AnnouncementTakeover.Imported,
            "help" => AnnouncementTakeover.NeedsHelp,
            _ => null,
        };
        if (takeover is null)
            problems.Add("Please choose whether you will take the certificate over.");

        DateTime? plannedAt = null;
        if (form.GetValueOrDefault("planned") is { Length: > 0 } planned)
        {
            if (DateTime.TryParseExact(planned.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                plannedAt = date;
            else
                problems.Add("Please enter the date as YYYY-MM-DD.");
        }

        var tls = form.GetValueOrDefault("tls") switch
        {
            "ca" => AnnouncementTlsTrust.CertificationAuthority,
            "pinned" => AnnouncementTlsTrust.Pinned,
            "none" => AnnouncementTlsTrust.NotChecked,
            _ => AnnouncementTlsTrust.Unknown,
        };
        bool? filters = form.GetValueOrDefault("ipfilter") switch { "yes" => true, "no" => false, _ => null };

        var email = Text("email", 200);
        if (email is not null && !MailAddress.TryCreate(email, out _))
            problems.Add("Please enter a valid e-mail address.");

        var answer = new QuestionnaireAnswer(takeover ?? AnnouncementTakeover.WillImport, plannedAt, tls, filters,
            Text("name", 200), email, Text("phone", 100), Text("note", 2000));
        return (problems.Count == 0 ? answer : null, problems);
    }

    /// <summary>The answer in one line, for the log.</summary>
    public static string Describe(QuestionnaireAnswer a) => string.Join("; ", new[]
    {
        a.Takeover switch
        {
            AnnouncementTakeover.Imported => "imported already",
            AnnouncementTakeover.NeedsHelp => "needs help",
            _ => a.PlannedAt is { } at ? $"will import by {at:yyyy-MM-dd}" : "will import",
        },
        $"TLS: {a.TlsTrust}",
        a.FiltersByIp switch { true => "filters by IP", false => "no IP filter", _ => null },
        a.ContactEmail is { } email ? $"contact {a.ContactName} <{email}>" : null,
        a.Note is { } note ? $"note: {note}" : null,
    }.Where(s => s is not null));

    private async Task<CertificateAnnouncementLink?> FindAsync(string token, CancellationToken cancellationToken)
    {
        // Tokens are 43 characters of URL safe base64; anything else is not looked up at all.
        if (token.Length != 43 || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            return null;

        var link = await linkRepository.FindByTokenAsync(token, cancellationToken);
        return link is { CertificateChange: { Status: CertificateChangeStatus.Scheduled or CertificateChangeStatus.Applied, CertificateId: not null } }
               && link.ExpiresAt > timeService.GetCurrentTime()
            ? link
            : null;
    }

    private static string? Truncate(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}
