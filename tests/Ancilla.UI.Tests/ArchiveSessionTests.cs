using System.Net;
using Ancilla.Core;
using Ancilla.Data;
using Ancilla.Testing;
using Ancilla.UI.Services;

namespace Ancilla.UI.Tests;

/// <summary>The local archive behind <see cref="HaloSession"/>: restarts, background sync, backfill and back-off.</summary>
public sealed class ArchiveSessionTests : IDisposable
{
    private readonly List<Harness> _harnesses = [];

    private Harness NewHarness(HaloWorld? world = null)
    {
        var h = new Harness(world);
        _harnesses.Add(h);
        return h;
    }

    public void Dispose() { foreach (var h in _harnesses) h.Dispose(); }

    private static int Requests(Harness h, string fragment) =>
        h.Halo.Requests.Count(r => r.Uri.ToString().Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a restart leaves: the archive on disk, but nothing in memory and a new session.</summary>
    private static async Task<HaloSession> RestartedAsync(Harness h)
    {
        var s = new HaloSession(h.Halo.Factory, new HaloCache(), h.Assets, h.Films, h.Config, h.Diagnostics, h.Archive, h.Snapshots, h.Waypoint, h.Settings)
        {
            Retry = new RetryPolicy(4, TimeSpan.FromMilliseconds(1)),
        };
        await s.SignInAsync(Spartans.Token);
        return s;
    }

    [Fact]
    public async Task After_a_restart_loaded_matches_show_without_asking_for_them_again()
    {
        var h = NewHarness();
        var first = await h.SignedInAsync();
        var before = await first.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 24);
        Assert.Equal(HaloWorld.MatchmadeCount, before.Count);

        var calls = h.Halo.Requests.Count;
        var second = await RestartedAsync(h);
        var signInCalls = h.Halo.Requests.Count - calls;   // the profile and service tag, not matches
        var after = await second.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 24);

        Assert.Equal(before.Select(m => m.Entry.MatchId), after.Select(m => m.Entry.MatchId));
        Assert.Equal(before.Select(m => m.Stats.Players.Count), after.Select(m => m.Stats.Players.Count));
        await second.BackgroundSync.OrCompleted();
        Assert.Equal(HaloWorld.MatchmadeCount, Requests(h, "/stats"));   // every scoreboard was fetched once, in the first run
        Assert.Equal(calls + signInCalls + 1, h.Halo.Requests.Count);   // just the one background check for new matches
    }

    [Fact]
    public async Task Skill_results_and_names_come_back_after_a_restart_too()
    {
        var h = NewHarness();
        var first = await h.SignedInAsync();
        var recent = await first.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 6);
        var ids = recent.Select(m => m.Entry.MatchId).ToList();
        var skills = await first.GetSkillsAsync(ids);
        await first.GetMapAsync(recent[0].Entry.MatchInfo.MapVariant);
        var skillCalls = Requests(h, "/skill");
        var ugcCalls = Requests(h, "/maps/");

        var second = await RestartedAsync(h);
        var again = await second.GetSkillsAsync(ids);
        var map = await second.GetMapAsync(recent[0].Entry.MatchInfo.MapVariant);

        Assert.Equal(skills.Keys.Order(), again.Keys.Order());
        Assert.NotNull(map?.PublicName);
        Assert.Equal(skillCalls, Requests(h, "/skill"));   // Firefight has no skill result, and that is remembered too
        Assert.Equal(ugcCalls, Requests(h, "/maps/"));
    }

    [Fact]
    public async Task Matches_played_since_are_found_in_the_background_and_on_refresh()
    {
        var world = new HaloWorld();
        var newest = world.Games.Where(g => g.Type == MatchHistoryType.Matchmaking).OrderByDescending(g => g.Entry.MatchInfo.StartTime).Take(3).ToList();
        foreach (var g in newest) world.Games.Remove(g);
        var h = NewHarness(world);
        var first = await h.SignedInAsync();
        await first.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 10);

        world.Games.AddRange(newest);   // three more games get played

        // A restarted app shows what it has straight away, and finds the new ones in the background.
        var second = await RestartedAsync(h);
        var shown = await second.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 5);
        Assert.DoesNotContain(shown, m => newest.Any(g => g.Id == m.Entry.MatchId));
        await second.BackgroundSync.OrCompleted();
        var later = await second.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 5);
        Assert.Equal(newest.OrderByDescending(g => g.Entry.MatchInfo.StartTime).Select(g => g.Id), later.Take(3).Select(m => m.Entry.MatchId));

        // Refreshing waits for them instead.
        var third = await RestartedAsync(h);
        third.ForgetHistory();
        var refreshed = await third.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 5);
        Assert.Equal(later.Select(m => m.Entry.MatchId), refreshed.Select(m => m.Entry.MatchId));
    }

    [Fact]
    public async Task Backfill_reaches_past_two_hundred_matches()
    {
        var h = NewHarness(new HaloWorld(matchmade: 230));
        var s = await h.SignedInAsync();
        await s.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 25);
        Assert.True(s.ArchiveSummary().Matches < 100);

        var messages = new List<string>();
        var result = await s.BackfillAsync(m => { lock (messages) messages.Add(m); });

        Assert.Equal(h.World.Games.Count, result.Matches);   // 230 matchmade + 2 custom
        Assert.True(result.Matches > 200);
        var summary = s.ArchiveSummary();
        Assert.Equal(h.World.Games.Count, summary.Matches);
        Assert.Equal(h.World.Games.Count, summary.WithScoreboard);
        Assert.Equal(h.World.Matchmade().Count(g => !g.IsPve), summary.WithSkill);   // matchmade PvP only
        Assert.Contains(messages, m => m.Contains("scoreboards"));

        // Everything is now readable with no API calls at all, and a second backfill has nothing to fetch.
        var calls = h.Halo.Requests.Count;
        var all = await s.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, HaloSession.AllStored);
        Assert.Equal(230, all.Count);
        var again = await s.BackfillAsync();
        Assert.Equal(0, again.Added);
        Assert.Equal(0, again.ScoreboardsFetched);
        // The only thing it may ask for is the history pages again, and the skill of a game too new to be sure about.
        Assert.True(h.Halo.Requests.Count - calls - HistoryPagesSince(h, calls) <= 1);
    }

    // A second backfill still re-checks the history pages; that's all it may ask for.
    private static int HistoryPagesSince(Harness h, int calls) =>
        h.Halo.Requests.Skip(calls).Count(r => r.Uri.AbsolutePath.EndsWith("/matches", StringComparison.Ordinal));

    [Fact]
    public async Task Everything_stored_covers_more_than_a_page_and_keeps_firefight_identifiable()
    {
        var h = NewHarness(new HaloWorld(matchmade: 60));
        var s = await h.SignedInAsync();
        await s.BackfillAsync();

        var restarted = await RestartedAsync(h);
        var all = await restarted.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, HaloSession.AllStored);
        Assert.Equal(60, all.Count);
        Assert.Equal(h.World.Matchmade().Count(g => g.IsPve), all.Count(m => m.IsPve(restarted.Xuid)));
        Assert.Equal(h.World.Matchmade().Count(g => !g.IsPve), all.Count(m => !m.IsPve(restarted.Xuid)));
    }

    [Fact]
    public async Task Everything_stored_does_not_page_through_the_whole_history_when_the_archive_is_empty()
    {
        var h = NewHarness(new HaloWorld(matchmade: 120));
        var s = await h.SignedInAsync();
        var first = await s.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, HaloSession.AllStored);
        Assert.Equal(25, first.Count);   // one page to get started, not all 120
        Assert.Equal(1, Requests(h, "/matches?"));
    }

    [Fact]
    public async Task A_busy_service_is_backed_off_from_and_retried()
    {
        var h = NewHarness();
        var refusals = 2;
        h.Halo.Fail(u => u.AbsolutePath.EndsWith("/stats") && Interlocked.Decrement(ref refusals) >= 0, HttpStatusCode.TooManyRequests);
        var s = await h.SignedInAsync();
        var recent = await s.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 3);

        Assert.Equal(3, recent.Count);
        Assert.True(Requests(h, "/stats") >= 5);   // 3 matches and 2 refusals
    }

    [Fact]
    public async Task Backing_off_gives_up_in_the_end()
    {
        var h = NewHarness();
        h.Halo.Fail("/matches?", HttpStatusCode.TooManyRequests);
        var s = await h.SignedInAsync();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => s.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 3));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(4, Requests(h, "/matches?"));
    }

    [Fact]
    public async Task Snapshots_from_the_old_json_file_are_imported_at_sign_in_once()
    {
        var h = NewHarness();
        await h.Snapshots.AppendAsync(new ProgressSnapshot(DateTimeOffset.UtcNow.AddDays(-3), 40, 4_000));
        var s = await h.SignedInAsync();
        Assert.Single(await s.Snapshots.LoadAsync());
        await (await h.SignedInAsync()).Snapshots.LoadAsync();
        Assert.Single(await s.Snapshots.LoadAsync());
    }

    [Fact]
    public async Task Exports_cover_every_stored_match_with_names()
    {
        var h = NewHarness();
        var s = await h.SignedInAsync();
        await s.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 8);
        foreach (var m in await s.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 8))
        {
            await s.GetMapAsync(m.Entry.MatchInfo.MapVariant);
            await s.GetModeAsync(m.Entry.MatchInfo.UgcGameVariant);
        }

        var rows = s.ExportRows();
        Assert.Equal(HaloWorld.MatchmadeCount, rows.Count);   // all the matchmade history; eight have scoreboards
        Assert.Equal(8, rows.Count(r => r.Kills is not null));
        Assert.All(rows.Where(r => r.Kills is not null), r => Assert.False(string.IsNullOrEmpty(r.Map)));
        Assert.Equal(HaloWorld.MatchmadeCount + 1, ArchiveExport.ToCsv(rows).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task The_archive_location_is_shown_relative_to_home()
    {
        var h = NewHarness();
        var s = await h.SignedInAsync();
        Assert.Equal(h.Archive.DisplayPath, s.ArchiveFile);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Path.DirectorySeparatorChar, s.ArchiveFile + "x", StringComparison.Ordinal);
    }
}

internal static class TaskExtensions
{
    /// <summary>Waits for a task that may not have started (null counts as done).</summary>
    public static Task OrCompleted(this Task? t) => t ?? Task.CompletedTask;
}
