using Oftp4Net.Domain;

namespace Oftp4Net.Services.Retention;

/// <summary>
/// A send queue item as it is written to the archive: with the names of its partner and identity, which may be
/// gone by the time somebody reads it, and without the webhook secret.
/// </summary>
public sealed record ArchivedSendQueueItem(
    int Id, DateTime Created, string? Partner, string? PartnerSsid, string? Identity, string? DestinationSfid,
    string VirtualFileName, string? FileDate, string? FileTime, string Format, int MaxRecordSize, string FilePath,
    string? Description, string? Reference, SendStatus Status, DateTime? SentDate, DateTime? DeliveredDate,
    int RetryCount, string? LastError, DateTime? LastErrorDate, string? CipherSuite, bool SignedResponseRequested,
    byte[]? ContentHash, string? WebhookUrl)
{
    public static ArchivedSendQueueItem From(SendQueueItem i) => new(
        i.Id, i.Created, i.Partner?.Name, i.Partner?.SSID, i.Identity?.SSID, i.DestinationSfid,
        i.VirtualFileName, i.FileDate, i.FileTime, i.Format, i.MaxRecordSize, i.FilePath,
        i.Description, i.Reference, i.Status, i.SentDate, i.DeliveredDate,
        i.RetryCount, i.LastError, i.LastErrorDate, i.CipherSuite, i.SignedResponseRequested,
        i.ContentHash, i.WebhookUrl);
}

/// <summary>A received file as it is written to the archive, with the name of its partner.</summary>
public sealed record ArchivedReceivedFile(
    int Id, DateTime Created, string? Partner, string? PartnerSsid, string VirtualFileName, string FileDate,
    string FileTime, string Originator, string Destination, string UserData, string? Description, string FilePath,
    long Size, string Format, int MaxRecordSize, long Records, ReceiveStatus Status, DateTime? ConfirmedDate,
    DateTime? FetchedDate, DateTime? DecidedDate, string? NotDeliveredReasonCode, string? LastError,
    long RestartedFrom, string? SecurityLevel, string? CipherSuite, bool Compressed, bool SignedResponseRequested,
    byte[]? ContentHash)
{
    public static ArchivedReceivedFile From(ReceivedFile f) => new(
        f.Id, f.Created, f.Partner?.Name, f.Partner?.SSID, f.VirtualFileName, f.FileDate,
        f.FileTime, f.Originator, f.Destination, f.UserData, f.Description, f.FilePath,
        f.Size, f.Format, f.MaxRecordSize, f.Records, f.Status, f.ConfirmedDate,
        f.FetchedDate, f.DecidedDate, f.NotDeliveredReasonCode, f.LastError,
        f.RestartedFrom, f.SecurityLevel, f.CipherSuite, f.Compressed, f.SignedResponseRequested,
        f.ContentHash);
}
