using Oftp4Net.Core.Protocol;
using Oftp4Net.Domain;
using Oftp4Net.Services.Security;

namespace Oftp4Net.Services.Oftp;

/// <summary>
/// File settings used with one station of a partner: the partner itself or one of its sub-stations, whose settings
/// override the ones of the partner where they are set.
/// </summary>
public sealed record StationSettings
{
    /// <summary>SFID of the station (SFIDDEST of files we send, SFIDORIG of files we receive).</summary>
    public required string Sfid { get; init; }

    /// <summary>The partner the station belongs to.</summary>
    public required Partner Partner { get; init; }

    public PartnerSubStation? SubStation { get; init; }

    public bool SignFiles { get; init; }
    public bool EncryptFiles { get; init; }
    public bool CompressFiles { get; init; }
    public bool RequestSignedEndResponse { get; init; }
    public bool RequireSignedFiles { get; init; }
    public bool RequireEncryptedFiles { get; init; }
    public bool RequireCompressedFiles { get; init; }
    public required string FileCipherSuite { get; init; }

    /// <summary>
    /// Settings for the station <paramref name="sfid"/> of <paramref name="partner"/>; the partner's own settings
    /// when the SFID is empty, the partner's one or not a sub-station of it.
    /// </summary>
    public static StationSettings For(Partner partner, string? sfid)
    {
        var sub = FindSubStation(partner, sfid);
        return new StationSettings
        {
            Sfid = sub?.SFID ?? partner.SFID,
            Partner = partner,
            SubStation = sub,
            SignFiles = sub?.SignFiles ?? partner.SignFiles,
            EncryptFiles = sub?.EncryptFiles ?? partner.EncryptFiles,
            CompressFiles = sub?.CompressFiles ?? partner.CompressFiles,
            RequestSignedEndResponse = sub?.RequestSignedEndResponse ?? partner.RequestSignedEndResponse,
            RequireSignedFiles = sub?.RequireSignedFiles ?? partner.RequireSignedFiles,
            RequireEncryptedFiles = sub?.RequireEncryptedFiles ?? partner.RequireEncryptedFiles,
            RequireCompressedFiles = sub?.RequireCompressedFiles ?? partner.RequireCompressedFiles,
            FileCipherSuite = sub?.FileCipherSuite ?? partner.FileCipherSuite,
        };
    }

    /// <summary>
    /// The partner's certificate for one purpose: the one assigned to this station, otherwise the one assigned to
    /// the partner, otherwise its single certificate (Odette OP08 2.5).
    /// </summary>
    public int? CertificateFor(CertificateUsage usage) =>
        Partner.FindCertificate(Sfid, usage)?.CertificateId ?? Partner.SecurityCertificateId;

    /// <summary>The certificate that was replaced and is still accepted during the roll-over period.</summary>
    public int? PreviousCertificateFor(CertificateUsage usage) =>
        Partner.FindCertificate(Sfid, usage) is { } assignment
            ? assignment.PreviousCertificateId
            : Partner.PreviousSecurityCertificateId;

    public static PartnerSubStation? FindSubStation(Partner partner, string? sfid) =>
        string.IsNullOrWhiteSpace(sfid)
            ? null
            : partner.SubStations?.FirstOrDefault(s => string.Equals(s.SFID.Trim(), sfid.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Checks a file offered by the station against what we require from it; returns the answer reason code and
    /// text of the refusal, or <c>null</c> when the file is acceptable.
    /// </summary>
    public (string ReasonCode, string ReasonText)? CheckIncoming(FileSecurityDescriptor security)
    {
        if (RequireEncryptedFiles && !security.Encrypted)
            return (AnswerReasonCodes.UnencryptedFileNotAllowed, "Files have to be encrypted.");
        if (RequireSignedFiles && !security.Signed)
            return (AnswerReasonCodes.UnsignedFileNotAllowed, "Files have to be signed.");
        if (RequireCompressedFiles && !security.Compressed)
            return (AnswerReasonCodes.UnspecifiedReason, "Files have to be compressed.");
        return null;
    }
}
