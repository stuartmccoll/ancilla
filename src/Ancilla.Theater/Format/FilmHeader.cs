using System.Buffers.Binary;
using System.Text;

namespace Ancilla.Theater.Format;

/// <summary>One slot in the component registry: a name and a small number (1, 2, 4, ...; meaning unknown).</summary>
public sealed record ComponentSlot(string Name, uint Value);

/// <summary>A registry block: the components that make up one kind of entity (players, weapons, ...).</summary>
public sealed record ComponentBlock(int Index, int Offset, IReadOnlyList<ComponentSlot> Slots);

public sealed record BuildIdentity(string Version, string Branch, string Flavour, uint Value1, uint Value2);

/// <summary>
/// The decompressed type-1 (header) chunk. Layout, as observed in film version 41:
/// <code>
/// u32 film version (41)
/// u32 slots used in block 0
/// blocks[] × 16,640 bytes: 64 slots × (char name[256], u32 value), names zero-padded
/// u32 table[] (small values; meaning unknown)
/// char[32] build version, char[32] branch, char[32] flavour, u32, u32
/// bit-packed stream (MSB-first) containing the roster, and more
/// </code>
/// </summary>
public sealed record FilmHeader(
    uint Version, IReadOnlyList<ComponentBlock> Blocks, IReadOnlyList<uint> Table, BuildIdentity Build, int StreamOffset)
{
    public const int SlotSize = 260;
    public const int SlotsPerBlock = 64;
    public const int BlockSize = SlotSize * SlotsPerBlock;
    private const int FirstBlock = 8;

    public static FilmHeader Parse(ReadOnlySpan<byte> d)
    {
        var version = BinaryPrimitives.ReadUInt32LittleEndian(d);

        // Blocks run until one starts with something that is neither a name nor empty. Some
        // blocks are entirely empty, so an empty first slot doesn't end the registry.
        var blocks = new List<ComponentBlock>();
        var o = FirstBlock;
        while (o + BlockSize <= d.Length && (LooksLikeName(d.Slice(o, 256)) || d.Slice(o, SlotSize).IndexOfAnyExcept((byte)0) < 0))
        {
            var slots = new List<ComponentSlot>();
            for (var s = 0; s < SlotsPerBlock; s++)
            {
                var slot = d.Slice(o + s * SlotSize, SlotSize);
                var name = ReadName(slot[..256]);
                if (name.Length == 0) break;
                slots.Add(new ComponentSlot(name, BinaryPrimitives.ReadUInt32LittleEndian(slot[256..])));
            }
            blocks.Add(new ComponentBlock(blocks.Count, o, slots));
            o += BlockSize;
        }

        // Then small u32s up to the build strings, which start with printable ASCII.
        var table = new List<uint>();
        while (o + 4 <= d.Length)
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
            if (v == 0 || v > 0xFFFF) break;
            table.Add(v);
            o += 4;
        }

        var build = new BuildIdentity(
            ReadName(d.Slice(o, 32)), ReadName(d.Slice(o + 32, 32)), ReadName(d.Slice(o + 64, 32)),
            BinaryPrimitives.ReadUInt32LittleEndian(d[(o + 96)..]), BinaryPrimitives.ReadUInt32LittleEndian(d[(o + 100)..]));
        return new FilmHeader(version, blocks, table, build, o + 104);
    }

    private static bool LooksLikeName(ReadOnlySpan<byte> field) =>
        field[0] is >= (byte)'A' and <= (byte)'z' && ReadName(field).Length >= 3;

    private static string ReadName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        var bytes = end < 0 ? field : field[..end];
        foreach (var b in bytes) if (b is < 0x20 or > 0x7E) return "";
        return Encoding.ASCII.GetString(bytes);
    }
}
