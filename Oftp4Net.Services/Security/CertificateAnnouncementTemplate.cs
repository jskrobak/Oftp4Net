using System.Text.RegularExpressions;

namespace Oftp4Net.Services.Security;

/// <summary>
/// The e-mail announcing a new certificate of ours to a partner: a subject and a plain text with placeholders.
/// <para>
/// <c>{Name}</c> is replaced by its value (see <see cref="Placeholders"/>). A line that contains <c>{?Name}</c> is
/// left out when the value is empty, e.g. the line with the address of the certificate page when it is not
/// published, and a line with <c>{!Name}</c> when it is not empty (the text used instead); the markers themselves
/// are removed. An unknown placeholder stays as it is, so that a typo shows.
/// </para>
/// </summary>
public static partial class CertificateAnnouncementTemplate
{
    /// <summary>The placeholders and what they hold, for the editor.</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> Placeholders =
    [
        ("Company", "our company (station profile), else the name of our identity"),
        ("Host", "host partners call"),
        ("Port", "port partners call"),
        ("Connections", "the partner's connections, one per line: partner – its Odette ID (host) ⇄ our Odette ID"),
        ("ActivateAt", "time the new certificate is used from"),
        ("PreviousUntil", "until when files encrypted for the replaced certificate are still accepted"),
        ("Subject", "subject of the new certificate"),
        ("Issuer", "issuer of the new certificate (distinguished name)"),
        ("IssuerName", "issuer of the new certificate (its common name)"),
        ("SerialNumber", "serial number of the new certificate"),
        ("ValidFrom", "start of validity of the new certificate"),
        ("ValidTo", "end of validity of the new certificate"),
        ("Sha256", "SHA-256 fingerprint of the new certificate"),
        ("Sha1", "SHA-1 fingerprint of the new certificate"),
        ("ReplacedSubject", "subject of the replaced certificate"),
        ("ReplacedSerialNumber", "serial number of the replaced certificate"),
        ("FileName", "name of the attached certificate file"),
        ("PageUrl", "address of the public certificate page, empty when it is not published"),
        ("Oftp", "not empty when the certificate is sent over OFTP as well"),
        ("ConfirmUrl", "address of the questionnaire of the message, empty without one"),
        ("Pem", "the new certificate in PEM"),
    ];

    public const string DefaultSubject = "OFTP2 certificate change – {Company} – {ActivateAt}";

    public const string DefaultBody = """
        Dear EDI partner,

        from {ActivateAt} we will use a new certificate issued by {IssuerName} for our OFTP2 communication with you.
        It concerns these connections (partner – your Odette ID (host) ⇄ our Odette ID):

        {Connections}

        Please import the new certificate into your OFTP2 system before that time:
        - for encrypting the files you send us and for verifying our signatures,
        - for TLS if you have pinned our certificate; if you trust the root authority of the issuer, nothing needs to be done for TLS.
        We will still accept files encrypted for the current certificate (serial number {ReplacedSerialNumber}) until {PreviousUntil}.{?PreviousUntil}

        The new certificate is attached ({FileName}) and included at the end of this e-mail. Please compare its fingerprint before importing it:
          Subject:       {Subject}
          Issuer:        {Issuer}
          Serial number: {SerialNumber}
          Valid:         {ValidFrom} – {ValidTo}
          SHA-256:       {Sha256}
        It can also be downloaded from {PageUrl}{?PageUrl}
        We also send it to you over OFTP (ODETTE_CERTIFICATE_DELIVER).{?Oftp}

        Please confirm that you take the certificate over in a short questionnaire (about a minute; it also asks about IP whitelisting):{?ConfirmUrl}
        {ConfirmUrl}{?ConfirmUrl}
        For questions, please reply to this e-mail.{?ConfirmUrl}
        Please confirm that you have taken the certificate over by replying to this e-mail.{!ConfirmUrl}

        Best regards
        {Company}

        ------------------------------------------------------------

        {Pem}
        """;

    /// <summary>Fills the placeholders of <paramref name="template"/> with <paramref name="values"/>.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        var kept = new List<string>();
        foreach (var line in template.Replace("\r\n", "\n").Split('\n'))
        {
            // {?Name} keeps the line only with a value, {!Name} only without one.
            if (Condition().Matches(line).Any(m => string.IsNullOrEmpty(values.GetValueOrDefault(m.Groups[2].Value)) == (m.Groups[1].Value == "?")))
                continue;

            var text = Condition().Replace(line, "");
            kept.Add(Placeholder().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value ?? "" : m.Value));
        }

        return string.Join('\n', kept);
    }

    [GeneratedRegex(@"\{([?!])([A-Za-z0-9]+)\}")]
    private static partial Regex Condition();

    [GeneratedRegex(@"\{([A-Za-z0-9]+)\}")]
    private static partial Regex Placeholder();
}
