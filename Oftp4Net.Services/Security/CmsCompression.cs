using System.Formats.Asn1;
using System.IO.Compression;

namespace Oftp4Net.Services.Security;

/// <summary>
/// CMS CompressedData with the zlib algorithm (RFC 3274, used by RFC 5024, section 6.4).
/// .NET has no built-in support for this content type, so the small ASN.1 structure is built here:
/// <code>
/// ContentInfo ::= SEQUENCE { contentType id-ct-compressedData, content [0] EXPLICIT CompressedData }
/// CompressedData ::= SEQUENCE { version 0, compressionAlgorithm zlib, encapContentInfo }
/// EncapsulatedContentInfo ::= SEQUENCE { eContentType id-data, eContent [0] EXPLICIT OCTET STRING }
/// </code>
/// </summary>
public static class CmsCompression
{
    private const string CompressedDataOid = "1.2.840.113549.1.9.16.1.9";
    private const string ZlibOid = "1.2.840.113549.1.9.16.3.8";
    private const string DataOid = "1.2.840.113549.1.7.1";

    public static byte[] Compress(byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            writer.WriteObjectIdentifier(CompressedDataOid);
            using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
            using (writer.PushSequence())
            {
                writer.WriteInteger(0);
                using (writer.PushSequence())
                    writer.WriteObjectIdentifier(ZlibOid);

                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier(DataOid);
                    using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
                        writer.WriteOctetString(compressed.ToArray());
                }
            }
        }

        return writer.Encode();
    }

    public static byte[] Decompress(byte[] data)
    {
        byte[] compressed;
        try
        {
            var reader = new AsnReader(data, AsnEncodingRules.BER).ReadSequence();
            if (reader.ReadObjectIdentifier() != CompressedDataOid)
                throw new FileSecurityException("The file is not a CMS CompressedData package.");

            var compressedData = reader.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
            compressedData.ReadInteger();

            var algorithm = compressedData.ReadSequence().ReadObjectIdentifier();
            if (algorithm != ZlibOid)
                throw new FileSecurityException($"Compression algorithm {algorithm} is not supported, only zlib.");

            var content = compressedData.ReadSequence();
            content.ReadObjectIdentifier();
            compressed = content.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadOctetString();
        }
        catch (AsnContentException ex)
        {
            throw new FileSecurityException("The compressed file could not be read: " + ex.Message, ex);
        }

        using var source = new MemoryStream(compressed);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        try
        {
            zlib.CopyTo(target);
        }
        catch (InvalidDataException ex)
        {
            throw new FileSecurityException("The file could not be decompressed: " + ex.Message, ex);
        }

        return target.ToArray();
    }
}
