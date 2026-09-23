namespace Oftp4Net.Services.Security;

/// <summary>The three exchanges of the automatic certificate exchange (Odette OP08 2.5).</summary>
public enum CertificateExchangeKind
{
    /// <summary>Our certificate together with the wish to get the partner's one back.</summary>
    Request,

    /// <summary>Our certificate, as an answer to a request or on our own (e.g. before the old one expires).</summary>
    Deliver,

    /// <summary>Our new certificate; the previous one must not be used any more (e.g. after a compromise).</summary>
    Replace,
}

/// <summary>
/// Which certificates received from partners over OFTP are taken over without the administrator.
/// </summary>
public enum CertificateExchangeMode
{
    /// <summary>Certificate files are refused with a NERP.</summary>
    Off = 0,

    /// <summary>
    /// Only a certificate that belongs to one the partner already has here (the same logical identification data)
    /// is taken over: renewals, roll-overs and replacements. An unknown certificate is answered with a NERP and has
    /// to be assigned by the administrator.
    /// </summary>
    Known = 1,

    /// <summary>
    /// A certificate of a partner that has none here yet is taken over as well, provided its chain of trust ends
    /// with a certification authority of the Odette TSL or of the operating system (initial exchange).
    /// </summary>
    Trusted = 2,
}

/// <summary>
/// Virtual files of the automatic certificate exchange (Odette OP08 2.5). They carry one certificate in DER and are
/// always transferred unsecured: SFIDFMT U, SFIDSEC 00, SFIDCIPH 0, SFIDCOMP 0, SFIDENV 0, SFIDSIGN N.
/// </summary>
public static class CertificateExchange
{
    public const string RequestFileName = "ODETTE_CERTIFICATE_REQUEST";
    public const string DeliverFileName = "ODETTE_CERTIFICATE_DELIVER";
    public const string ReplaceFileName = "ODETTE_CERTIFICATE_REPLACE";

    public static string FileName(CertificateExchangeKind kind) => kind switch
    {
        CertificateExchangeKind.Request => RequestFileName,
        CertificateExchangeKind.Deliver => DeliverFileName,
        CertificateExchangeKind.Replace => ReplaceFileName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The exchange a virtual file name stands for, <c>null</c> when it is an ordinary file.</summary>
    public static CertificateExchangeKind? KindOf(string? virtualFileName)
    {
        var name = virtualFileName?.Trim();
        if (string.Equals(name, RequestFileName, StringComparison.OrdinalIgnoreCase))
            return CertificateExchangeKind.Request;
        if (string.Equals(name, DeliverFileName, StringComparison.OrdinalIgnoreCase))
            return CertificateExchangeKind.Deliver;
        if (string.Equals(name, ReplaceFileName, StringComparison.OrdinalIgnoreCase))
            return CertificateExchangeKind.Replace;
        return null;
    }

    public static bool IsCertificateFile(string? virtualFileName) => KindOf(virtualFileName) is not null;
}
