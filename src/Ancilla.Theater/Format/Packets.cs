using System.Buffers.Binary;

namespace Ancilla.Theater.Format;

/// <summary>
/// One packet. Every decompressed type-2 and type-3 chunk is a run of these with no gaps:
/// <code>
/// u16 type | u16 field2 | u32 payload length | u64 timestamp (µs) | payload
/// </code>
/// The timestamp is a 64-bit microsecond clock that doesn't start at zero (0.3–3.8 hours at the
/// start of the films seen so far, perhaps server uptime). It was first read as a u32 plus an
/// unknown u32; that "unknown" was the high word, which ticks over when the low word wraps.
/// </summary>
public readonly record struct Packet(int Offset, ushort Type, ushort Field2, ulong TimestampUs, ReadOnlyMemory<byte> Payload);

public static class PacketReader
{
    public const int HeaderSize = 16;

    /// <summary>
    /// Reads packets until the data runs out. <paramref name="consumed"/> is where reading
    /// stopped; for a well-formed chunk it equals the data length.
    /// </summary>
    public static List<Packet> Read(ReadOnlyMemory<byte> data, out int consumed)
    {
        var packets = new List<Packet>();
        var span = data.Span;
        var o = 0;
        while (o + HeaderSize <= span.Length)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(span[o..]);
            var field2 = BinaryPrimitives.ReadUInt16LittleEndian(span[(o + 2)..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(span[(o + 4)..]);
            var ts = BinaryPrimitives.ReadUInt64LittleEndian(span[(o + 8)..]);
            if (length > span.Length - o - HeaderSize) break;
            packets.Add(new Packet(o, type, field2, ts, data.Slice(o + HeaderSize, (int)length)));
            o += HeaderSize + (int)length;
        }
        consumed = o;
        return packets;
    }
}
