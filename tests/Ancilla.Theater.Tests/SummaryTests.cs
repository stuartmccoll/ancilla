using Ancilla.Theater.Format;
using static Ancilla.Theater.Tests.Synthetic;

namespace Ancilla.Theater.Tests;

public class SummaryRecordsTests
{
    [Fact]
    public void Walks_every_record_and_reads_its_fields()
    {
        var payload = SummaryPayload([
            Kill("Test Alpha", 50_000),
            Death("Test Bravo", 50_001) with { BodyOffset = ShortRecordOffset },
            Medal("Test Alpha", 50_002, medalCode: 165, tier: 25),
        ]);

        var (records, stored, separatorsOk) = SummaryRecords.Walk(payload);
        Assert.Equal(3u, stored);
        Assert.True(separatorsOk);
        Assert.Equal(["Test Alpha", "Test Bravo", "Test Alpha"], records.Select(r => r.Gamertag));
        Assert.Equal([25, 10, 25], records.Select(r => r.Code));
        Assert.Equal([50_000u, 50_001u, 50_002u], records.Select(r => r.TimeMs));
        Assert.Equal([false, false, true], records.Select(r => r.IsMedal));
        Assert.Equal(165, records[2].MedalCode);   // 8-bit code; a 7-bit read would give 37
        Assert.Equal(-1, records[0].MedalCode);
    }

    [Fact]
    public void Reads_times_past_17_and_a_half_minutes()
    {
        // A 20-bit field would wrap at 1,048,575 ms.
        var (records, _, _) = SummaryRecords.Walk(SummaryPayload([Kill("Long Match", 1_500_000)]));
        Assert.Equal(1_500_000u, Assert.Single(records).TimeMs);
    }

    [Fact]
    public void Reports_a_record_without_the_12000_separator()
    {
        var (_, _, separatorsOk) = SummaryRecords.Walk(SummaryPayload([Kill("Test Alpha", 1), Death("Test Bravo", 2) with { Separator = 99 }]));
        Assert.False(separatorsOk);
    }

    [Fact]
    public void A_truncated_payload_ends_the_walk_instead_of_throwing()
    {
        var full = SummaryPayload([Kill("Test Alpha", 1), Death("Test Bravo", 2)]);
        var (records, stored, _) = SummaryRecords.Walk(full[..^3]);   // cuts into the last separator
        Assert.Single(records);
        Assert.Equal(2u, stored);
    }

    [Fact]
    public void An_empty_summary_has_no_records()
    {
        var (records, stored, separatorsOk) = SummaryRecords.Walk(SummaryPayload([]));
        Assert.Empty(records);
        Assert.Equal(0u, stored);
        Assert.True(separatorsOk);
    }
}

public class SummaryTests
{
    private static readonly string?[] Players = ["Test Alpha", "Test Bravo"];

    [Fact]
    public void Decode_maps_records_to_player_indices_and_skips_unknown_names()
    {
        var events = Summary.Decode(SummaryPayload([
            Kill("Test Alpha", 10), Death("Test Bravo", 11), Death("Someone Else", 12), Medal("Test Alpha", 13, 0),
        ]), Players);

        Assert.Equal([0, 1, 0], events.Select(e => e.Player));
        Assert.Equal([SummaryEventKind.PlayerKill, SummaryEventKind.Death, SummaryEventKind.Medal], events.Select(e => e.Kind));
    }

    [Theory]
    [InlineData(10, false, SummaryEventKind.Death)]
    [InlineData(25, false, SummaryEventKind.PlayerKill)]
    [InlineData(25, true, SummaryEventKind.Medal)]    // a normal-tier medal shares code 25 with kills
    [InlineData(110, true, SummaryEventKind.Medal)]
    [InlineData(5, false, SummaryEventKind.Objective)]
    [InlineData(7, false, SummaryEventKind.Other)]
    public void Kind_comes_from_the_code_and_medal_flag(int code, bool medal, SummaryEventKind kind)
    {
        var e = new Event("Test Alpha", code, 1, Medal: medal);
        Assert.Equal(kind, Assert.Single(Summary.Decode(SummaryPayload([e]), Players)).Kind);
    }

    [Fact]
    public void The_472_byte_is_the_record_type_for_events_and_the_medal_code_for_medals()
    {
        var events = Summary.Decode(SummaryPayload([
            Kill("Test Alpha", 1), Death("Test Alpha", 2), new Event("Test Alpha", Summary.ObjectiveCode, 3),
            Medal("Test Alpha", 4, medalCode: 152), new Event("Test Alpha", Summary.DeathCode, 5, Type: 7)]), Players);
        Assert.Equal([0, 1, 2, 152, 7], events.Select(e => e.TypeByte));
        Assert.Equal([-1, -1, -1, 152, -1], events.Select(e => e.MedalCode));
        Assert.Equal(1, Summary.RecordTypeOf(Summary.DeathCode));
        Assert.Null(Summary.RecordTypeOf(7));
    }

    [Fact]
    public void A_name_listed_twice_maps_to_its_first_index()
    {
        var records = SummaryRecords.Walk(SummaryPayload([Kill("Test Alpha", 1)])).Records;
        Assert.Equal(0, Assert.Single(Summary.FromRecords(records, ["Test Alpha", "Test Alpha"])).Player);
    }

    [Fact]
    public void Walking_and_name_search_find_the_same_events()
    {
        var payload = SummaryPayload([
            Kill("Test Alpha", 100), Death("Test Bravo", 101) with { BodyOffset = ShortRecordOffset },
            Medal("Test Alpha", 102, 9), Kill("Test Bravo", 1_200_000), Death("Test Alpha", 1_200_001),
        ]);
        var walked = Summary.Decode(payload, Players);
        var searched = Summary.DecodeByNameSearch(payload, Players);
        Assert.Equal(walked.Select(e => (e.Player, e.NameBit, e.TimeMs, e.Code, e.MedalCode, e.Kind)),
                     searched.Select(e => (e.Player, e.NameBit, e.TimeMs, e.Code, e.MedalCode, e.Kind)));
    }

    [Fact]
    public void PairKills_links_a_kill_to_the_death_right_after_it()
    {
        var events = Summary.Decode(SummaryPayload([Kill("Test Alpha", 100), Death("Test Bravo", 101)]), Players);
        Assert.Equal(1, Summary.PairKills(events));
        Assert.Equal(1, events[0].Victim);
    }

    [Fact]
    public void PairKills_needs_the_death_within_2_ms()
    {
        var events = Summary.Decode(SummaryPayload([Kill("Test Alpha", 100), Death("Test Bravo", 103)]), Players);
        Assert.Equal(0, Summary.PairKills(events));
        Assert.Null(events[0].Victim);
    }

    [Fact]
    public void PairKills_needs_the_very_next_record_to_be_another_players_death()
    {
        var sameplayer = Summary.Decode(SummaryPayload([Kill("Test Alpha", 100), Death("Test Alpha", 100)]), Players);
        var medalBetween = Summary.Decode(SummaryPayload([Kill("Test Alpha", 100), Medal("Test Alpha", 100, 0), Death("Test Bravo", 101)]), Players);
        Assert.Equal(0, Summary.PairKills(sameplayer));
        Assert.Equal(0, Summary.PairKills(medalBetween));
    }

    [Fact]
    public void Payload_is_the_type_9_packet()
    {
        var packets = PacketReader.Read(Packet(9, 1, [1, 2]).Concat(Packet(7, 2)).ToArray(), out _);
        Assert.Equal(new byte[] { 1, 2 }, Summary.Payload(packets).ToArray());
    }

    [Fact]
    public void Payload_without_a_type_9_packet_is_an_error()
    {
        var packets = PacketReader.Read(Packet(7, 2), out _);
        Assert.Throws<InvalidDataException>(() => Summary.Payload(packets));
    }
}

public class MedalCodesTests
{
    [Fact]
    public void Each_medal_has_one_code() =>
        Assert.Equal(MedalCodes.Known.Count, MedalCodes.Known.Values.Distinct().Count());

    [Fact]
    public void Codes_fit_the_8_bit_field() =>
        Assert.All(MedalCodes.Known.Keys, k => Assert.InRange(k, 0, (1 << Summary.MedalCodeBits) - 1));
}
