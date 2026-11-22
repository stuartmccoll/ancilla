using System.Text.Json;
using Ancilla.Core;
using Ancilla.Testing;
using Ancilla.UI.Components.Shared;
using Ancilla.UI.Services;
using Bunit;

namespace Ancilla.UI.Tests.Components;

public sealed class WidgetTests : ComponentTest
{
    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void Stat_groups_hide_all_zero_groups_unless_asked()
    {
        var groups = new Dictionary<string, JsonElement>
        {
            ["PvpStats"] = J(new { Kills = 117, Deaths = 0 }),
            ["OddballStats"] = J(new { SkullGrabs = 0, TimeAsSkullCarrier = "PT0S" }),
            ["NotAGroup"] = J(new { Kills = 3 }),
            ["EmptyStats"] = J(new { }),
        };
        var cut = Render<StatGroups>(p => p.Add(x => x.Groups, groups));
        Assert.Equal(["PvP"], cut.FindAll("h2").Select(h => h.TextContent));
        Assert.Equal(["Kills", "Deaths"], cut.FindAll("dt").Select(d => d.TextContent));
        Assert.Equal("117", cut.Find("dd").TextContent);
        Assert.Single(cut.FindAll("div.zero"));

        var all = Render<StatGroups>(p => p.Add(x => x.Groups, groups).Add(x => x.ShowEmpty, true));
        Assert.Equal(["PvP", "Oddball"], all.FindAll("h2").Select(h => h.TextContent));
        Assert.Contains("Time as skull carrier", all.Markup);
    }

    [Fact]
    public void Csr_cells_show_tier_placement_or_nothing()
    {
        var ranked = Render<CsrCell>(p => p.Add(x => x.Label, "Current").Add(x => x.Csr, HaloWorld.CsrOf(1452)));
        Assert.Equal("Diamond 6", ranked.Find(".tier").TextContent);
        Assert.Contains("tier-diamond", ranked.Find(".tier").ClassName);
        Assert.Equal("1,452 CSR", ranked.Find(".val").TextContent);

        var placing = Render<CsrCell>(p => p.Add(x => x.Label, "Current").Add(x => x.ShowPlacement, true)
            .Add(x => x.Csr, HaloWorld.CsrOf(-1) with { MeasurementMatchesRemaining = 1 }));
        Assert.Equal("1 placement match left", placing.Find(".val").TextContent);

        var none = Render<CsrCell>(p => p.Add(x => x.Label, "Season best").Add(x => x.Csr, HaloWorld.CsrOf(-1)));
        Assert.Equal("—", none.Find(".tier").TextContent);
        Assert.Empty(none.FindAll(".val"));
    }

    [Fact]
    public void Filter_bar_raises_changes_but_not_repeats()
    {
        var picked = new List<StatsFilter>();
        var cut = Render<StatsFilterBar>(p => p
            .Add(x => x.Filter, new StatsFilter())
            .Add(x => x.FilterChanged, f => picked.Add(f)));

        var pvp = cut.FindAll("button").Single(b => b.TextContent == "PvP");
        Assert.Equal("true", pvp.GetAttribute("aria-pressed"));
        pvp.Click();
        Assert.Empty(picked);

        cut.FindAll("button").Single(b => b.TextContent == "Firefight").Click();
        cut.FindAll("button").Single(b => b.TextContent == "Ranked").Click();
        Assert.Equal([new StatsFilter(StatsScope.Firefight), new StatsFilter(StatsScope.PvP, RankedFilter.Ranked)], picked);
        Assert.Contains("Firefight is left out", cut.Find("p.hint").TextContent);
    }

    [Fact]
    public void Filter_bar_can_be_disabled()
    {
        var cut = Render<StatsFilterBar>(p => p.Add(x => x.Filter, new StatsFilter()).Add(x => x.Disabled, true));
        Assert.All(cut.FindAll("button"), b => Assert.True(b.HasAttribute("disabled")));
    }

    [Theory]
    [InlineData(StatsScope.Firefight, RankedFilter.All, "Firefight matches only.")]
    [InlineData(StatsScope.All, RankedFilter.Unranked, "Every matchmade match, including Firefight. Unranked playlists only.")]
    public void Filter_descriptions(StatsScope scope, RankedFilter ranked, string text) =>
        Assert.Equal(text, StatsFilterBar.Describe(new StatsFilter(scope, ranked)));

    private static MedalBook Book()
    {
        var meta = HaloWorld.Medals.Metadata();
        return new MedalBook(meta, meta.Sprites["medium"], "assets/waypoint/sheet.png", meta.Medals.ToDictionary(m => m.NameId));
    }

    [Fact]
    public void Medal_icons_cut_their_medal_from_the_sheet()
    {
        var cut = Render<MedalIcon>(p => p.Add(x => x.Book, Book()).Add(x => x.NameId, HaloWorld.Medals.Killjoy).Add(x => x.Size, 48).Add(x => x.Count, 5));
        var span = cut.Find("span.medal");
        Assert.Equal("img", span.GetAttribute("role"));
        Assert.Equal("Killjoy", span.GetAttribute("aria-label"));
        Assert.Equal("Killjoy: End an enemy's killing spree.", span.GetAttribute("title"));
        var style = span.GetAttribute("style")!;
        Assert.Contains("background-position:-144px -0px", style);   // sprite 3 of 16 per row
        Assert.Contains("background-size:768px", style);
        Assert.Equal("5", cut.Find(".count").TextContent);
    }

    [Fact]
    public void Decorative_medals_are_hidden_from_screen_readers()
    {
        var cut = Render<MedalIcon>(p => p.Add(x => x.Book, Book()).Add(x => x.NameId, HaloWorld.Medals.Perfect).Add(x => x.Decorative, true).Add(x => x.Count, 1));
        var span = cut.Find("span.medal");
        Assert.Equal("true", span.GetAttribute("aria-hidden"));
        Assert.False(span.HasAttribute("role"));
        Assert.Empty(cut.FindAll(".count"));   // a count of one isn't shown
    }

    [Fact]
    public void Unknown_medals_get_a_placeholder()
    {
        var cut = Render<MedalIcon>(p => p.Add(x => x.Book, Book()).Add(x => x.NameId, HaloWorld.Medals.UnknownNameId));
        Assert.Equal("?", cut.Find(".medal.unknown").TextContent);
        Assert.Equal($"Unknown medal {HaloWorld.Medals.UnknownNameId}", cut.Find(".medal.unknown").GetAttribute("title"));
    }
}

public sealed class SignInTests : ComponentTest
{
    [Fact]
    public void The_button_waits_for_a_token()
    {
        SignedOut();
        var cut = Render<SignIn>();
        Assert.True(cut.Find("button[type=submit]").HasAttribute("disabled"));
        Assert.Equal("password", cut.Find("input#token").GetAttribute("type"));
        cut.Find("input#token").Input("v4=something");
        Assert.False(cut.Find("button[type=submit]").HasAttribute("disabled"));
    }

    [Fact]
    public void A_rejected_token_is_explained()
    {
        SignedOut();
        var cut = Render<SignIn>();
        cut.Find("input#token").Input("v4=prophet-of-truth");
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Equal(
            "Halo Waypoint rejected the token. It has probably expired; grab a fresh one.",
            cut.Find("p.error[role=alert]").TextContent), Wait);
        Assert.False(Session.IsSignedIn);
        Assert.DoesNotContain("prophet-of-truth", cut.Markup.Replace("value=\"v4=prophet-of-truth\"", ""));
    }

    [Fact]
    public void A_good_token_signs_in()
    {
        SignedOut();
        var cut = Render<SignIn>();
        cut.Find("input#token").Input(Spartans.Token);
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.True(Session.IsSignedIn), Wait);
        Assert.Equal("Noble Six", Session.Me!.Gamertag);
    }

    [Fact]
    public void Shows_an_error_it_was_given()
    {
        SignedOut();
        var cut = Render<SignIn>(p => p.Add(x => x.InitialError, "HALO_SPARTAN_TOKEN didn't work: nope"));
        Assert.Equal("HALO_SPARTAN_TOKEN didn't work: nope", cut.Find("p.error").TextContent);
    }

    [Fact]
    public void Explain_uses_plain_words_for_401_and_the_message_otherwise()
    {
        Assert.StartsWith("Halo Waypoint rejected the token.",
            SignIn.Explain(new HttpRequestException("x", null, System.Net.HttpStatusCode.Unauthorized)));
        Assert.Equal("500 boom", SignIn.Explain(new HttpRequestException("500 boom", null, System.Net.HttpStatusCode.InternalServerError)));
    }
}
