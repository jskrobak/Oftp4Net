using Oftp4Net.Core.Protocol.Commands;

namespace Oftp4Net.Core.Protocol;

/// <summary>
/// Data signed in an End to End Response, as defined by RFC 5024, sections 5.3.13 and 5.3.14:
/// the fields of the command in their entirety, including padding.
/// </summary>
public static class EndResponseSignature
{
    /// <summary>EERPDSN, EERPDATE, EERPTIME, EERPDEST, EERPORIG and EERPHSH.</summary>
    public static byte[] GetSignedContent(EERP response) =>
        new CommandWriter(EERP.Id)
            .Alpha(response.DatasetName, 26)
            .Alpha(response.Date, 8)
            .Alpha(response.Time, 10)
            .Alpha(response.Destination, 25)
            .Alpha(response.Originator, 25)
            .Raw(response.Hash)
            .ToArray()[1..];

    /// <summary>NERPDSN, NERPDATE, NERPTIME, NERPDEST, NERPORIG, NERPCREA and NERPHSH.</summary>
    public static byte[] GetSignedContent(NERP response) =>
        new CommandWriter(NERP.Id)
            .Alpha(response.DatasetName, 26)
            .Alpha(response.Date, 8)
            .Alpha(response.Time, 10)
            .Alpha(response.Destination, 25)
            .Alpha(response.Originator, 25)
            .Alpha(response.Creator, 25)
            .Raw(response.Hash)
            .ToArray()[1..];
}
