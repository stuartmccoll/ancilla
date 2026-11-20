using Ancilla.Testing;
using static Microsoft.Playwright.Assertions;

namespace Ancilla.Web.E2E;

/// <summary>Every route loads in a real browser without Blazor's unhandled-error bar.</summary>
[Collection(AppCollection.Name)]
public sealed class SmokeTests(AppFixture app)
{
    public static TheoryData<string, string> Routes => new()
    {
        { "", "Noble Six" },
        { "progress", "Progress" },
        { "stats", "Service record" },
        { "stats/medals", "Medals" },
        { "matches", "Matches" },
        { "matches/trends", "Trends" },
        { "matches/insights", "Insights" },
        { "matches/players", "Players" },
        { $"matches/{HaloWorld.Ids.Match(0)}", "Slayer" },
        { $"matches/{HaloWorld.Ids.Match(1)}/timeline", "Match timeline" },
        { $"matches/players/{Spartans.Carter.Xuid}", "" },
        { "about", "About" },
        { "diagnostics", "Diagnostics" },
        // Old flat URLs still work.
        { "medals", "Medals" },
        { "trends", "Trends" },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Every_page_loads(string path, string heading)
    {
        var page = await app.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error" && !m.Text.Contains("net::ERR_FAILED")) errors.Add(m.Text); };
        page.PageError += (_, e) => errors.Add(e);

        await page.GotoAsync(path);
        if (heading.Length > 0) await Expect(page.Locator("main h1")).ToContainTextAsync(heading);
        else await Expect(page.Locator(".vs-head")).ToContainTextAsync(Spartans.Carter.Gamertag);
        await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
        await page.AssertNoBlazorErrorAsync();
        Assert.Empty(errors);
        // One heading to land on (the router focuses it) and a title for the tab.
        await Expect(page.Locator("main h1")).ToHaveCountAsync(1);
        await Expect(page).ToHaveTitleAsync(new System.Text.RegularExpressions.Regex("Ancilla$"));
    }

    [Theory]
    [InlineData("matches/not-a-match", "That isn't a valid match link.")]
    [InlineData("matches/players/the-arbiter", "That isn't a valid player link.")]
    [InlineData("matches/not-a-match/timeline", "That isn't a valid match link.")]
    public async Task Bad_links_get_a_friendly_message(string path, string message)
    {
        var page = await app.NewPageAsync();
        await page.GotoAsync(path);
        await Expect(page.Locator("p.error")).ToHaveTextAsync(message);
        await page.AssertNoBlazorErrorAsync();
    }

    [Fact]
    public async Task Unknown_pages_are_not_found()
    {
        var page = await app.NewPageAsync();
        var response = await page.GotoAsync("no-such-ring");
        Assert.Equal(404, response!.Status);
        await Expect(page.Locator("h3")).ToHaveTextAsync("Not Found");
    }
}
