using Ancilla.Theater.Format;

namespace Ancilla.Theater.Tests;

public class BitReaderTests
{
    [Fact]
    public void Reads_most_significant_bit_first()
    {
        var r = new BitReader(new byte[] { 0b1010_0000, 0xFF });
        Assert.Equal(1UL, r.Read(1));
        Assert.Equal(0b01UL, r.Read(2));
        Assert.Equal(3, r.Position);
    }

    [Fact]
    public void Reads_across_byte_boundaries()
    {
        var r = new BitReader(new byte[] { 0x0F, 0xF0 }, bitPosition: 4);
        Assert.Equal(0xFFUL, r.Read(8));
    }

    [Fact]
    public void Reads_a_full_64_bit_value()
    {
        var r = new BitReader(new byte[] { 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x01, 0x01 });
        Assert.Equal(0x0009_0000_0000_0101UL, r.Read(64));
    }

    [Fact]
    public void Reads_whole_bytes_from_any_bit()
    {
        var r = new BitReader(new byte[] { 0x01, 0x23, 0x40 }, bitPosition: 4);
        Assert.Equal(new byte[] { 0x12, 0x34 }, r.ReadBytes(2));
    }

    [Fact]
    public void Length_is_in_bits() => Assert.Equal(24, new BitReader(new byte[3]).Length);

    [Fact]
    public void Reading_past_the_end_throws()
    {
        var r = new BitReader(new byte[1], bitPosition: 4);
        Assert.Throws<EndOfStreamException>(() => r.Read(5));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65)]
    public void Rejects_widths_outside_0_to_64(int bits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitReader(new byte[16]).Read(bits));
}

public class ShiftedBitsTests
{
    private static readonly byte[] Pattern = [0xDE, 0xAD, 0xBE, 0xEF];

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)] [InlineData(7)] [InlineData(8)] [InlineData(13)]
    public void Finds_a_pattern_at_any_bit_offset(int offset)
    {
        var data = new BitWriter().Zeros(offset).Bytes(Pattern).Zeros(16).ToArray();
        Assert.Equal([offset], new ShiftedBits(data).FindAll(Pattern));
    }

    [Fact]
    public void Finds_every_occurrence_in_order()
    {
        var data = new BitWriter().Zeros(40).Bytes(Pattern).Zeros(5).Bytes(Pattern).Zeros(3).Bytes(Pattern).Zeros(8).ToArray();
        Assert.Equal([40L, 77L, 112L], new ShiftedBits(data).FindAll(Pattern));
    }

    [Fact]
    public void Finds_nothing_when_the_pattern_is_absent() =>
        Assert.Empty(new ShiftedBits(new byte[64]).FindAll(Pattern));

    [Fact]
    public void At_returns_the_bytes_starting_at_a_bit()
    {
        var data = new BitWriter().Zeros(5).Bytes(Pattern).Zeros(11).ToArray();
        Assert.Equal(Pattern, new ShiftedBits(data).At(5, 4).ToArray());
    }

    [Fact]
    public void At_returns_nothing_past_the_end() =>
        Assert.True(new ShiftedBits(new byte[4]).At(9, 4).IsEmpty);
}

public class PacketReaderTests
{
    [Fact]
    public void Reads_each_header_field_and_the_payload()
    {
        var data = Synthetic.Packet(10, 0x0000_0001_0000_0010UL, [1, 2, 3], field2: 7);
        var p = Assert.Single(PacketReader.Read(data, out var consumed));
        Assert.Equal((ushort)10, p.Type);
        Assert.Equal((ushort)7, p.Field2);
        Assert.Equal(0x0000_0001_0000_0010UL, p.TimestampUs);
        Assert.Equal(new byte[] { 1, 2, 3 }, p.Payload.ToArray());
        Assert.Equal(0, p.Offset);
        Assert.Equal(data.Length, consumed);
    }

    [Fact]
    public void Walks_a_run_of_packets_using_every_byte()
    {
        var data = Synthetic.Packet(1, 100, new byte[40]).Concat(Synthetic.Packet(10, 200, new byte[7])).Concat(Synthetic.Packet(7, 300)).ToArray();
        var packets = PacketReader.Read(data, out var consumed);
        Assert.Equal(new ushort[] { 1, 10, 7 }, packets.Select(p => p.Type));
        Assert.Equal(56, packets[1].Offset);
        Assert.Equal(data.Length, consumed);
    }

    [Fact]
    public void Stops_at_a_truncated_packet()
    {
        var data = Synthetic.Packet(1, 100, new byte[4]).Concat(Synthetic.Packet(2, 200, new byte[50])[..30]).ToArray();
        var packets = PacketReader.Read(data, out var consumed);
        Assert.Single(packets);
        Assert.Equal(20, consumed);
        Assert.True(consumed < data.Length);
    }

    [Fact]
    public void Timestamps_keep_rising_where_the_low_32_bits_wrap()
    {
        // The high word was once read as a separate, "constant" field; it ticks over here.
        var data = Synthetic.Packet(10, 0xFFFF_FFF0UL).Concat(Synthetic.Packet(10, 0x1_0000_0010UL)).ToArray();
        var packets = PacketReader.Read(data, out _);
        Assert.True(packets[1].TimestampUs > packets[0].TimestampUs);
        Assert.Equal(0x20UL, packets[1].TimestampUs - packets[0].TimestampUs);
    }
}
