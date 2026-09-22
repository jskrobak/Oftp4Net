using Oftp4Net.Core.Protocol.Commands;

namespace Oftp4Net.Core.Tests;

public class DataBufferTests
{
    [Fact]
    public void Payload_IsSplitIntoSubrecordsOfAtMost63Octets()
    {
        var payload = Enumerable.Range(0, 130).Select(i => (byte)i).ToArray();

        var encoded = DATA.FromPayload(payload).Encode();

        Assert.Equal((byte)'D', encoded[0]);
        Assert.Equal(1 + 130 + 3, encoded.Length);
        Assert.Equal(63, encoded[1]);
        Assert.Equal(63, encoded[1 + 64]);
        Assert.Equal(4, encoded[1 + 128]);
    }

    [Fact]
    public void Decode_RoundTrip()
    {
        var payload = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
        var data = Assert.IsType<DATA>(OftpCommand.Decode(DATA.FromPayload(payload).Encode()));

        using var output = new MemoryStream();
        var length = data.DecodeTo(output);

        Assert.Equal(1000, length);
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public void Compression_ReplacesRunsOfEqualOctets()
    {
        // Two runs worth compressing with a literal octet between them.
        var payload = new byte[] { 1, 1, 1, 1, 1, 9, 2, 2, 2 };

        var encoded = DATA.FromPayload(payload, compress: true).Encode();

        Assert.Equal(new byte[] { (byte)'D', 0x45, 1, 0x01, 9, 0x43, 2 }, encoded);
    }

    [Fact]
    public void Compression_RoundTripsAnyContent()
    {
        var random = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            // Few distinct values, so runs appear by themselves.
            var payload = Enumerable.Range(0, random.Next(0, 500)).Select(_ => (byte)random.Next(0, 3)).ToArray();
            var data = Assert.IsType<DATA>(OftpCommand.Decode(DATA.FromPayload(payload, compress: true).Encode()));

            using var output = new MemoryStream();
            Assert.Equal(payload.Length, data.DecodeTo(output));
            Assert.Equal(payload, output.ToArray());
        }
    }

    [Fact]
    public void Compression_NeverExceedsTheUncompressedBuffer()
    {
        var random = new Random(7);
        for (var i = 0; i < 200; i++)
        {
            var payload = new byte[random.Next(1, 500)];
            random.NextBytes(payload);
            // Alternating single octets and short runs are the worst case for the subrecord headers.
            for (var j = 0; j + 3 < payload.Length; j += 4)
                payload[j + 1] = payload[j + 2] = payload[j + 3];

            Assert.True(DATA.FromPayload(payload, compress: true).Encode().Length <=
                        DATA.FromPayload(payload).Encode().Length);
        }
    }

    [Fact]
    public void Compression_ShrinksRepetitiveContent()
    {
        var payload = new byte[6300];
        Array.Fill(payload, (byte)' ');

        var compressed = DATA.FromPayload(payload, compress: true).Encode();

        // 100 runs of 63 octets, two octets each.
        Assert.Equal(1 + 200, compressed.Length);
        Assert.True(compressed.Length < DATA.FromPayload(payload).Encode().Length / 30);
    }

    [Fact]
    public void Decode_ExpandsCompressedSubrecords()
    {
        // 0x45 = compressed flag + count 5, followed by the repeated octet; then 2 plain octets with end of record flag.
        var data = Assert.IsType<DATA>(OftpCommand.Decode([(byte)'D', 0x45, (byte)'A', 0x82, (byte)'B', (byte)'C']));

        using var output = new MemoryStream();
        data.DecodeTo(output);

        Assert.Equal("AAAAABC"u8.ToArray(), output.ToArray());
    }

    [Theory]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(4096)]
    [InlineData(99999)]
    public void MaxPayloadLength_FillsButDoesNotExceedTheBuffer(int bufferSize)
    {
        var max = DATA.MaxPayloadLength(bufferSize);

        Assert.True(DATA.FromPayload(new byte[max]).Encode().Length <= bufferSize);
        Assert.True(DATA.FromPayload(new byte[max + 1]).Encode().Length > bufferSize);
    }
}
