using Ancilla.Site;
using Ancilla.Testing;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Ancilla.Web.E2E;

/// <summary>Runs only when <c>ANCILLA_SITE_SCREENSHOTS</c> names a folder: the site workflow sets it.</summary>
public sealed class SiteScreenshotsFactAttribute : FactAttribute
{
    public const string Variable = "ANCILLA_SITE_SCREENSHOTS";
    public static string? Folder => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } f ? f : null;

    public SiteScreenshotsFactAttribute()
    {
        if (Folder is null) Skip = $"Set {Variable} to a folder to take the website's screenshots.";
    }
}

/// <summary>
/// The website's screenshots (<see cref="SiteBuilder.Screenshots"/>), taken from the app signed in to the
/// fake backend, so they only ever show made-up players.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class SiteScreenshots(AppFixture app)
{
    private static readonly Dictionary<string, string> Routes = new()
    {
        ["progress.png"] = "progress",
        ["stats.png"] = "stats",
        // A match whose made-up score agrees with its result.
        ["match.png"] = $"matches/{HaloWorld.Ids.Match(5)}",
        ["timeline.png"] = $"matches/{HaloWorld.FilmMatchId}/timeline",
        ["trends.png"] = "matches/trends",
    };

    [Fact]
    public void Every_screenshot_has_a_page() =>
        Assert.Equal(SiteBuilder.Screenshots.Select(s => s.File).Order(), Routes.Keys.Order());

    [SiteScreenshotsFact]
    public async Task Take_the_website_screenshots()
    {
        var folder = SiteScreenshotsFactAttribute.Folder!;
        Directory.CreateDirectory(folder);
        foreach (var shot in SiteBuilder.Screenshots)
        {
            var page = await app.NewPageAsync();
            await page.SetViewportSizeAsync(1280, 800);
            await page.OpenAsync(Routes[shot.File]);
            if (shot.File == "timeline.png")
            {
                await page.GetByRole(AriaRole.Button, new() { Name = "Load the timeline" }).ClickAsync();
                await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Team kills by minute" })).ToBeVisibleAsync();
            }
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await Expect(page.Locator(".skeleton")).ToHaveCountAsync(0);
            await page.AssertNoBlazorErrorAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(folder, shot.File), Animations = ScreenshotAnimations.Disabled });
            await page.Context.CloseAsync();
        }
    }
}
