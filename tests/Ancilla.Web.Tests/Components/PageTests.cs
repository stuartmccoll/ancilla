using Ancilla.Core;
using Ancilla.Testing;
using Ancilla.Web.Components.Pages;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using static Ancilla.Testing.Spartans;

namespace Ancilla.Web.Tests.Components;

public sealed class HomePageTests : ComponentTest
{
    [Fact]
    public async Task Shows_the_player_and_each_overview()
    {
        await SignInAsync();
        var cut = Render<Home>();
        cut.WaitForAssertion(() => Assert.Contains("matchmade games", cut.Find("header.profile").TextContent), Wait);
        Assert.StartsWith("Noble Six", cut.Find("h1").TextContent.Trim());
        Assert.Equal("B312", cut.Find("h1 .stag").TextContent);
        Assert.Equal(["Progress", "Stats", "Matches", "Players"], cut.FindAll("h2").Select(h => h.TextContent).Take(4));
        cut.WaitForAssertion(() => Assert.NotNull(Session.Report), Wait);
        Assert.Single(await H.Snapshots.LoadAsync());   // opening the home page records a snapshot
    }
}

public sealed class ProgressPageTests : ComponentTest
{
    [Fact]
    public async Task Shows_career_rank_and_owned_unfinished_passes()
    {
        await SignInAsync();
        var cut = Render<Progress>();
        cut.WaitForAssertion(() => Assert.Contains("Career rank", cut.Markup), Wait);
        Assert.Equal("Corporal Bronze 1", cut.Find(".rank-title").TextContent);
        Assert.Equal($"12 / {HaloWorld.CareerRanks} ranks done", cut.Find(".ranks-done").TextContent);
        Assert.Contains("Operation: Fall of Reach", cut.Markup);
        Assert.DoesNotContain("Operation: First Strike", cut.Markup);   // free track only
        Assert.DoesNotContain("Operation: Sacred Icon", cut.Markup);    // finished
    }

    [Fact]
    public async Task Toggles_show_unowned_and_completed_passes()
    {
        await SignInAsync();
        var cut = Render<Progress>();
        cut.WaitForAssertion(() => Assert.Contains("Battle passes", cut.Markup), Wait);
        Click(cut, "button.chip", "Free-track-only passes");
        Click(cut, "button.chip", "Completed passes");
        Assert.Contains("Operation: First Strike", cut.Markup);
        Assert.Contains("Operation: Sacred Icon", cut.Markup);
        Assert.Equal(2, cut.FindAll("button.chip[aria-pressed=true]").Count(b => b.TextContent.Contains("passes")));
    }

    [Fact]
    public async Task A_failure_is_shown_not_thrown()
    {
        H.Halo.Fail("careerrank1", System.Net.HttpStatusCode.ServiceUnavailable);
        await SignInAsync();
        var cut = Render<Progress>();
        cut.WaitForAssertion(() => Assert.StartsWith("503", cut.Find("p.error[role=alert]").TextContent), Wait);
    }
}

public sealed class StatsPageTests : ComponentTest
{
    private static string Tile(IRenderedComponent<Stats> cut, string label) =>
        cut.FindAll(".tile").First(t => t.QuerySelector(".label")!.TextContent == label).QuerySelector(".value")!.TextContent;

    [Fact]
    public async Task Pvp_by_default_with_ranked_csr()
    {
        await SignInAsync();
        var cut = Render<Stats>();
        var pvp = H.World.Matchmade().Count(g => !g.IsPve);
        cut.WaitForAssertion(() => Assert.Equal(pvp.ToString(), Tile(cut, "Matches")), Wait);
        cut.WaitForAssertion(() => Assert.Contains("Diamond 6", cut.Markup), Wait);
        Assert.Contains("Ranked Arena", cut.Markup);
    }

    [Fact]
    public async Task Changing_the_filter_reloads_and_is_remembered()
    {
        await SignInAsync();
        var cut = Render<Stats>();
        cut.WaitForAssertion(() => Assert.Contains("Diamond 6", cut.Markup), Wait);   // everything has loaded
        Click(cut, ".filter-bar button", "Firefight");
        var ff = H.World.Matchmade().Count(g => g.IsPve);
        cut.WaitForAssertion(() => Assert.Equal(ff.ToString(), Tile(cut, "Matches")), Wait);
        Assert.Equal(StatsScope.Firefight, Session.StatsFilter.Scope);
        Assert.DoesNotContain("<h2>Ranked</h2>", cut.Markup);   // no CSR for Firefight
    }

    [Fact]
    public async Task An_empty_filter_says_so()
    {
        await SignInAsync();
        Session.StatsFilter = new StatsFilter(StatsScope.Firefight, RankedFilter.Ranked);
        var cut = Render<Stats>();
        cut.WaitForAssertion(() => Assert.Contains("No matches for this filter yet.", cut.Markup), Wait);
    }
}

public sealed class MedalsPageTests : ComponentTest
{
    private static IEnumerable<string> Names(IRenderedComponent<Medals> cut) =>
        cut.FindAll(".medal-card").Select(c => c.TextContent);

    [Fact]
    public async Task Shows_earned_medals_by_default_and_counts_unknown_ones()
    {
        await SignInAsync();
        var cut = Render<Medals>();
        // Noble Six has earned Double Kill, Killing Spree and Perfect, plus one medal the metadata doesn't list.
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".medal-card").Count), Wait);
        Assert.DoesNotContain(Names(cut), n => n.Contains("Killjoy"));
        var hint = string.Join(' ', cut.Find(".results p.hint").TextContent.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("Plus 1 medal of 1 kind that Ancilla can't name yet", hint);

        Click(cut, "button.chip", "Not earned yet");
        Assert.Equal(2, cut.FindAll(".medal-card.unearned").Count);
        Assert.Empty(cut.FindAll(".results p.hint"));

        Click(cut, "button.chip", "All");
        Assert.Equal(5, cut.FindAll(".medal-card").Count);
    }

    [Fact]
    public async Task Searches_names_and_descriptions()
    {
        await SignInAsync();
        var cut = Render<Medals>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll(".medal-card").Count), Wait);
        Click(cut, "button.chip", "All");
        OnRenderer(cut, () => cut.Find("input.search").Input("spree"));
        Assert.Equal(2, cut.FindAll(".medal-card").Count);   // Killing Spree, and Killjoy's description
        OnRenderer(cut, () => cut.Find("input.search").Input("Scarab"));
        Assert.Equal("No medals match \"Scarab\".", cut.Find(".results p.muted").TextContent);
    }

    [Fact]
    public async Task Firefight_medals_are_counted_separately()
    {
        await SignInAsync();
        Session.MedalsFilter = new StatsFilter(StatsScope.Firefight);
        var cut = Render<Medals>();
        cut.WaitForAssertion(() => Assert.Contains(Names(cut), n => n.Contains("Killing Spree")), Wait);
        Assert.Equal(1, Requests("gameVariantCategory=42"));
    }
}

public sealed class MatchesPageTests : ComponentTest
{
    [Fact]
    public async Task Lists_matchmade_games_then_switches_to_custom()
    {
        await SignInAsync();
        var cut = Render<Matches>();
        cut.WaitForAssertion(() => Assert.Equal(HaloWorld.MatchmadeCount, cut.FindAll("a.match").Count), Wait);
        cut.WaitForAssertion(() => Assert.Equal("Slayer", cut.FindAll("a.match .mode")[0].TextContent), Wait);
        Assert.Equal($"matches/{HaloWorld.Ids.Match(0)}", cut.FindAll("a.match")[0].GetAttribute("href"));
        Assert.Empty(cut.FindAll("button.secondary"));   // nothing more to load

        Click(cut, ".chips button", "Custom");
        cut.WaitForAssertion(() => Assert.Equal(HaloWorld.CustomCount, cut.FindAll("a.match").Count), Wait);
    }

    [Fact]
    public async Task An_empty_history_says_so()
    {
        await SignInAsync();
        Session.SetMatchType(MatchHistoryType.Local);
        var cut = Render<Matches>();
        cut.WaitForAssertion(() => Assert.Contains("No matches found.", cut.Markup), Wait);
    }
}

public sealed class MatchDetailPageTests : ComponentTest
{
    [Theory]
    [InlineData("not-a-match")]
    [InlineData("000ba771-0000-4000-8000-000000000001%2F..%2Fusers")]
    public async Task Bad_links_get_a_friendly_message_and_no_api_call(string id)
    {
        await SignInAsync();
        var before = H.Halo.Requests.Count;
        var cut = Render<MatchDetail>(p => p.Add(x => x.MatchId, id));
        Assert.Equal("That isn't a valid match link.", cut.Find("p.error").TextContent);
        Assert.Equal(before, H.Halo.Requests.Count);
    }

    [Fact]
    public async Task A_ranked_team_game_shows_the_scoreboard_and_csr()
    {
        await SignInAsync();
        var game = H.World.Games[0];   // Ranked Slayer on Live Fire
        var cut = Render<MatchDetail>(p => p.Add(x => x.MatchId, game.Id));
        cut.WaitForAssertion(() => Assert.Equal("Slayer", cut.Find("h1").TextContent), Wait);
        cut.WaitForAssertion(() => Assert.Contains("Against expectations", cut.Markup), Wait);
        Assert.Contains("Live Fire", cut.Find("header.hero").TextContent);
        Assert.Contains("Ranked Arena", cut.Find("header.hero").TextContent);
        Assert.Equal(["Eagle", "Cobra"], cut.FindAll(".team-name").Select(t => t.TextContent));
        Assert.Contains("CSR", cut.Find(".perf").TextContent);

        // Other players link to head-to-head; your own row is highlighted, not linked.
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("a.name"), a => a.TextContent == "Kat B320"), Wait);
        Assert.Equal($"matches/players/{Kat.Xuid}", cut.FindAll("a.name").Single(a => a.TextContent == "Kat B320").GetAttribute("href"));
        var mine = cut.Find("tr.me");
        Assert.Equal("Noble Six", mine.QuerySelector(".name")!.TextContent);
        Assert.Null(mine.QuerySelector("a"));
        Assert.Equal($"matches/{game.Id}/timeline", cut.Find(".timeline-link a").GetAttribute("href"));
    }

    [Fact]
    public async Task Free_for_all_lists_one_group_with_the_bot()
    {
        await SignInAsync();
        var game = H.World.Games.First(g => !g.Stats.MatchInfo.TeamsEnabled);
        var cut = Render<MatchDetail>(p => p.Add(x => x.MatchId, game.Id));
        cut.WaitForAssertion(() => Assert.Equal(["Players"], cut.FindAll(".team-name").Select(t => t.TextContent)), Wait);
        Assert.Contains(cut.FindAll("span.name"), s => s.TextContent == "Bot");
    }

    [Fact]
    public async Task A_custom_game_has_no_skill_section()
    {
        await SignInAsync();
        var game = H.World.Games.First(g => g.Type == MatchHistoryType.Custom);
        var cut = Render<MatchDetail>(p => p.Add(x => x.MatchId, game.Id));
        cut.WaitForAssertion(() => Assert.Contains("Scoreboard", cut.Markup), Wait);
        cut.WaitForAssertion(() => Assert.Contains("Your medals", cut.Markup), Wait);
        Assert.DoesNotContain("Against expectations", cut.Markup);
    }

    [Fact]
    public async Task A_match_that_does_not_exist_shows_the_error()
    {
        await SignInAsync();
        var cut = Render<MatchDetail>(p => p.Add(x => x.MatchId, HaloWorld.Ids.Match(999)));
        cut.WaitForAssertion(() => Assert.StartsWith("404", cut.Find("p.error[role=alert]").TextContent), Wait);
    }
}

public sealed class TimelinePageTests : ComponentTest
{
    [Fact]
    public async Task Bad_links_get_a_friendly_message()
    {
        await SignInAsync();
        var cut = Render<MatchTimelinePage>(p => p.Add(x => x.MatchId, "halo-3"));
        Assert.Equal("That isn't a valid match link.", cut.Find("p.error").TextContent);
    }

    [Fact]
    public async Task An_uncached_film_waits_to_be_asked_for()
    {
        await SignInAsync();
        var game = H.World.Games[1];
        var cut = Render<MatchTimelinePage>(p => p.Add(x => x.MatchId, game.Id));
        cut.WaitForAssertion(() => Assert.Contains("Oddball · Streets", cut.Markup), Wait);
        Assert.Equal("Load the timeline", cut.Find("section.intro button").TextContent);
        Assert.Equal(0, Requests("/spectate"));
    }

    [Fact]
    public async Task A_missing_film_can_be_retried()
    {
        await SignInAsync();
        var cut = Render<MatchTimelinePage>(p => p.Add(x => x.MatchId, H.World.Games[1].Id));
        cut.WaitForAssertion(() => cut.Find("section.intro button"), Wait);
        Click(cut, "section.intro button", "Load the timeline");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("p.error[role=alert]")), Wait);
        Assert.Equal(1, Requests("/spectate"));
    }
}

public sealed class TrendsAndInsightsTests : ComponentTest
{
    [Fact]
    public async Task Trends_leave_firefight_out()
    {
        await SignInAsync();
        var cut = Render<Trends>();
        var pvp = H.World.Matchmade().Count(g => !g.IsPve);
        var ff = H.World.Matchmade().Count(g => g.IsPve);
        cut.WaitForAssertion(() => Assert.Contains("K/D by match", cut.Markup), Wait);
        var tile = cut.FindAll(".tile").First(t => t.TextContent.Contains("PvP games"));
        Assert.Equal(pvp.ToString(), tile.QuerySelector(".value")!.TextContent);
        Assert.Equal($"{ff} Firefight left out", tile.QuerySelector(".note")!.TextContent);
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading skill data…", cut.Markup), Wait);
        Assert.Equal(cut.FindAll("details.table-view").Count, cut.FindAll("details.table-view > summary").Count(s => s.TextContent == "Show as table"));
        Assert.True(cut.FindAll("details.table-view").Count >= 3);
    }

    [Fact]
    public async Task Insights_leave_firefight_out()
    {
        await SignInAsync();
        var cut = Render<Insights>();
        cut.WaitForAssertion(() => Assert.Contains("Where you do best", cut.Markup), Wait);
        cut.WaitForAssertion(() => Assert.DoesNotContain("Loading skill data…", cut.Markup), Wait);
        Assert.DoesNotContain("Behemoth", cut.Markup);                       // the Firefight map
        Assert.DoesNotContain("Firefight: King of the Hill", cut.Markup);
        Assert.Contains("Live Fire", cut.Markup);
    }
}

public sealed class PlayerPagesTests : ComponentTest
{
    [Fact]
    public async Task Players_lists_people_seen_more_than_once()
    {
        await SignInAsync();
        var cut = Render<Players>();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("a.who span"), s => s.TextContent == "Kat B320"), Wait);
        var names = cut.FindAll("a.who span").Select(s => s.TextContent).ToList();
        Assert.DoesNotContain("Noble Six", names);
        Assert.Contains("Thel Vadam", names);
        Assert.Equal($"matches/players/{Kat.Xuid}?range=100", cut.FindAll("a.who").First(a => a.TextContent.Contains("Kat B320")).GetAttribute("href"));
    }

    [Theory]
    [InlineData("arbiter")]
    [InlineData("-1")]
    [InlineData("1e9")]
    public async Task Head_to_head_rejects_bad_player_links(string xuid)
    {
        await SignInAsync();
        var cut = Render<HeadToHead>(p => p.Add(x => x.Xuid, xuid));
        cut.WaitForAssertion(() => Assert.Equal("That isn't a valid player link.", cut.Find("p.error").TextContent), Wait);
    }

    [Fact]
    public async Task Head_to_head_with_a_sometimes_teammate()
    {
        await SignInAsync();
        var cut = Render<HeadToHead>(p => p.Add(x => x.Xuid, Carter.Xuid));
        cut.WaitForAssertion(() => Assert.Contains("Carter A259", cut.Find(".vs-head").TextContent), Wait);
        var tiles = cut.FindAll(".tile");
        Assert.Contains("win rate together", tiles[0].TextContent);
        Assert.Contains("your record against them", tiles[1].TextContent);
        Assert.Contains("Shared matches", cut.Markup);
    }

    [Fact]
    public async Task Head_to_head_with_a_stranger()
    {
        await SignInAsync();
        var cut = Render<HeadToHead>(p => p.Add(x => x.Xuid, Spartans.Xuid(117)));
        cut.WaitForAssertion(() => Assert.Contains("You haven't shared a game with this player in your last 100 matches.", cut.Markup), Wait);
    }

    [Fact]
    public async Task Head_to_head_range_comes_from_the_address()
    {
        await SignInAsync();
        Services.GetRequiredService<NavigationManager>().NavigateTo($"matches/players/{Carter.Xuid}?range=10");
        var cut = Render<HeadToHead>(p => p.Add(x => x.Xuid, Carter.Xuid));
        cut.WaitForAssertion(() => Assert.Contains("in your last 10 matches", cut.Markup), Wait);
    }
}

public sealed class StaticPageTests : ComponentTest
{
    [Fact]
    public void About_keeps_the_disclaimer_and_hides_the_home_folder()
    {
        SignedOut();
        var cut = Render<About>();
        Assert.Contains("Disclaimer", cut.FindAll("h2").Select(h => h.TextContent));
        Assert.Contains("unofficial", cut.Markup);
        Assert.Equal(H.Snapshots.DisplayPath, cut.Find("code.path").TextContent);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (H.Snapshots.FilePath.StartsWith(home)) Assert.DoesNotContain(home, cut.Markup);
    }

    [Fact]
    public void Not_found_says_so()
    {
        var cut = Render<NotFound>();
        Assert.Equal("Not Found", cut.Find("h3").TextContent);
    }
}
