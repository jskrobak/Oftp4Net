using System.Text;
using System.Text.RegularExpressions;

namespace Oftp4Net.Services.Security;

/// <summary>
/// The e-mail announcing a new certificate of ours to a partner: a subject and a plain text with placeholders.
/// <para>
/// <c>{Name}</c> is replaced by its value (see <see cref="Placeholders"/>). A line that contains <c>{?Name}</c> is
/// left out when the value is empty, e.g. the line with the address of the certificate page when it is not
/// published; the marker itself is removed. An unknown placeholder stays as it is, so that a typo shows.
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
        ("Pem", "the new certificate in PEM"),
    ];

    public const string DefaultSubject =
        "Změna OFTP2 certifikátu / OFTP2 certificate change – {Company} – {ActivateAt}";

    public const string DefaultBody = """
        English version below.

        Dobrý den,

        od {ActivateAt} budeme pro OFTP2 komunikaci s vámi používat nový certifikát vydaný {IssuerName}.
        Týká se těchto spojení (partner – vaše Odette ID (host) ⇄ naše Odette ID):

        {Connections}

        Prosíme, nahrajte si nový certifikát do svého OFTP2 systému před tímto termínem:
        - pro šifrování souborů, které nám posíláte, a pro ověřování našich podpisů,
        - pro TLS, pokud máte náš certifikát připnutý; důvěřujete-li kořenové autoritě vydavatele, pro TLS nemusíte dělat nic.
        Soubory zašifrované pro dosavadní certifikát (sériové číslo {ReplacedSerialNumber}) přijmeme ještě do {PreviousUntil}.{?PreviousUntil}

        Nový certifikát je v příloze ({FileName}) a na konci tohoto e-mailu. Před importem prosím porovnejte jeho otisk:
          Subjekt:       {Subject}
          Vydavatel:     {Issuer}
          Sériové číslo: {SerialNumber}
          Platnost:      {ValidFrom} – {ValidTo}
          SHA-256:       {Sha256}
        Ke stažení je také na {PageUrl}{?PageUrl}
        Certifikát vám zároveň posíláme přes OFTP (ODETTE_CERTIFICATE_DELIVER).{?Oftp}

        Převzetí certifikátu nám prosím potvrďte odpovědí na tento e-mail.

        S pozdravem
        {Company}

        ------------------------------------------------------------

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

        Please confirm that you have taken the certificate over by replying to this e-mail.

        Best regards
        {Company}

        ------------------------------------------------------------

        {Pem}
        """;

    /// <summary>Fills the placeholders of <paramref name="template"/> with <paramref name="values"/>.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values)
    {
        var result = new StringBuilder();
        var lines = template.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var conditions = Condition().Matches(line).Select(m => m.Groups[1].Value).ToList();
            if (conditions.Any(name => string.IsNullOrEmpty(values.GetValueOrDefault(name))))
                continue;

            line = Condition().Replace(line, "");
            line = Placeholder().Replace(line, m => values.TryGetValue(m.Groups[1].Value, out var value) ? value ?? "" : m.Value);
            result.Append(line);
            if (i < lines.Length - 1)
                result.Append('\n');
        }

        return result.ToString();
    }

    [GeneratedRegex(@"\{\?([A-Za-z0-9]+)\}")]
    private static partial Regex Condition();

    [GeneratedRegex(@"\{([A-Za-z0-9]+)\}")]
    private static partial Regex Placeholder();
}
