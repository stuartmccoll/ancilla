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

public class EntityRecordsTests
{
    private static void Biped(BitWriter w, int slot, int[] components, (int X, int Y, int Z) p) =>
        Header(w, slot, components).Write(0, 2).Write((ulong)p.X, 18).Write((ulong)p.Y, 15).Write((ulong)p.Z, 18);

    private static BitWriter Header(BitWriter w, int slot, int[] components)
    {
        w.Write(1, 1).Write((ulong)slot, 13).Write(0, 1).Write(1, 1).Write((ulong)components.Length, 5);
        foreach (var c in components) w.Write((ulong)c, 6);
        return w;
    }

    [Fact]
    public void Finds_biped_and_dropped_object_positions_at_any_bit_offset()
    {
        var w = new BitWriter().Write(0b101, 3);
        Biped(w, 600, [0, 1, 25], (16_000, 18_000, 231_038));
        Header(w, 1500, [0, 1, 2, 3]).Write(16_010, 18).Write(18_020, 15).Write(231_100, 18);
        var (bipeds, objects) = EntityRecords.Find(w.Write(0, 8).ToArray());
        Assert.Contains(new EntityPosition(600, new FilmPosition(16_000, 18_000, 231_038)), bipeds);
        Assert.Contains(new EntityPosition(1500, new FilmPosition(16_010, 18_020, 231_100)), objects);
    }

    [Theory]
    [InlineData(300, new[] { 0, 1 })]   // not a biped slot
    [InlineData(600, new[] { 1, 25 })]  // no position component
    [InlineData(600, new[] { 0, 25, 1 })] // components not ascending
    public void Ignores_headers_that_are_not_biped_positions(int slot, int[] components)
    {
        var w = new BitWriter();
        Biped(w, slot, components, (1, 2, 4));
        Assert.DoesNotContain(EntityRecords.Find(w.Write(0, 8).ToArray()).Bipeds, b => b.Slot == slot);
    }

    // Component data in the layouts FindBipedUpdates reads (see EntityRecords).
    private static BitWriter Position(BitWriter w, (int X, int Y, int Z) p) =>
        w.Write(0, 2).Write((ulong)p.X, 18).Write((ulong)p.Y, 15).Write((ulong)p.Z, 18).Write(0, 1);
    private static BitWriter Aim(BitWriter w, int yaw, int pitch) => w.Write(1, 1).Write((ulong)yaw, 12).Write((ulong)pitch, 12);
    private static BitWriter Tick(BitWriter w, int tick, bool more) { w.Write(1, 1).Write((ulong)tick, 8); return more ? w.Write(0, 1) : w; }
    private static BitWriter Ammo(BitWriter w, int rounds) => w.Write(0, 1).Write((ulong)rounds, 8).Write(0b1011, 4);

    [Fact]
    public void Reads_position_aim_command_tick_and_ammo_from_one_biped_record()
    {
        var w = new BitWriter().Write(0b11, 2);
        Header(w, 600, [0, 1, 5, 21, 25, 30]);
        Position(w, (16_000, 18_000, 231_038)).Write(0x5555_5555, 31).Write(0x0AAA_AAAA, 29);
        Tick(Aim(w, 3288, 3020), 200, more: true);
        Ammo(w, 27).Write(0, 8);

        var u = Assert.Single(EntityRecords.FindBipedUpdates(w.ToArray()), x => x.Slot == 600);
        Assert.True(u.Checked);
        Assert.Equal([0, 1, 5, 21, 25, 30], u.Components);
        Assert.Equal(new FilmPosition(16_000, 18_000, 231_038), u.Position);
        Assert.Equal(new Aim(3288, 3020), u.Aim);
        Assert.Equal(200, u.CommandTick);
        Assert.Equal((30, 27), (u.AmmoComponent!.Value, u.Rounds!.Value));
    }

    [Fact]
    public void Reads_aim_and_command_tick_without_a_position()
    {
        var w = new BitWriter().Write(0b1, 1);
        Tick(Aim(Header(w, 512, [21, 25]), 1024, 2048), 7, more: false).Write(0, 8);
        var u = Assert.Single(EntityRecords.FindBipedUpdates(w.ToArray()), x => x.Slot == 512);
        Assert.True(u.Checked);
        Assert.Null(u.Position);
        Assert.Equal(90.0, u.Aim!.Value.YawDegrees);
        Assert.Equal(0.0, u.Aim!.Value.PitchDegrees);
    }

    [Fact]
    public void Drops_a_record_whose_command_tick_does_not_land()
    {
        // The tick's leading bit is 0: a width before it is wrong, so nothing in the record is trusted.
        var w = new BitWriter();
        Aim(Header(w, 600, [21, 25]), 100, 2048).Write(0, 1).Write(7, 8).Write(0, 8);
        Assert.DoesNotContain(EntityRecords.FindBipedUpdates(w.ToArray()), x => x.Slot == 600);

        // A component follows the tick but the bit after the tick is 1.
        w = new BitWriter();
        Aim(Header(w, 600, [21, 25, 30]), 100, 2048).Write(1, 1).Write(7, 8).Write(1, 1);
        Ammo(w, 12).Write(0, 8);
        Assert.DoesNotContain(EntityRecords.FindBipedUpdates(w.ToArray()), x => x.Slot == 600);
    }

    [Fact]
    public void Leaves_records_it_cannot_check_unchecked()
    {
        // Velocity without a position has another width, so reading stops before the tick.
        var w = new BitWriter();
        Tick(Aim(Header(w, 600, [1, 21, 25]).Write(0x5555_5555, 31), 100, 2048), 7, more: false);
        Assert.DoesNotContain(EntityRecords.FindBipedUpdates(w.Write(0, 8).ToArray()), x => x.Slot == 600);

        // An ammo component on its own has no tick: a candidate, not checked.
        w = new BitWriter();
        Ammo(Header(w, 601, [33]), 11).Write(0, 8);
        var u = Assert.Single(EntityRecords.FindBipedUpdates(w.ToArray()), x => x.Slot == 601);
        Assert.False(u.Checked);
        Assert.Equal((33, 11), (u.AmmoComponent!.Value, u.Rounds!.Value));
    }

    [Fact]
    public void Filtering_keeps_slots_with_enough_records_inside_the_dropped_objects_range()
    {
        var objects = Enumerable.Range(0, 400).Select(i => (i, new EntityPosition(2000, new FilmPosition(10_000 + i * 10, 15_000 + i * 5, 230_000 + i * 2)))).ToList();
        var bipeds = Enumerable.Range(0, 40).Select(i => (i, new EntityPosition(520, new FilmPosition(12_000 + i, 16_000, 231_000))))
            .Concat(Enumerable.Range(0, 5).Select(i => (i, new EntityPosition(530, new FilmPosition(12_000, 16_000, 231_000)))))    // too few
            .Concat(Enumerable.Range(0, 40).Select(i => (i, new EntityPosition(540, new FilmPosition(200_000, 16_000, 231_000)))))   // out of range
            .ToList();
        var kept = EntityRecords.FilterBipeds(bipeds, objects);
        Assert.Equal([520], kept.Select(k => k.Record.Slot).Distinct());
    }
}

public class LivesTests
{
    private static BipedTrack Track(int slot, int first, int last) =>
        new(slot, Enumerable.Range(first, last - first + 1).Select(t => (t, new FilmPosition(t, 0, 0))).ToList());

    [Fact]
    public void Chains_each_players_lives_through_deaths_and_8_second_respawns()
    {
        // Ticks are 1 s apart for simplicity. Player 0 dies at 10 s and 30 s; player 1 at 12 s.
        var tickMs = Enumerable.Range(0, 60).Select(t => t * 1000.0).ToList();
        var tracks = new[] { Track(512, 0, 10), Track(513, 0, 12), Track(514, 18, 30), Track(515, 20, 59), Track(516, 38, 59) }
            .ToDictionary(t => t.Slot);
        int[] deathTicks = [10, 12, 30], deathPlayers = [0, 1, 0];
        var deathMs = deathTicks.Select(t => t * 1000.0).ToList();

        var matched = Lives.MatchDeaths(deathTicks, tracks);
        Assert.Equal(new Dictionary<int, int> { [0] = 512, [1] = 513, [2] = 514 }, matched);
        var respawns = Lives.Respawns(deathMs, matched.Keys, tracks, tickMs);
        Assert.Equal(new Dictionary<int, int> { [0] = 514, [1] = 515, [2] = 516 }, respawns);

        var lives = Lives.Chain(deathPlayers, matched, respawns, tracks);
        Assert.Equal([512, 514, 516], lives[0]);
        Assert.Equal([513, 515], lives[1]);
    }

    [Fact]
    public void A_death_with_no_track_ending_nearby_is_not_matched()
    {
        var tracks = new[] { Track(512, 0, 10) }.ToDictionary(t => t.Slot);
        Assert.Empty(Lives.MatchDeaths([20], tracks));
        Assert.Single(Lives.MatchDeaths([12], tracks));   // within 3 ticks
    }
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
