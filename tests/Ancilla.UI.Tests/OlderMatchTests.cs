using System.Net;
using Ancilla.Core;
using Ancilla.Testing;
using Ancilla.UI.Services;

namespace Ancilla.UI.Tests;

/// <summary>
/// Old matches whose skill results or scoreboards come back missing or in a shape the models don't
/// expect. Loading older matches (backfill, or paging the match list) skips them instead of failing.
/// </summary>
public sealed class OlderMatchTests : IDisposable
{
    private readonly List<Harness> _harnesses = [];

    public void Dispose() { foreach (var h in _harnesses) h.Dispose(); }

    private Harness NewHarness()
    {
        var h = new Harness(new HaloWorld(matchmade: 60));
        _harnesses.Add(h);
        return h;
    }

    /// <summary>The oldest few matchmade PvP games, the ones a backfill reaches last.</summary>
    private static HashSet<string> Oldest(Harness h, int n = 5) =>
        [.. h.World.Matchmade().Where(g => !g.IsPve).OrderBy(g => g.Entry.MatchInfo.StartTime).Take(n).Select(g => g.Id)];

    private static Func<Uri, bool> For(HashSet<string> ids, string suffix) =>
        u => u.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal) && ids.Any(id => u.AbsolutePath.Contains(id, StringComparison.Ordinal));

    public static TheoryData<string> BadSkillBodies => new()
    {
        """{"Value":null}""",
        """{}""",
        "null",
        """{"Value":[{"Id":"xuid(1)","ResultCode":0,"Result":{"TeamMmr":null,"TeamId":0}}]}""",
        """{"Value":[{"Id":"xuid(1)","ResultCode":0,"Result":{"TeamMmr":1500.5,"TeamId":0,"RankRecap":{"PreMatchCsr":{"Value":1500.5}}}}]}""",
        """{"Value":[{"Id":"xuid(1)","ResultCode":0,"Result":{"TeamMmr":1500,"TeamId":0,"StatPerformances":{"Kills":{"Count":10,"Expected":null,"StdDev":null}}}}]}""",
        "not json",
    };

    [Fact]
    public async Task Backfill_skips_older_matches_that_have_no_skill_result()
    {
        var h = NewHarness();
        h.Halo.Fail(For(Oldest(h), "/skill"), HttpStatusCode.NotFound);
        var s = await h.SignedInAsync();

        var result = await s.BackfillAsync();

        Assert.Equal(h.World.Games.Count, result.Matches);
    }

    [Theory]
    [MemberData(nameof(BadSkillBodies))]
    public async Task Backfill_skips_skill_results_it_cannot_read(string body)
    {
        var h = NewHarness();
        var oldest = Oldest(h);
        h.Halo.Respond(For(oldest, "/skill"), body);
        var s = await h.SignedInAsync();

        var result = await s.BackfillAsync();

        Assert.Equal(h.World.Games.Count, result.Matches);
        foreach (var id in oldest) Assert.Null(await s.GetSkillAsync(id));
    }

    [Theory]
    [MemberData(nameof(BadSkillBodies))]
    public async Task Skills_for_a_page_of_matches_skip_ones_it_cannot_read(string body)
    {
        var h = NewHarness();
        var oldest = Oldest(h);
        h.Halo.Respond(For(oldest, "/skill"), body);
        var s = await h.SignedInAsync();
        var ids = h.World.Matchmade().Where(g => !g.IsPve).Select(g => g.Id).ToList();

        var skills = await s.GetSkillsAsync(ids);

        Assert.Equal(ids.Count - oldest.Count, skills.Count);
    }

    // The cause of the real crash: a skill result whose expected kills was infinity, which the archive couldn't write.
    [Theory]
    [InlineData("1e400")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"-Infinity\"")]
    [InlineData("\"NaN\"")]
    public async Task Backfill_keeps_skill_results_with_numbers_that_are_not_finite_and_drops_those_numbers(string number)
    {
        var h = NewHarness();
        var oldest = Oldest(h);
        h.Halo.Respond(For(oldest, "/skill"),
            """{"Value":[{"Id":"xuid(1)","ResultCode":0,"Result":{"TeamMmr":NUMBER,"TeamId":0,"TeamMmrs":{"0":1500,"1":NUMBER},"StatPerformances":{"Kills":{"Count":10,"Expected":NUMBER,"StdDev":2},"Deaths":{"Count":8,"Expected":9,"StdDev":2}}}}]}""".Replace("NUMBER", number));
        var s = await h.SignedInAsync();

        var result = await s.BackfillAsync();

        Assert.Equal(h.World.Games.Count, result.Matches);
        var restarted = new HaloSession(h.Halo.Factory, new HaloCache(), h.Assets, h.Films, h.Config, h.Diagnostics, h.Archive, h.Snapshots, h.Waypoint, h.Settings);
        await restarted.SignInAsync(Spartans.Token);
        var calls = h.Halo.Requests.Count;
        foreach (var id in oldest)
        {
            var skill = await restarted.GetSkillAsync(id);   // from the archive: it was stored
            Assert.NotNull(skill);
            Assert.Equal(["Deaths"], skill.StatPerformances!.Keys);
            Assert.Equal(["0"], skill.TeamMmrs!.Keys);
            Assert.Equal(0, skill.TeamMmr);
        }
        Assert.Equal(calls, h.Halo.Requests.Count);
    }

    [Theory]
    [InlineData("""{"MatchId":null}""")]
    [InlineData("""{}""")]
    [InlineData("null")]
    [InlineData("not json")]
    public async Task Backfill_skips_scoreboards_it_cannot_read(string body)
    {
        var h = NewHarness();
        var oldest = Oldest(h);
        h.Halo.Respond(For(oldest, "/stats"), body);
        var s = await h.SignedInAsync();

        var result = await s.BackfillAsync();

        Assert.Equal(h.World.Games.Count, result.Matches);
        Assert.Equal(h.World.Games.Count - oldest.Count, s.ArchiveSummary().WithScoreboard);
    }
}
