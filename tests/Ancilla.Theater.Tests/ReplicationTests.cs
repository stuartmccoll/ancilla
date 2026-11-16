using Ancilla.Theater.Format;

namespace Ancilla.Theater.Tests;

public class SlotTableTests
{
    /// <summary>One slot in a synthetic table. Never-used slots have generation 1.</summary>
    private sealed record Slot(int Index, bool InUse, uint Generation, ulong Value = 0, bool Optional = false, int OptionalBits = 0);

    /// <summary>
    /// A type-1 payload built bit by bit: table A, table B, then five u32 cursors and 7 zero bits.
    /// <paramref name="strayBit"/>, when set, is a bit to set afterwards.
    /// </summary>
    private static byte[] Table(IEnumerable<Slot> slots, uint[] cursors, long strayBit = -1)
    {
        var bySlot = slots.ToDictionary(s => s.Index);
        Slot Get(int i) => bySlot.GetValueOrDefault(i) ?? new Slot(i, false, 1);
        var w = new BitWriter();
        for (var i = 0; i < SlotTable.SlotCount; i++)
        {
            var s = Get(i);
            var used = s.InUse ? 1UL : 0;
            w.Write(0, 3).Write(used, 1).Write(s.Optional ? 1UL : 0, 1).Write(used, 1).Write(0, 6)
             .Write((s.Generation - 1) % 4, 2).Write(s.Generation, 32).Write((ulong)s.OptionalBits, 9).Write(0, 23).Write(0, 1);
        }
        for (var i = 0; i < SlotTable.SlotCount; i++) w.Write(Get(i).Value, 64).Zeros(192);
        foreach (var c in cursors) w.Write(c, 32);
        w.Zeros(7);
        var bytes = w.ToArray();
        if (strayBit >= 0) bytes[strayBit / 8] |= (byte)(0x80 >> (int)(strayBit % 8));
        return bytes;
    }

    private static readonly uint[] NoCursors = [0, 0, 0, 0, 0];

    [Fact]
    public void Layout_constants_add_up_to_the_payload_size()
    {
        Assert.Equal(647_089, SlotTable.TableBStart);
        Assert.Equal(2_743_985, SlotTable.TrailerStart);
        Assert.Equal(167, SlotTable.TrailerBits);
        Assert.Equal(SlotTable.PayloadBytes, Table([], NoCursors).Length);
    }

    [Fact]
    public void Decodes_slots_values_and_cursors()
    {
        var t = SlotTable.Parse(Table(
            [new Slot(0, true, 1, Value: 0xABCD), new Slot(513, true, 3, Value: 1), new Slot(600, false, 2), new Slot(1100, true, 5, Optional: true, OptionalBits: 510)],
            [123, 514, 0, 1101, 0]));
        Assert.True(t.LayoutOk);
        Assert.True(t.InUse[0]);
        Assert.Equal(0xABCDUL, t.Value[0]);
        Assert.True(t.InUse[513]);
        Assert.Equal(3u, t.Generation[513]);
        Assert.False(t.InUse[600]);
        Assert.True(t.Touched(600));
        Assert.False(t.Touched(601));
        Assert.Equal(new uint[] { 123, 514, 0, 1101, 0 }, t.Cursors);
        Assert.Equal(2 + 1, t.Allocations(512, 768));
        Assert.Equal(601, t.HighWater(512, 768));
        Assert.Equal(0, t.HighWater(768, 1024));
    }

    [Theory]
    [InlineData(0)]                              // slot 0, an always-zero bit
    [InlineData(79 * 10 + 60)]                   // slot 10, inside bits 55–77
    [InlineData(647_089 + 256 * 5 + 100)]        // table B tail of slot 5
    [InlineData(2_743_985 + 160 + 3)]            // the 7 zero bits after the cursors
    public void A_stray_bit_outside_the_decoded_fields_fails_the_layout(long bit) =>
        Assert.False(SlotTable.Parse(Table([], NoCursors, strayBit: bit)).LayoutOk);

    [Fact]
    public void A_value_on_a_free_slot_fails_the_layout() =>
        Assert.False(SlotTable.Parse(Table([new Slot(7, false, 2, Value: 9)], NoCursors)).LayoutOk);

    [Fact]
    public void Optional_bits_without_the_optional_flag_fail_the_layout() =>
        Assert.False(SlotTable.Parse(Table([new Slot(7, true, 2, OptionalBits: 3)], NoCursors)).LayoutOk);

    [Fact]
    public void A_generation_that_disagrees_with_its_low_bits_fails_the_layout()
    {
        // Bits 12–13 of slot 0 hold (generation − 1) mod 4; set bit 13 on a generation-1 slot.
        Assert.False(SlotTable.Parse(Table([], NoCursors, strayBit: 13)).LayoutOk);
    }

    [Fact]
    public void A_payload_of_the_wrong_size_fails_the_layout() =>
        Assert.False(SlotTable.Parse(new byte[SlotTable.PayloadBytes - 1]).LayoutOk);
}

public class TickMarkerTests
{
    private static byte[] Marker(int?[] slots, int extraZeroBytes = 0, bool strayTailBit = false)
    {
        var w = new BitWriter();
        foreach (var v in slots) { if (v is { } x) w.Write(1, 1).Write((ulong)x, 6); else w.Write(0, 1); }
        var bytes = w.ToArray().Concat(new byte[extraZeroBytes]).ToArray();
        if (strayTailBit) bytes[^1] |= 1;
        return bytes;
    }

    private static int?[] Slots(params int[] present)
    {
        var s = new int?[TickMarker.Slots];
        for (var i = 0; i < present.Length; i++) s[i] = present[i];
        return s;
    }

    [Fact]
    public void Decodes_present_slots_and_values()
    {
        var (values, ok) = TickMarker.Parse(Marker(Slots(60, 3, 0, 14)));
        Assert.True(ok);
        Assert.Equal([60, 3, 0, 14], values.Take(4).Select(v => v!.Value));
        Assert.All(values.Skip(4), v => Assert.Null(v));
    }

    [Theory]
    [InlineData(4, 7)]    // 4 × 7 + 28 = 56 bits
    [InlineData(8, 10)]   // 8 × 7 + 24 = 80 bits
    [InlineData(9, 11)]   // 9 × 7 + 23 = 86 bits
    public void Payload_length_follows_the_number_of_present_slots(int present, int bytes) =>
        Assert.Equal(bytes, Marker(Slots(Enumerable.Repeat(5, present).ToArray())).Length);

    [Fact]
    public void An_extra_byte_fails_the_layout() => Assert.False(TickMarker.Parse(Marker(Slots(1, 2, 3), extraZeroBytes: 1)).LayoutOk);

    [Fact]
    public void A_set_bit_in_the_padding_fails_the_layout()
    {
        // 3 present slots use 3 × 7 + 29 = 50 bits, so the last 6 bits of 7 bytes are padding.
        Assert.True(TickMarker.Parse(Marker(Slots(1, 2, 3))).LayoutOk);
        Assert.False(TickMarker.Parse(Marker(Slots(1, 2, 3), strayTailBit: true)).LayoutOk);
    }

    [Fact]
    public void A_truncated_payload_fails_the_layout() => Assert.False(TickMarker.Parse(Marker(Slots(1, 2, 3, 4, 5, 6, 7, 8))[..^2]).LayoutOk);
}
