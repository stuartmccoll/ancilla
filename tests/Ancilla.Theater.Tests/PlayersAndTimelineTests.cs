using Ancilla.Core;
using Ancilla.Theater.Format;
using static Ancilla.Theater.Tests.Synthetic;

namespace Ancilla.Theater.Tests;

public class FilmPlayersTests
{
    private static readonly string MatchId = Guid.NewGuid().ToString();

    private static List<RosterEntry> Roster(params (ulong Xuid, string Name)[] players) =>
        players.Select((p, i) => new RosterEntry(p.Xuid, i * 28_000L, p.Name, i * 28_000L + LongRecordOffset)).ToList();

    private static IReadOnlyList<SummaryRecord> Records(params Event[] events) =>
        SummaryRecords.Walk(SummaryPayload(events)).Records;

    [Fact]
    public void Roster_players_are_matched_by_xuid_in_header_order()
    {
        var board = Board(MatchId, [Human(Xuid(2), 1, new()), Human(Xuid(1), 0, new())]);
        var (players, unlinked) = FilmPlayers.Resolve(Roster((Xuid(1), "Test Alpha"), (Xuid(2), "Test Bravo")), [], board);

        Assert.Equal(["Test Alpha", "Test Bravo"], players.Select(p => p.Gamertag));
        Assert.Equal([board.Players[1], board.Players[0]], players.Select(p => p.Row));
        Assert.All(players, p => Assert.False(p.LateJoiner));
        Assert.Empty(unlinked);
    }

    [Fact]
    public void A_roster_entry_not_on_the_scoreboard_is_left_out()
    {
        var board = Board(MatchId, [Human(Xuid(1), 0, new())]);
        var (players, _) = FilmPlayers.Resolve(Roster((Xuid(1), "Test Alpha"), (Xuid(9), "Not Here")), [], board);
        Assert.Equal("Test Alpha", Assert.Single(players).Gamertag);
    }

    [Fact]
    public void A_late_joiner_is_linked_by_kills_deaths_and_medals()
    {
        var late = Human(Xuid(3), 1, new(Kills: 1, Deaths: 2), joinedLate: true);
        var board = Board(MatchId, [Human(Xuid(1), 0, new()), late]);
        var (players, unlinked) = FilmPlayers.Resolve(Roster((Xuid(1), "Test Alpha")),
            Records(Kill("Late Joiner", 10), Death("Late Joiner", 20), Death("Late Joiner", 30)), board);

        var linked = players[^1];
        Assert.True(linked.LateJoiner);
        Assert.Equal("Late Joiner", linked.Gamertag);
        Assert.Same(late, linked.Row);
        Assert.Empty(unlinked);
    }

    [Fact]
    public void A_late_joiner_is_not_linked_when_two_players_fit()
    {
        var board = Board(MatchId, [
            Human(Xuid(3), 1, new(Deaths: 1), joinedLate: true),
            Human(Xuid(4), 0, new(Deaths: 1), joinedLate: true),
        ]);
        var (players, unlinked) = FilmPlayers.Resolve([], Records(Death("Late Joiner", 10)), board);
        Assert.Empty(players);
        Assert.Equal(["Late Joiner"], unlinked);
    }

    [Fact]
    public void A_late_joiner_is_not_linked_when_no_counts_fit()
    {
        var board = Board(MatchId, [Human(Xuid(3), 1, new(Deaths: 5), joinedLate: true)]);
        var (_, unlinked) = FilmPlayers.Resolve([], Records(Death("Late Joiner", 10)), board);
        Assert.Equal(["Late Joiner"], unlinked);
    }

    [Fact]
    public void Only_humans_who_joined_in_progress_can_be_linked()
    {
        var board = Board(MatchId, [Human(Xuid(3), 1, new(Deaths: 1)), Bot(1, 1, new(Deaths: 1))]);
        var (_, unlinked) = FilmPlayers.Resolve([], Records(Death("Late Joiner", 10)), board);
        Assert.Equal(["Late Joiner"], unlinked);
    }

    [Fact]
    public void Stats_uses_pvp_kills_where_present()
    {
        var p = Human(Xuid(1), 0, new(Kills: 4, Deaths: 2, Medals: 3));
        Assert.Equal((4, 2, 3), FilmPlayers.Stats(p));
    }
}

public class TimelineMedalTests
{
    private static readonly string?[] Players = ["Test Alpha", "Test Bravo"];

    private static List<SummaryEvent> Events(params Event[] events) => Summary.Decode(SummaryPayload(events), Players);

    [Fact]
    public void Each_medal_goes_to_the_nearest_kill_not_the_first_in_range()
    {
        // Two kills 400 ms apart, then the second kill's medals. Both are within 500 ms of the
        // first kill, but they belong to the second.
        var events = Events(
            Kill("Test Alpha", 10_000), Kill("Test Alpha", 10_400),
            Medal("Test Alpha", 10_400, 0), Medal("Test Alpha", 10_400, 108, tier: 25));
        var medals = Timeline.AssignMedals(events);
        Assert.Equal(1, medals[2]);
        Assert.Equal(1, medals[3]);
        Assert.DoesNotContain(0, medals.Values);
    }

    [Fact]
    public void A_medal_recorded_just_before_its_kill_still_counts()
    {
        var medals = Timeline.AssignMedals(Events(Medal("Test Alpha", 9_998, 26, tier: 25), Kill("Test Alpha", 10_000)));
        Assert.Equal(1, medals[0]);
    }

    [Fact]
    public void A_medal_far_from_any_kill_is_left_unassigned()
    {
        var medals = Timeline.AssignMedals(Events(Kill("Test Alpha", 10_000), Medal("Test Alpha", 11_000, 91, tier: 25)));
        Assert.Empty(medals);
    }

    [Fact]
    public void Another_players_kill_is_never_used()
    {
        var medals = Timeline.AssignMedals(Events(Kill("Test Bravo", 10_000), Medal("Test Alpha", 10_000, 0)));
        Assert.Empty(medals);
    }

    [Fact]
    public void On_a_tie_the_earlier_kill_wins()
    {
        var medals = Timeline.AssignMedals(Events(Kill("Test Alpha", 10_000), Medal("Test Alpha", 10_100, 0), Kill("Test Alpha", 10_200)));
        Assert.Equal(0, medals[1]);
    }
}

public class TimelineRunTests
{
    // Team 0: Alpha, Bravo. Team 1: Charlie, Delta.
    private static readonly HeaderPlayer[] Roster =
    [
        new(Xuid(1), "Test Alpha"), new(Xuid(2), "Test Bravo"),
        new(Xuid(3), "Test Charlie"), new(Xuid(4), "Test Delta", ShortRecordOffset),
    ];

    private static readonly Event[] Events =
    [
        Kill("Test Alpha", 50_000), Death("Test Charlie", 50_001),
        Kill("Test Alpha", 50_400), Death("Test Delta", 50_400) with { BodyOffset = ShortRecordOffset },
        Medal("Test Alpha", 50_400, 0),
        Death("Test Bravo", 70_000),                          // a suicide
        Kill("Test Charlie", 90_000), Death("Test Alpha", 90_001),
    ];

    private static MatchStats Board(string id, bool rosterOnScoreboard = true) => Synthetic.Board(id,
    [
        Human(rosterOnScoreboard ? Xuid(1) : Xuid(91), 0, new(Kills: 2, Deaths: 1, Medals: 1)),
        Human(Xuid(2), 0, new(Deaths: 1, Suicides: 1)),
        Human(Xuid(3), 1, new(Kills: 1, Deaths: 1)),
        Human(Xuid(4), 1, new(Deaths: 1)),
    ], (0, 2), (1, 1));

    private static string Run(TempCache t, string id, bool feed = true)
    {
        var o = new StringWriter();
        Assert.Equal(0, Timeline.Run(t.Cache, id, showNames: false, feed, o));
        return o.ToString();
    }

    [Fact]
    public void Writes_the_kill_feed_with_medals_on_the_right_kill()
    {
        using var t = new TempCache();
        var id = Guid.NewGuid().ToString();
        WriteFilm(t.Cache, id, Roster, Events, Board(id));
        var lines = Run(t, id).Split('\n');

        var first = Assert.Single(lines, l => l.Contains("00:50.0") && l.Contains("→ T1 P2"));
        var second = Assert.Single(lines, l => l.Contains("00:50.4") && l.Contains("→ T1 P3"));
        Assert.DoesNotContain("[", first);
        Assert.Contains("[medal code 0]", second);
        Assert.Contains(lines, l => l.Contains("T0 P1") && l.Contains("died: suicide"));
    }

    [Fact]
    public void Score_flow_and_head_to_head_match_the_events()
    {
        using var t = new TempCache();
        var id = Guid.NewGuid().ToString();
        WriteFilm(t.Cache, id, Roster, Events, Board(id));
        var output = Run(t, id, feed: false);

        Assert.Contains("T0: 2 kills in the film + 0 by bots and late joiners = 2; scoreboard team kills 2  [matches]", output);
        Assert.Contains("T1: 1 kills in the film + 0 by bots and late joiners = 1; scoreboard team kills 1  [matches]", output);
        var alpha = Assert.Single(output.Split('\n'), l => l.StartsWith("  T0 P0") && l.Contains("nemesis") == false && l.Contains("P2 (1)"));
        Assert.Matches(@"T0 P0\s+·\s+·\s+1\s+1\s+0\s+P2 \(1\)", alpha);
        Assert.DoesNotContain("Kill feed", output);
    }

    [Fact]
    public void Gamertags_appear_only_when_asked_for()
    {
        using var t = new TempCache();
        var id = Guid.NewGuid().ToString();
        WriteFilm(t.Cache, id, Roster, Events, Board(id));
        Assert.DoesNotContain("Test Alpha", Run(t, id));

        var o = new StringWriter();
        Timeline.Run(t.Cache, id, showNames: true, feed: true, o);
        Assert.Contains("Test Alpha", o.ToString());
    }

    [Fact]
    public void A_film_with_no_decodable_players_reports_it_instead_of_crashing()
    {
        using var t = new TempCache();
        var id = Guid.NewGuid().ToString();
        WriteFilm(t.Cache, id, [], Events, Board(id));
        var o = new StringWriter();
        Assert.Equal(1, Timeline.Run(t.Cache, id, showNames: false, feed: true, o));
        Assert.Contains("No players could be decoded", o.ToString());
    }

    [Fact]
    public void A_roster_player_missing_from_the_scoreboard_is_an_error()
    {
        using var t = new TempCache();
        var id = Guid.NewGuid().ToString();
        WriteFilm(t.Cache, id, Roster, Events, Board(id, rosterOnScoreboard: false));
        Assert.Throws<InvalidDataException>(() => Timeline.Run(t.Cache, id, false, true, new StringWriter()));
    }
}
