using Oftp4Net.Domain;

namespace Oftp4Net.Services.Oftp;

/// <summary>
/// Adding, changing and removing sub-stations of a partner by hand. The list of sub-stations is replaced, so that
/// the change of the JSON column is detected; certificates assigned to a sub-station follow a change of its SFID.
/// </summary>
public static class SubStationEditor
{
    /// <summary>Longest ODETTE file identification (SFIDDEST / SFIDORIG).</summary>
    public const int MaxSfidLength = 25;

    /// <summary>
    /// Why <paramref name="edited"/> cannot be stored as a sub-station of <paramref name="partner"/>, or <c>null</c>.
    /// <paramref name="originalSfid"/> is the SFID of the sub-station being changed, empty for a new one.
    /// </summary>
    public static string? Validate(Partner partner, string? originalSfid, PartnerSubStation edited)
    {
        var sfid = edited.SFID.Trim();
        if (string.IsNullOrEmpty(edited.Name.Trim()))
            return "The sub-station needs a name.";
        if (string.IsNullOrEmpty(sfid))
            return "The sub-station needs an SFID.";
        if (sfid.Length > MaxSfidLength)
            return $"The SFID is longer than {MaxSfidLength} characters.";
        // Files to the partner's own SFID go to the partner itself.
        if (string.Equals(sfid, partner.SFID.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"{sfid} is the SFID of the partner {partner.Name} itself.";
        if (partner.SubStations.Any(s => !Same(s.SFID, originalSfid) && Same(s.SFID, sfid)))
            return $"The partner {partner.Name} has a sub-station with the SFID {sfid} already.";
        return null;
    }

    /// <summary>
    /// Stores <paramref name="edited"/> in place of the sub-station <paramref name="originalSfid"/>, or adds it when
    /// the SFID is empty. Call <see cref="Validate"/> first.
    /// </summary>
    public static void Save(Partner partner, string? originalSfid, PartnerSubStation edited)
    {
        edited.Name = edited.Name.Trim();
        edited.SFID = edited.SFID.Trim();

        var stations = partner.SubStations.ToList();
        var index = string.IsNullOrEmpty(originalSfid) ? -1 : stations.FindIndex(s => Same(s.SFID, originalSfid));
        if (index < 0)
            stations.Add(edited);
        else
            stations[index] = edited;
        partner.SubStations = stations;

        if (!string.IsNullOrEmpty(originalSfid) && !Same(originalSfid, edited.SFID))
            partner.Certificates = partner.Certificates
                .Select(c => Same(c.Sfid, originalSfid) ? Copy(c, edited.SFID) : c)
                .ToList();
    }

    /// <summary>Removes the sub-station <paramref name="sfid"/> together with the certificates assigned to it.</summary>
    public static void Remove(Partner partner, string sfid)
    {
        partner.SubStations = partner.SubStations.Where(s => !Same(s.SFID, sfid)).ToList();
        partner.Certificates = partner.Certificates.Where(c => !Same(c.Sfid, sfid)).ToList();
    }

    /// <summary>A copy to edit, so that cancelling the dialog leaves the partner as it was.</summary>
    public static PartnerSubStation Copy(PartnerSubStation station) => new()
    {
        Name = station.Name,
        SFID = station.SFID,
        SignFiles = station.SignFiles,
        EncryptFiles = station.EncryptFiles,
        CompressFiles = station.CompressFiles,
        RequestSignedEndResponse = station.RequestSignedEndResponse,
        RequireSignedFiles = station.RequireSignedFiles,
        RequireEncryptedFiles = station.RequireEncryptedFiles,
        RequireCompressedFiles = station.RequireCompressedFiles,
        FileCipherSuite = station.FileCipherSuite,
        Contacts = station.Contacts.Select(c => new PartnerContact
        {
            Name = c.Name,
            Description = c.Description,
            Emails = c.Emails.ToList(),
            Phones = c.Phones.ToList(),
            Url = c.Url,
        }).ToList(),
        InboundDsnPatterns = station.InboundDsnPatterns.ToList(),
        OutboundDsnPatterns = station.OutboundDsnPatterns.ToList(),
    };

    private static CertificateAssignment Copy(CertificateAssignment assignment, string sfid) => new()
    {
        Sfid = sfid,
        Usage = assignment.Usage,
        CertificateId = assignment.CertificateId,
        PreviousCertificateId = assignment.PreviousCertificateId,
    };

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
