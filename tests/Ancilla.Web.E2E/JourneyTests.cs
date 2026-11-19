using Ancilla.Testing;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Ancilla.Web.E2E;

/// <summary>Things people do, end to end: sign in, move around, change theme, read a table.</summary>
[Collection(AppCollection.Name)]
public sealed class JourneyTests(AppFixture app)
{
    [Fact]
    public async Task Sign_out_then_sign_in_with_a_pasted_token()
    {
        var page = await app.NewPageAsync();
        await page.OpenAsync("");
        await page.Locator(".user-btn").ClickAsync();
        await Expect(page.Locator(".menu-note")).ToContainTextAsync("HALO_SPARTAN_TOKEN");
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();

        await Expect(page.Locator("main h1")).ToHaveTextAsync("Sign in");
        await Expect(page).ToHaveTitleAsync("Sign in · Ancilla");
        var submit = page.GetByRole(AriaRole.Button, new() { Name = "Sign in" });
        await Expect(submit).ToBeDisabledAsync();

        await page.GetByLabel("Spartan token").FillAsync("v4=343-guilty-spark");
        await submit.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("Halo Waypoint rejected the token. It has probably expired; grab a fresh one.");

        await page.GetByLabel("Spartan token").FillAsync(Spartans.Token);
        await submit.ClickAsync();
        await Expect(page.Locator(".user-btn .gt")).ToHaveTextAsync("Noble Six");
    }

    [Fact]
    public async Task From_the_match_list_to_a_scoreboard_to_a_head_to_head()
    {
        var page = await app.NewPageAsync();
        await page.OpenAsync("");
        await page.Locator("nav.sections").GetByRole(AriaRole.Link, new() { Name = "Matches" }).ClickAsync();
        await Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/matches$"));
        await Expect(page.Locator("a.match")).ToHaveCountAsync(HaloWorld.MatchmadeCount);

        await page.Locator("a.match").First.ClickAsync();
        await Expect(page.Locator("main h1")).ToHaveTextAsync("Slayer");
        await Expect(page.Locator(".team-name")).ToHaveTextAsync(["Eagle", "Cobra"]);

        await page.GetByRole(AriaRole.Link, new() { Name = "Kat B320" }).ClickAsync();
        await Expect(page.Locator(".vs-head")).ToContainTextAsync("Kat B320");
        await Expect(page.Locator("nav.tabs a[aria-current=page]")).ToHaveTextAsync("Players");

        await page.GoBackAsync();
        await Expect(page.Locator("main h1")).ToHaveTextAsync("Slayer");
    }

    [Fact]
    public async Task The_chosen_theme_survives_a_reload()
    {
        var page = await app.NewPageAsync();
        await page.OpenAsync("");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "basic");
        await page.Locator(".user-btn").ClickAsync();
        await page.GetByRole(AriaRole.Radio, new() { Name = "Halo" }).ClickAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "halo");
        await Expect(page.GetByRole(AriaRole.Radio, new() { Name = "Halo" })).ToHaveAttributeAsync("aria-checked", "true");

        await page.ReloadAsync();
        await page.Locator("main h1").WaitForAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "halo");   // rendered by the server from the cookie
    }

    [Fact]
    public async Task A_forged_theme_cookie_falls_back_to_the_default()
    {
        var page = await app.NewPageAsync();
        await page.Context.AddCookiesAsync([new Cookie { Name = "theme", Value = "\"><script>alert(1)</script>", Url = app.BaseUri.ToString() }]);
        await page.OpenAsync("about");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "basic");
    }

    [Fact]
    public async Task Charts_open_as_tables()
    {
        var page = await app.NewPageAsync();
        await page.OpenAsync("matches/trends");
        var details = page.Locator("details.table-view").First;
        await Expect(details.Locator("table")).ToBeHiddenAsync();
        await details.Locator("summary", new() { HasText = "Show as table" }).ClickAsync();
        await Expect(details.Locator("table")).ToBeVisibleAsync();
        Assert.True(await details.Locator("tbody tr").CountAsync() > 0);
    }

    [Fact]
    public async Task Stats_filters_change_the_figures()
    {
        var page = await app.NewPageAsync();
        await page.OpenAsync("stats");
        var matches = page.Locator(".tile", new() { Has = page.Locator(".label", new() { HasTextString = "Matches" }) }).Locator(".value").First;
        var pvp = app.Halo.World.Matchmade().Count(g => !g.IsPve);
        var ff = app.Halo.World.Matchmade().Count(g => g.IsPve);
        await Expect(matches).ToHaveTextAsync(pvp.ToString());
        await page.GetByRole(AriaRole.Button, new() { Name = "Firefight", Exact = true }).ClickAsync();
        await Expect(matches).ToHaveTextAsync(ff.ToString());
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Firefight", Exact = true })).ToHaveAttributeAsync("aria-pressed", "true");
    }
}
