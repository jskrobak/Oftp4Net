using MimeKit;
using Oftp4Net.Domain;
using Oftp4Net.Services.Mail;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Tests;

public class CertificateAnnouncementTests
{
    [Fact]
    public void Render_FillsPlaceholdersAndLeavesOutConditionalLinesWithoutValue()
    {
        var text = CertificateAnnouncementTemplate.Render(
            "From {ActivateAt}\nAlso at {PageUrl}{?PageUrl}\nOver OFTP{?Oftp}\n{Unknown} stays",
            new Dictionary<string, string?> { ["ActivateAt"] = "2026-12-14 10:00 UTC+01:00", ["PageUrl"] = null, ["Oftp"] = "yes" });

        Assert.Equal("From 2026-12-14 10:00 UTC+01:00\nOver OFTP\n{Unknown} stays", text);
    }

    [Fact]
    public void DefaultTemplate_HasBothLanguagesWithTheNoteOnTop()
    {
        var values = CertificateAnnouncementTemplate.Placeholders.ToDictionary(p => p.Name, p => (string?)$"<{p.Name}>");

        var text = CertificateAnnouncementTemplate.Render(CertificateAnnouncementTemplate.DefaultBody, values);

        Assert.StartsWith("English version below.", text);
        Assert.True(text.IndexOf("Dobrý den", StringComparison.Ordinal) < text.IndexOf("Dear EDI partner", StringComparison.Ordinal));
        // Every placeholder of the default text is a known one.
        Assert.DoesNotMatch(@"\{[A-Za-z0-9]+\}", text);
        Assert.Equal(2, CountOf(text, "<Connections>"));
        Assert.Equal(2, CountOf(text, "<PageUrl>"));
        Assert.Equal(1, CountOf(text, "<Pem>"));
        Assert.DoesNotContain("{?", text);
    }

    [Fact]
    public void Group_OneMessagePerSetOfRecipients()
    {
        Partner P(string name, params string[] emails) => new()
        {
            Name = name, Contacts = [new PartnerContact { Name = "EDI", Emails = [.. emails] }],
        };
        var seeburger2 = P("SEEBURGER2", "servicedesk@seeburger.de");
        var seeburger3 = P("SEEBURGER3", "ServiceDesk@seeburger.de ");
        var bmw = P("BMW", "edi.support@bmw.de", "otd.edi.onboarding@bmw.de");
        bmw.Contacts.Add(new PartnerContact { Name = "Again", Emails = ["EDI.support@bmw.de"] });
        var none = P("SIGMAPLAST");

        var groups = CertificateAnnouncementService.Group([seeburger3, bmw, none, seeburger2]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(["edi.support@bmw.de", "otd.edi.onboarding@bmw.de"], groups[0].To);
        Assert.Equal(["BMW"], groups[0].Partners.Select(p => p.Name));
        Assert.Equal(["SEEBURGER2", "SEEBURGER3"], groups[1].Partners.Select(p => p.Name));
    }

    [Fact]
    public void Connections_OneLinePerConnection()
    {
        var text = CertificateAnnouncementService.Connections(
        [
            new AnnouncedConnection(1, "VW", "O0013000001VW      KOI", "194.114.33.125", 6619, "O01770000000000X0HU000000", 1),
        ]);

        Assert.Equal("  VW – O0013000001VW      KOI (194.114.33.125:6619) ⇄ O01770000000000X0HU000000", text);
    }

    private static readonly OutgoingMail Mail = new(["edi@partner.example"], "Subject", "Text",
        [new MailAttachment("oftp.example.com-2026.cer", "application/pkix-cert", [1, 2, 3])]);

    private static readonly MailOptions Options = new()
    {
        Provider = MailProvider.Graph, From = "support@example.com", FromName = "Example", ReplyTo = "support@example.com",
        Bcc = "archive@example.com",
        Graph = new GraphMailOptions { TenantId = "t", ClientId = "c", ClientSecret = "s", Mailbox = "mailer@example.com" },
        Smtp = new SmtpMailOptions { Host = "smtp.example.com" },
    };

    [Fact]
    public void Smtp_MessageHasSenderReplyToCopyAndAttachment()
    {
        var message = MailService.BuildMimeMessage(Options, Mail);

        Assert.Equal("support@example.com", message.From.Mailboxes.Single().Address);
        Assert.Equal("support@example.com", message.ReplyTo.Mailboxes.Single().Address);
        Assert.Equal("archive@example.com", message.Bcc.Mailboxes.Single().Address);
        var attachment = Assert.Single(message.Attachments.OfType<MimePart>());
        Assert.Equal("oftp.example.com-2026.cer", attachment.FileName);
        Assert.Equal("Text", message.TextBody);
    }

    [Fact]
    public void Graph_MessageIsSentFromTheMailboxAsTheSender()
    {
        var json = MailService.BuildGraphMessage(Options, Mail);

        Assert.Equal("support@example.com", (string?)json["message"]!["from"]!["emailAddress"]!["address"]);
        Assert.Equal("support@example.com", (string?)json["message"]!["replyTo"]![0]!["emailAddress"]!["address"]);
        Assert.Equal("archive@example.com", (string?)json["message"]!["bccRecipients"]![0]!["emailAddress"]!["address"]);
        Assert.Equal("AQID", (string?)json["message"]!["attachments"]![0]!["contentBytes"]);
        Assert.Equal("Text", (string?)json["message"]!["body"]!["contentType"]);
        Assert.True((bool)json["saveToSentItems"]!);
    }

    [Fact]
    public void Validate_SaysWhatIsMissing()
    {
        Assert.NotNull(MailService.Validate(new MailOptions()));
        Assert.Equal("Mail:From is missing.", MailService.Validate(new MailOptions { Provider = MailProvider.Smtp, Smtp = { Host = "smtp" } }));
        Assert.Equal("Mail:Graph:Mailbox is missing.", MailService.Validate(new MailOptions
        {
            Provider = MailProvider.Graph, Graph = { TenantId = "t", ClientId = "c", ClientSecret = "s" },
        }));
        Assert.Null(MailService.Validate(Options));
    }

    private static int CountOf(string text, string value) =>
        (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
}
