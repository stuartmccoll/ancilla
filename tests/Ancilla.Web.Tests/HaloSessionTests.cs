using System.Net;
using Ancilla.Core;
using Ancilla.Testing;
using Ancilla.Theater;
using Ancilla.Web.Services;
using static Ancilla.Testing.Spartans;

namespace Ancilla.Web.Tests;

public sealed class HaloSessionTests : IDisposable
{
    private readonly Harness _h = new();
    public void Dispose() => _h.Dispose();

    private int Requests(string fragment) => _h.Halo.Requests.Count(r => r.Uri.ToString().Contains(fragment, StringComparison.OrdinalIgnoreCase));

    // ---- Sign-in ----------------------------------------------------------------------

    [Fact]
    public async Task Signing_in_loads_the_player_and_their_service_tag()
    {
        var s = _h.NewSession();
        var changed = 0;
        s.Changed += () => changed++;
        await s.SignInAsync(Token);

        Assert.True(s.IsSignedIn);
        Assert.Equal(NobleSix.Gamertag, s.Me!.Gamertag);
        Assert.Equal(NobleSix.Xuid, s.Xuid);
        Assert.Equal("B312", s.ServiceTag);
        Assert.False(s.FromEnvironment);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task A_rejected_token_leaves_the_session_signed_out()
    {
        var s = _h.NewSession();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => s.SignInAsync("v4=flood-spore"));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.False(s.IsSignedIn);
        Assert.Throws<InvalidOperationException>(() => s.Client);
        Assert.Throws<InvalidOperationException>(() => s.Xuid);
    }

    [Fact]
    public async Task A_missing_service_tag_does_not_stop_sign_in()
    {
        _h.Halo.Fail("/customization/appearance", HttpStatusCode.NotFound);
        var s = await _h.SignedInAsync();
        Assert.True(s.IsSignedIn);
        Assert.Null(s.ServiceTag);
    }

    [Fact]
    public async Task Environment_sign_in_needs_the_variable()
    {
        Assert.False(await _h.NewSession().TrySignInFromEnvironmentAsync());

        using var env = new Harness(envToken: Token);
        var s = env.NewSession();
        Assert.True(await s.TrySignInFromEnvironmentAsync());
        Assert.True(s.FromEnvironment);
        Assert.Equal(NobleSix.Gamertag, s.Me!.Gamertag);
    }

    [Fact]
    public async Task Signing_out_forgets_everything()
    {
        var s = await _h.SignedInAsync();
        await s.GetServiceRecordAsync();
        s.Report = new ProgressReport("Noble Six", 1, new CareerStatus(0, 1, "", null, 0, 1, false), [], new XpPerMatchEstimate(1, ""), []);
        s.SignOut();
        Assert.False(s.IsSignedIn);
        Assert.Null(s.Report);
        Assert.Null(s.ServiceTag);
        Assert.Empty(s.Matches.Rows);
    }

    // ---- Service records ------------------------------------------------------------------

    [Fact]
    public async Task The_service_record_is_kept_until_refreshed()
    {
        var s = await _h.SignedInAsync();
        await s.GetServiceRecordAsync();
        await s.GetServiceRecordAsync();
        Assert.Equal(1, Requests("/servicerecord"));
        Assert.NotNull(s.ServiceRecordFetchedAt);
        await s.GetServiceRecordAsync(refresh: true);
        Assert.Equal(2, Requests("/servicerecord"));
    }

    [Fact]
    public async Task A_failed_service_record_is_retried()
    {
        var s = await _h.SignedInAsync();
        _h.Halo.Fail(u => u.AbsolutePath.EndsWith("/servicerecord") && Requests("/servicerecord") <= 1);
        await Assert.ThrowsAsync<HttpRequestException>(() => s.GetServiceRecordAsync());
        Assert.Equal(_h.World.ServiceRecord().MatchesCompleted, (await s.GetServiceRecordAsync()).MatchesCompleted);
    }

    [Fact]
    public async Task Pvp_is_everything_minus_firefight()
    {
        var s = await _h.SignedInAsync();
        var pvp = await s.GetServiceRecordAsync(new StatsFilter(StatsScope.PvP));
        var games = _h.World.Matchmade().Where(g => !g.IsPve).ToList();
        Assert.Equal(games.Count, pvp.MatchesCompleted);
        Assert.Equal(games.Sum(g => g.Me.Stats!.CoreStats.Kills), pvp.CoreStats.Kills);
        Assert.DoesNotContain("PveStats", pvp.ModeStats.Keys);
        Assert.Equal(1, Requests("gameVariantCategory=42"));
    }

    [Theory]
    [InlineData(StatsScope.Firefight, RankedFilter.All, "?gameVariantCategory=42")]
    [InlineData(StatsScope.All, RankedFilter.Ranked, "?isRanked=true")]
    [InlineData(StatsScope.All, RankedFilter.All, "")]
    public async Task Other_filters_go_straight_to_the_api(StatsScope scope, RankedFilter ranked, string query)
    {
        var s = await _h.SignedInAsync();
        await s.GetServiceRecordAsync(new StatsFilter(scope, ranked));
        Assert.Contains(_h.Halo.Requests, r => r.Uri.AbsolutePath.EndsWith("/servicerecord") && r.Uri.Query == query);
    }

    [Fact]
    public async Task Ranked_pvp_subtracts_ranked_firefight()
    {
        var s = await _h.SignedInAsync();
        var r = await s.GetServiceRecordAsync(new StatsFilter(StatsScope.PvP, RankedFilter.Ranked));
        Assert.Equal(_h.World.Matchmade().Count(g => g.IsRanked && !g.IsPve), r.MatchesCompleted);
        Assert.Contains(_h.Halo.Requests, x => x.Uri.Query == "?gameVariantCategory=42&isRanked=true");
    }

    [Fact]
    public async Task Mode_records_cover_modes_with_matches_and_skip_failures()
    {
        _h.Halo.Fail("gameVariantCategory=15");   // Capture the Flag is down
        var s = await _h.SignedInAsync();
        var modes = await s.GetModeRecordsAsync(new StatsFilter(StatsScope.PvP));
        Assert.Equal([6, 18], modes.Select(m => m.Category).Order());
        Assert.DoesNotContain(modes, m => m.Category == GameVariantCategories.Firefight);
        Assert.Equal(_h.World.Matchmade().Count(g => g.Category == 6), modes.Single(m => m.Category == 6).Record.MatchesCompleted);

        var ff = await s.GetModeRecordsAsync(new StatsFilter(StatsScope.Firefight));
        Assert.Equal(GameVariantCategories.Firefight, Assert.Single(ff).Category);
    }

    [Fact]
    public async Task Parallel_calls_are_throttled_to_eight()
    {
        _h.Halo.Latency = TimeSpan.FromMilliseconds(30);
        var s = await _h.SignedInAsync();
        await s.GetModeRecordsAsync(StatsFilter.Everything);   // 16 categories at once
        Assert.InRange(_h.Halo.MaxInFlight, 2, 8);
    }

    [Fact]
    public async Task Only_ranked_playlists_get_a_csr()
    {
        var s = await _h.SignedInAsync();
        var ranked = await s.GetRankedAsync();
        var arena = Assert.Single(ranked);
        Assert.Equal("Ranked Arena", arena.Name);
        Assert.Equal("Diamond 6", arena.Csr.Current.Display);
        Assert.Same(ranked, await s.GetRankedAsync());
    }

    [Fact]
    public async Task Ranked_playlists_get_their_names_from_match_history()
    {
        _h.World.RankedPlaylistIds.Add(HaloWorld.QuickPlay.AssetId);   // not one the app knows by name
        var s = await _h.SignedInAsync();
        var ranked = await s.GetRankedAsync();
        Assert.Equal(["Quick Play", "Ranked Arena"], ranked.Select(r => r.Name).Order());
    }

    [Fact]
    public async Task A_ranked_playlist_keeps_a_plain_name_when_history_fails()
    {
        _h.World.RankedPlaylistIds.Add(HaloWorld.QuickPlay.AssetId);
        _h.Halo.Fail("/matches?");
        var s = await _h.SignedInAsync();
        var ranked = await s.GetRankedAsync();
        Assert.Equal(HaloSession.UnnamedPlaylist, ranked.Single(r => r.PlaylistId == HaloWorld.QuickPlay.AssetId).Name);
    }

    [Fact]
    public async Task Refreshing_a_filtered_record_asks_the_api_again()
    {
        var s = await _h.SignedInAsync();
        var pvp = new StatsFilter(StatsScope.PvP);
        await s.GetServiceRecordAsync(pvp);
        await s.GetServiceRecordAsync(pvp);
        Assert.Equal(1, Requests("gameVariantCategory=42"));
        await s.GetServiceRecordAsync(pvp, refresh: true);
        Assert.Equal(2, Requests("gameVariantCategory=42"));
    }

    // ---- Matches ------------------------------------------------------------------------

    [Fact]
    public async Task Recent_matches_page_through_history()
    {
        var s = await _h.SignedInAsync();
        var progress = new List<(int, int)>();
        var recent = await s.GetRecentMatchesAsync(MatchHistoryType.All, 26, (d, t) => { lock (progress) progress.Add((d, t)); });

        Assert.Equal(26, recent.Count);
        Assert.Equal(2, Requests("/matches?"));   // 25, then 1
        Assert.Equal(recent.OrderByDescending(m => m.Entry.MatchInfo.StartTime).Select(m => m.Entry.MatchId), recent.Select(m => m.Entry.MatchId));
        Assert.Contains((26, 26), progress);
    }

    [Fact]
    public async Task History_is_kept_until_forgotten()
    {
        var s = await _h.SignedInAsync();
        await s.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 10);
        await s.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 10);
        Assert.Equal(1, Requests("/matches?"));
        s.ForgetHistory();
        await s.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 10);
        Assert.Equal(2, Requests("/matches?"));
        Assert.Equal(10, Requests("/stats"));   // scoreboards came from the shared cache the second time
    }

    [Fact]
    public async Task A_missing_scoreboard_drops_just_that_match()
    {
        var gone = _h.World.Games[3].Id;
        _h.Halo.Fail($"/matches/{gone}/stats", HttpStatusCode.NotFound);
        var s = await _h.SignedInAsync();
        var recent = await s.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 10);
        Assert.Equal(9, recent.Count);
        Assert.DoesNotContain(recent, m => m.Entry.MatchId == gone);
    }

    [Fact]
    public async Task Scoreboards_are_shared_between_sessions()
    {
        var a = await _h.SignedInAsync();
        var b = await _h.SignedInAsync();
        var id = _h.World.Games[0].Id;
        Assert.Same(await a.GetMatchAsync(id), await b.GetMatchAsync(id));
        Assert.Equal(1, Requests($"/matches/{id}/stats"));
    }

    [Fact]
    public async Task The_match_list_loads_and_fills_in()
    {
        var s = await _h.SignedInAsync();
        await s.LoadMoreMatchesAsync();
        var rows = s.Matches.Rows;
        Assert.Equal(HaloWorld.MatchmadeCount, rows.Count);
        Assert.False(s.Matches.HasMore);   // a short page means the end
        Assert.False(s.Matches.Loading);

        await WaitUntil(() => rows.All(r => r.Loaded));
        var first = rows[0];
        var game = _h.World.GameById(first.Entry.MatchId);
        Assert.Equal("Live Fire", first.Map?.PublicName);
        Assert.Equal("Slayer", first.Mode?.PublicName);
        Assert.Equal("Ranked Arena", first.Playlist?.PublicName);
        Assert.Equal(game.Me.Stats!.CoreStats.Kills, first.Mine!.Kills);
        Assert.NotNull(first.ScoreLine);
    }

    [Fact]
    public async Task Switching_match_type_starts_a_new_list()
    {
        var s = await _h.SignedInAsync();
        await s.LoadMoreMatchesAsync();
        s.SetMatchType(MatchHistoryType.Custom);
        Assert.Empty(s.Matches.Rows);
        await s.LoadMoreMatchesAsync();
        Assert.Equal(HaloWorld.CustomCount, s.Matches.Rows.Count);
        Assert.All(s.Matches.Rows, r => Assert.Null(r.Entry.MatchInfo.Playlist));
    }

    [Fact]
    public async Task A_failed_page_shows_an_error_on_the_list()
    {
        _h.Halo.Fail("/matches?", HttpStatusCode.BadGateway);
        var s = await _h.SignedInAsync();
        await s.LoadMoreMatchesAsync();
        Assert.StartsWith("502", s.Matches.Error);
        Assert.Empty(s.Matches.Rows);
        Assert.False(s.Matches.Loading);
    }

    [Fact]
    public void Score_lines_put_your_team_first_or_give_your_place()
    {
        var w = new HaloWorld();
        var team = w.Games.First(g => g.Stats.MatchInfo.TeamsEnabled && g.Stats.Teams.Count == 2);
        var scores = team.Stats.Teams.OrderBy(t => t.TeamId).Select(t => t.Stats.CoreStats.Score.ToString("N0")).ToArray();
        Assert.Equal($"{scores[0]}–{scores[1]}", HaloSession.ScoreLine(team.Stats, team.Me));
        var cobra = team.Stats.Players.First(p => p.LastTeamId == 1);
        Assert.Equal($"{scores[1]}–{scores[0]}", HaloSession.ScoreLine(team.Stats, cobra));

        var ffa = w.Games.First(g => !g.Stats.MatchInfo.TeamsEnabled);
        Assert.Equal(Fmt.Ordinal(ffa.Me.Rank), HaloSession.ScoreLine(ffa.Stats, ffa.Me));
        Assert.Null(HaloSession.ScoreLine(ffa.Stats, null));
    }

    // ---- Skill, UGC, users, images -----------------------------------------------------------

    [Fact]
    public async Task Skill_is_missing_for_custom_games_and_firefight()
    {
        var s = await _h.SignedInAsync();
        var custom = _h.World.Games.First(g => g.Type == MatchHistoryType.Custom).Id;
        var ff = _h.World.Games.First(g => g.IsPve).Id;
        var slayer = _h.World.Games[0].Id;
        Assert.Null(await s.GetSkillAsync(custom));
        var skills = await s.GetSkillsAsync([custom, ff, slayer, slayer]);
        Assert.Equal([slayer], skills.Keys);
        Assert.True(skills[slayer].RankRecap!.PostMatchCsr!.IsRanked);
    }

    [Fact]
    public async Task Bad_or_empty_asset_refs_are_skipped_without_a_request()
    {
        var s = await _h.SignedInAsync();
        var before = _h.Halo.Requests.Count;
        Assert.Null(await s.GetMapAsync(null));
        Assert.Null(await s.GetMapAsync(new AssetRef(2, "../users/me", HaloWorld.Ids.Version(1))));
        Assert.Null(await s.GetModeAsync(new AssetRef(6, Guid.Empty.ToString(), Guid.Empty.ToString())));
        Assert.Equal(before, _h.Halo.Requests.Count);
    }

    [Fact]
    public async Task Deleted_ugc_is_null_and_playlist_names_are_remembered()
    {
        var s = await _h.SignedInAsync();
        Assert.Null(await s.GetMapAsync(new AssetRef(2, HaloWorld.Ids.Map(99), HaloWorld.Ids.Version(99))));
        Assert.Equal("Quick Play", (await s.GetPlaylistAsync(HaloWorld.QuickPlay.Ref(3)))!.PublicName);
    }

    [Fact]
    public async Task Users_are_cached_and_malformed_ids_skipped()
    {
        var s = await _h.SignedInAsync();
        var users = await s.GetUsersAsync([Kat.Xuid, Jorge.Xuid, "bid(2001.0)", "Arbiter"]);
        Assert.Equal("Kat B320", users[Kat.Xuid].Gamertag);
        await s.GetUsersAsync([Kat.Xuid]);
        Assert.Equal(1, Requests("/users?"));
    }

    [Fact]
    public async Task Users_degrade_quietly()
    {
        var s = await _h.SignedInAsync();
        _h.Halo.Fail("/users?");
        Assert.Empty(await s.GetUsersAsync([Kat.Xuid]));
    }

    [Fact]
    public async Task The_medal_book_serves_its_sheet_through_the_asset_cache()
    {
        var s = await _h.SignedInAsync();
        var book = await s.GetMedalBookAsync();
        Assert.Equal("assets/waypoint/" + HaloWorld.Medals.SheetPath, book.SheetUrl);
        Assert.Equal(128, book.Sprite.Size);
        Assert.True(_h.Assets.TryGet("waypoint/" + HaloWorld.Medals.SheetPath, out _));
        var killjoy = book.ById[HaloWorld.Medals.Killjoy];
        Assert.Equal("normal", book.DifficultyOf(killjoy));
        Assert.Equal("spree", book.TypeOf(killjoy));
        Assert.Equal("", book.TypeOf(killjoy with { TypeIndex = 99 }));
    }

    [Fact]
    public async Task Cms_images_degrade_to_null()
    {
        var s = await _h.SignedInAsync();
        Assert.Null(await s.GetCmsImageUrlAsync(null));
        Assert.Equal("assets/cms/career_rank/x.png", await s.GetCmsImageUrlAsync("career_rank/x.png"));
        _h.Halo.Fail("/images/file/broken");
        Assert.Null(await s.GetCmsImageUrlAsync("broken.png"));
    }

    // ---- Films ----------------------------------------------------------------------------

    [Fact]
    public async Task A_film_that_cannot_be_fetched_can_be_retried()
    {
        var s = await _h.SignedInAsync();
        var id = _h.World.Games[0].Id;
        Assert.False(s.IsFilmCached(id));
        await Assert.ThrowsAsync<HttpRequestException>(() => s.GetTimelineAsync(id));
        // The failed load is dropped by a continuation, so an instant retry can still see it; a
        // person pressing "Try again" can't be that quick. Retry until it asks the API again.
        await WaitUntil(async () =>
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => s.GetTimelineAsync(id));
            return Requests("/spectate") >= 2;
        });
    }

    [Fact]
    public async Task A_film_is_downloaded_once_then_read_from_disk()
    {
        var s = await _h.SignedInAsync();
        var id = HaloWorld.FilmMatchId;
        var progress = new List<(int Done, int Total)>();
        var t = await s.GetTimelineAsync(id, (done, total) => { lock (progress) progress.Add((done, total)); });

        Assert.Equal([NobleSix.Gamertag, Fred.Gamertag, Kelly.Gamertag, Linda.Gamertag], t.Players.Select(p => p.Gamertag));
        Assert.Equal(NobleSix.Xuid, t.Players[0].Xuid);
        Assert.Equal([(1, 2), (2, 2)], progress.Order());
        Assert.True(s.IsFilmCached(id));
        Assert.Equal((1, 2), (Requests("/spectate"), Requests("blobs-")));

        // Another session (or a restart) reads the film from disk instead of the network.
        var again = await _h.SignedInAsync().ContinueWith(x => x.Result.GetTimelineAsync(id)).Unwrap();
        Assert.Equal(t.Feed.Count, again.Feed.Count);
        Assert.Equal((1, 2), (Requests("/spectate"), Requests("blobs-")));
    }

    [Fact]
    public async Task An_unfinished_film_is_not_downloaded()
    {
        var s = await _h.SignedInAsync();
        await Assert.ThrowsAsync<UnfinishedFilmException>(() => s.GetTimelineAsync(HaloWorld.UnfinishedFilmMatchId));
        Assert.Equal(1, Requests("/spectate"));
        Assert.Equal(0, Requests("blobs-"));   // no summary, so no point fetching the rest
        Assert.False(s.IsFilmCached(HaloWorld.UnfinishedFilmMatchId));
    }

    [Fact]
    public void The_film_folder_is_shown_relative_to_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var h = new Harness();
        var s = new HaloSession(h.Halo.Factory, h.Cache, h.Assets, new FilmCache(Path.Combine(home, ".ancilla-films")), h.Config, h.Diagnostics, h.Archive, h.Snapshots);
        Assert.StartsWith("~", s.FilmFolder);
    }

    [Fact]
    public async Task Maps_without_a_navmesh_have_none()
    {
        var s = await _h.SignedInAsync();
        Assert.Null(await s.GetNavMeshAsync(HaloWorld.LiveFire.Ref(2)));
        Assert.Null(await s.GetNavMeshAsync(null));
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10);
        Assert.True(done());
    }

    private static async Task WaitUntil(Func<Task<bool>> done)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await done()) return;
            await Task.Delay(10);
        }
        Assert.Fail("Timed out.");
    }
}
