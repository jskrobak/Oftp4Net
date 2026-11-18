using Oftp4Net.Domain;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Tests;

public class CertificateQuestionnaireTests
{
    private static Dictionary<string, string?> Form(params (string Key, string? Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value);

    [Fact]
    public void Parse_ReadsACompleteAnswer()
    {
        var (answer, problems) = CertificateQuestionnaireService.Parse(Form(
            ("takeover", "will"), ("planned", "2026-12-10"), ("tls", "pinned"), ("ipfilter", "yes"),
            ("ips", " 203.0.113.10 "), ("name", "EDI team"), ("email", "edi@partner.example"), ("phone", ""), ("note", "Thanks")));

        Assert.Empty(problems);
        Assert.Equal(new QuestionnaireAnswer(AnnouncementTakeover.WillImport, new DateTime(2026, 12, 10), AnnouncementTlsTrust.Pinned, true,
            "203.0.113.10", "EDI team", "edi@partner.example", null, "Thanks"), answer);
    }

    [Fact]
    public void Parse_RefusesAMissingChoiceAWrongDateAndAWrongAddress()
    {
        var (answer, problems) = CertificateQuestionnaireService.Parse(Form(("planned", "10.12.2026"), ("email", "not an address")));

        Assert.Null(answer);
        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void Parse_UnknownValuesAreNotGuessed()
    {
        var (answer, _) = CertificateQuestionnaireService.Parse(Form(("takeover", "done"), ("tls", "whatever"), ("ipfilter", "unknown")));

        Assert.Equal((AnnouncementTlsTrust.Unknown, (bool?)null), (answer!.TlsTrust, answer.FiltersByIp));
    }

    [Fact]
    public void Parse_RefusesTooLongTexts()
    {
        var (answer, problems) = CertificateQuestionnaireService.Parse(Form(("takeover", "done"), ("note", new string('x', 2001))));

        Assert.Null(answer);
        Assert.Single(problems);
    }

    [Fact]
    public void Token_IsLongRandomAndUrlSafe()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => CertificateAnnouncementService.NewToken()).ToList();

        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{43}$", t));
        Assert.Equal(100, tokens.Distinct().Count());
        Assert.Equal("https://oftp.example.com/cert/confirm/abc", CertificateAnnouncementService.QuestionnaireUrl("https://oftp.example.com/", "abc"));
    }

    [Fact]
    public void Questionnaire_AnswersUntilAMonthAfterTheOldCertificateIsGivenUp()
    {
        var change = new CertificateChange { ActivateAt = new DateTime(2026, 12, 14, 10, 0, 0), KeepPreviousUntil = new DateTime(2026, 12, 31, 23, 59, 0) };

        Assert.Equal(new DateTime(2027, 1, 31, 23, 59, 0), CertificateAnnouncementService.QuestionnaireExpiry(change));
        change.KeepPreviousUntil = null;
        Assert.Equal(new DateTime(2027, 1, 14, 10, 0, 0), CertificateAnnouncementService.QuestionnaireExpiry(change));
    }

    [Fact]
    public void Describe_SumsTheAnswerUpForTheLog()
    {
        var text = CertificateQuestionnaireService.Describe(new QuestionnaireAnswer(AnnouncementTakeover.WillImport, new DateTime(2026, 12, 10),
            AnnouncementTlsTrust.CertificationAuthority, false, null, "EDI", "edi@partner.example", null, null));

        Assert.Equal("will import by 2026-12-10; TLS: CertificationAuthority; no IP filter; contact EDI <edi@partner.example>", text);
    }
}
