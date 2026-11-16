namespace Ancilla.Theater.Format;

/// <summary>
/// The type-1 packet at the start of every replication chunk: the game's entity slot table, not a
/// state snapshot. It holds no positions. Checked on eleven v41 films (293 snapshots), MSB-first:
/// <code>
/// bits 0 .. 647,088            table A: 8,191 records × 79 bits
///   bits 0–2, 6–11, 55–77      always 0
///   bit 3                      in use (bit 5 is a copy)
///   bit 4                      optional group present (only on in-use slots)
///   bits 12–13                 (generation − 1) mod 4
///   bits 14–45                 generation: a per-slot reuse counter, never-used slots read 1
///   bits 46–54, 78             only set when bit 4 is (meaning unknown)
/// bits 647,089 .. 2,743,984    table B: 8,191 records × 256 bits: a u64 (nonzero only on in-use
///                              slots), then 192 zero bits
/// bits 2,743,985 .. 2,744,151  trailer, 167 bits: five u32 allocation cursors (pools 0–511,
///                              512–767, 768–1023, 1024–1279, 1280–8190), then 7 zero bits
/// </code>
/// Slots 0–122 are static entities, in use all match; slots 512–767 are bipeds, one new slot per spawn.
/// </summary>
public sealed class SlotTable
{
    public const int SlotCount = 8_191;
    public const int RecordABits = 79, RecordBBits = 256;
    public const long TableBStart = (long)SlotCount * RecordABits;            // 647,089
    public const long TrailerStart = TableBStart + (long)SlotCount * RecordBBits;   // 2,743,985
    public const int PayloadBytes = 343_019;
    public const int TrailerBits = (int)(PayloadBytes * 8L - TrailerStart);         // 167: 5 × u32, 7 zero
    public const int StaticSlots = 123;

    /// <summary>The pools with allocation cursors in the trailer, as [first, end) slot ranges.</summary>
    public static readonly (int First, int End)[] CursorPools = [(0, 512), (512, 768), (768, 1024), (1024, 1280), (1280, SlotCount)];
    public static readonly (int First, int End) BipedPool = CursorPools[1];

    public required bool[] InUse { get; init; }
    public required uint[] Generation { get; init; }
    /// <summary>Table B's u64 per slot: mostly constant per entity; changes on bipeds (meaning unknown).</summary>
    public required ulong[] Value { get; init; }
    /// <summary>
    /// One per pool in <see cref="CursorPools"/>. Until a pool wraps it's 1 + the highest slot used in
    /// it (0 if none); after that it keeps moving, and the slot behind it is always the newest.
    /// </summary>
    public required uint[] Cursors { get; init; }
    /// <summary>The payload is the right size and every set bit falls in a decoded field.</summary>
    public required bool LayoutOk { get; init; }

    /// <summary>Whether a slot has ever been used (its generation has moved past 1).</summary>
    public bool Touched(int slot) => Generation[slot] > 1;

    /// <summary>Slots allocated so far in [first, end): a slot's generation counts its uses, plus 1.</summary>
    public long Allocations(int first, int end)
    {
        long n = 0;
        for (var s = first; s < end; s++) n += Generation[s] - 1;
        return n;
    }

    /// <summary>1 + the highest slot used so far in [first, end), or 0 if none.</summary>
    public int HighWater(int first, int end)
    {
        for (var s = end - 1; s >= first; s--) if (Touched(s)) return s + 1;
        return 0;
    }

    public static SlotTable Parse(ReadOnlyMemory<byte> payload)
    {
        var inUse = new bool[SlotCount];
        var gen = new uint[SlotCount];
        var value = new ulong[SlotCount];
        var cursors = new uint[CursorPools.Length];
        if (payload.Length != PayloadBytes)
            return new SlotTable { InUse = inUse, Generation = gen, Value = value, Cursors = cursors, LayoutOk = false };

        var r = new BitReader(payload);
        var ok = true;
        for (var s = 0; s < SlotCount; s++)
        {
            r.Position = (long)s * RecordABits;
            ok &= r.Read(3) == 0;
            var used = r.Read(1) == 1;
            var optional = r.Read(1) == 1;
            ok &= (r.Read(1) == 1) == used;
            ok &= r.Read(6) == 0;
            var low2 = (uint)r.Read(2);
            var g = (uint)r.Read(32);
            var optionalBits = r.Read(9);
            ok &= r.Read(23) == 0;
            var bit78 = r.Read(1);
            ok &= g >= 1 && low2 == (g - 1) % 4;
            ok &= !optional || used;
            ok &= optional || (optionalBits == 0 && bit78 == 0);
            inUse[s] = used;
            gen[s] = g;

            r.Position = TableBStart + (long)s * RecordBBits;
            value[s] = r.Read(64);
            ok &= used || value[s] == 0;
            for (var i = 0; i < 3; i++) ok &= r.Read(64) == 0;
        }

        r.Position = TrailerStart;
        for (var i = 0; i < cursors.Length; i++) cursors[i] = (uint)r.Read(32);
        ok &= r.Read(TrailerBits - 32 * cursors.Length) == 0;
        return new SlotTable { InUse = inUse, Generation = gen, Value = value, Cursors = cursors, LayoutOk = ok };
    }
}
