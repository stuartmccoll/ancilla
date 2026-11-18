using Ancilla.Theater.Format;

namespace Ancilla.Theater.Tests;

public class PlayerListTests
{
    // A status that starts 11, with bit 18 (5 more bits) and bit 19 (16 more bits) as asked.
    private static ulong Status(bool five = false, bool sixteen = false) =>
        (0b11UL << 19) | 0b0101UL << 8 | (five ? 1UL << 2 : 0) | (sixteen ? 1UL << 1 : 0);

    /// <summary>A type-0 payload: some record bits, then the list, then the terminating 0 and padding.</summary>
    private static byte[] Packet(params (int Index, bool Five, bool Sixteen)[] entries) =>
        Packet(entries.Select(e => (e.Index, e.Five ? 0b10110 : -1, e.Sixteen)).ToArray());

    // Five: the 5-bit extension's value, or -1 for none. When its last bit is 1, 16 more bits follow it.
    private static byte[] Packet((int Index, int Five, bool Sixteen)[] entries)
    {
        // Stand-in records before the list: an idle 1, then bits that don't read as entries.
        var w = new BitWriter().Write(0b1, 1).Write(0x2A5, 10).Write(0b0, 1);
        foreach (var (index, five, sixteen) in entries)
        {
            w.Write(1, 1).Write((ulong)index, 8).Write(Status(five >= 0, sixteen), 21);
            if (five >= 0) { w.Write((ulong)five, 5); if ((five & 1) == 1) w.Write(0x8C4, 16); }
            if (sixteen) w.Write(0xBEEF, 16);
        }
        w.Write(0, 1);
        return w.ToArray();
    }

    [Fact]
    public void Reads_the_indexes_of_the_players_listed()
    {
        Assert.Equal([0, 1, 2, 4, 7], PlayerList.Parse(Packet((0, false, false), (1, true, false), (2, false, false), (4, false, true), (7, true, true)))!);
    }

    [Fact]
    public void An_extension_ending_in_1_carries_16_more_bits()
    {
        Assert.Equal([3, 5, 6], PlayerList.Parse(Packet([(3, 0b00100, false), (5, 0b00111, false), (6, -1, false)]))!);
    }

    [Fact]
    public void An_empty_list_and_a_packet_with_no_terminator_are_told_apart()
    {
        Assert.Null(PlayerList.Parse(new BitWriter().Write(0xFF, 8).ToArray()));   // ends on a 1: no terminating 0
        Assert.Null(PlayerList.Parse(new byte[4]));                                 // all zeros
    }

    [Fact]
    public void Entries_must_start_their_status_with_11_and_ascend()
    {
        var notStatus = new BitWriter().Write(1, 1).Write(3, 8).Write(0b10UL << 19, 21).Write(0, 1).ToArray();
        Assert.Null(PlayerList.Parse(notStatus));
        var descending = new BitWriter().Write(1, 1).Write(5, 8).Write(Status(), 21).Write(1, 1).Write(2, 8).Write(Status(), 21).Write(0, 1).ToArray();
        Assert.Equal([2], PlayerList.Parse(descending)!);   // only the last entry reads to the end on its own
    }

    [Fact]
    public void Presence_ignores_short_blips_and_carries_over_packets_that_dont_parse()
    {
        // Index 0 is listed for ticks 0–199 except a 10-tick gap and an unparsed packet; index 9 appears for 5 ticks.
        var lists = Enumerable.Range(0, 300).Select(t =>
            t == 50 ? null : t is >= 100 and < 110 ? Array.Empty<int>() : t < 200 ? (t is >= 150 and < 155 ? new[] { 0, 9 } : [0]) : []).ToList();
        var presence = PlayerList.Presence(lists);

        Assert.Equal([(0, 199)], PlayerList.Runs(presence[0]));
        Assert.False(presence.ContainsKey(9));
    }

    [Fact]
    public void Lives_go_to_the_player_whose_death_ends_them_even_when_an_index_changes_hands()
    {
        // Index 0: player 2 dies at 400 and later quits; player 5 joins on the same index and dies at 1,600.
        // Index 1: player 1 dies at 700; their last life runs to the end of the film with no death.
        var n = 2_000;
        bool[] Listed(params (int From, int To)[] runs) { var a = new bool[n]; foreach (var (f, t) in runs) for (var k = f; k <= t; k++) a[k] = true; return a; }
        var presence = new Dictionary<int, bool[]>
        {
            [0] = Listed((100, 400), (900, 1_100), (1_300, 1_600)),
            [1] = Listed((100, 701), (1_200, n - 1)),
        };
        var spans = PlayerList.Spans(presence, [(400, 2), (701, 1), (1_600, 5)]);

        Assert.Equal(
            [new(1, 100, 701, true), new(2, 100, 400, true), new(2, 900, 1_100, false), new(1, 1_200, n - 1, false), new(5, 1_300, 1_600, true)],
            spans);
    }
}
