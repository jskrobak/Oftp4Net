using Oftp4Net.Domain;

namespace Oftp4Net.Services.Encodings;

/// <summary>Applies the character encoding configured for a partner to the content of virtual files.</summary>
public static class PartnerEncoding
{
    /// <summary>
    /// Wraps the content of a file sent to <paramref name="partner"/> so that it is converted to the partner's
    /// outgoing encoding while it is read. Returns <paramref name="content"/> when no conversion is configured.
    /// </summary>
    public static Stream ForSending(Partner partner, Stream content) =>
        partner.OutgoingEncoding == FileCharacterEncoding.EBCDIC
            ? new ByteTranslatingStream(content,
                CharacterEncodings.GetAnsiToEbcdicTable(partner.AnsiCodePage, partner.EbcdicCodePage))
            : content;

    /// <summary>
    /// Wraps the destination of a file received from <paramref name="partner"/> so that the content is converted
    /// from EBCDIC to ANSI while it is written. Returns <paramref name="destination"/> when no conversion is configured.
    /// </summary>
    public static Stream ForReceiving(Partner partner, Stream destination) =>
        partner.ConvertIncomingEbcdicToAnsi
            ? new ByteTranslatingStream(destination,
                CharacterEncodings.GetEbcdicToAnsiTable(partner.EbcdicCodePage, partner.AnsiCodePage))
            : destination;
}
