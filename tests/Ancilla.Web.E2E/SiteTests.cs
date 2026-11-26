using Ancilla.Site;
using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Ancilla.Web.E2E;

/// <summary>
/// The project website (site/), built twice into a temporary folder: once with a made-up release and
/// once with none, as before the first installers. Pages are served from those folders under a made-up
/// origin, and any request for anything else fails the test that made it.
/// </summary>
public sealed class SiteFixture : IAsyncLifetime
{
    public const string Origin = "https://ancilla.test/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ancilla-site-" + Guid.NewGuid().ToString("N"));
    private IPlaywright? _playwright;

    public static readonly Release SampleRelease = Release.Parse("""
        {
          "tag_name": "v1.2.0",
          "html_url": "https://github.com/stuartmccoll/ancilla/releases/tag/v1.2.0",
          "published_at": "2026-11-03T18:00:00Z",
          "assets": [
            { "name": "Ancilla-1.2.0.dmg", "browser_download_url": "https://github.com/stuartmccoll/ancilla/releases/download/v1.2.0/Ancilla-1.2.0.dmg", "size": 48234496 },
            { "name": "Ancilla-1.2.0-win-x64.msix", "browser_download_url": "https://github.com/stuartmccoll/ancilla/releases/download/v1.2.0/Ancilla-1.2.0-win-x64.msix", "size": 61865984 },
            { "name": "Ancilla-1.2.0.msix.sha256", "browser_download_url": "https://github.com/stuartmccoll/ancilla/releases/download/v1.2.0/Ancilla-1.2.0.msix.sha256", "size": 64 },
            { "name": "elsewhere.msix", "browser_download_url": "https://example.com/elsewhere.msix", "size": 1 }
          ]
        }
        """)!;

    public string WithRelease => Path.Combine(_root, "release");
    public string WithoutRelease => Path.Combine(_root, "none");
    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await SiteBuilder.BuildAsync(new SiteOptions(WithRelease, SampleRelease));
        await SiteBuilder.BuildAsync(new SiteOptions(WithoutRelease));
        var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exit != 0) throw new InvalidOperationException($"Playwright couldn't install Chromium (exit code {exit}).");
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync();
    }

    /// <summary>A page showing one build of the site. <paramref name="outside"/> collects requests for anything else.</summary>
    public async Task<IPage> NewPageAsync(string folder, List<string> outside, ColorScheme scheme = ColorScheme.Light)
    {
        var context = await Browser.NewContextAsync(new() { BaseURL = Origin, Locale = "en-GB", ColorScheme = scheme });
        context.SetDefaultTimeout(15_000);
        await context.RouteAsync("**/*", async route =>
        {
            var url = route.Request.Url;
            if (!url.StartsWith(Origin, StringComparison.Ordinal))
            {
                outside.Add(url);
                await route.AbortAsync();
                return;
            }
            var path = Uri.UnescapeDataString(new Uri(url).AbsolutePath.TrimStart('/'));
            if (path.Length == 0) path = "index.html";
            var file = Path.GetFullPath(Path.Combine(folder, path));
            if (!file.StartsWith(Path.GetFullPath(folder), StringComparison.Ordinal) || !File.Exists(file))
            {
                await route.FulfillAsync(new() { Status = 404, Body = "Not found" });
                return;
            }
            await route.FulfillAsync(new() { Status = 200, Path = file });
        });
        return await context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

public sealed class SiteTests(SiteFixture site) : IClassFixture<SiteFixture>
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var scheme in new[] { "light", "dark" })
        foreach (var page in SiteBuilder.Pages)
            data.Add(scheme, page.File);
        return data;
    }

    private static readonly AxeRunOptions Options = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Pages_pass_axe(string scheme, string file)
    {
        var outside = new List<string>();
        var page = await site.NewPageAsync(site.WithRelease, outside, scheme == "dark" ? ColorScheme.Dark : ColorScheme.Light);
        await page.GotoAsync(file);
        var result = await page.RunAxe(Options);
        var problems = result.Violations
            .SelectMany(v => v.Nodes.Select(n => $"{v.Id} ({v.Impact}): {v.Help} at {string.Join(" ", n.Target)}"))
            .ToList();
        Assert.True(problems.Count == 0, $"{scheme} {file}:\n" + string.Join("\n", problems));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Every_page_has_the_disclaimer_and_loads_nothing_from_elsewhere(string file)
    {
        var outside = new List<string>();
        var page = await site.NewPageAsync(site.WithRelease, outside);
        var response = await page.GotoAsync(file);
        Assert.Equal(200, response!.Status);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await Expect(page.Locator("footer")).ToContainTextAsync("Ancilla is an unofficial fan project. Not affiliated with, endorsed or sponsored by Microsoft, Xbox or Halo Studios.");
        await Expect(page.Locator("main h1")).ToHaveCountAsync(1);
        await Expect(page.Locator("nav [aria-current=page]")).ToHaveAttributeAsync("href", file);
        Assert.Empty(outside);
        Assert.Equal(0, await page.Locator("script").CountAsync());

        // Every link on the site goes to another of its pages, or somewhere over HTTPS.
        foreach (var href in await page.Locator("a[href]").EvaluateAllAsync<string[]>("links => links.map(a => a.getAttribute('href'))"))
        {
            if (href.StartsWith('#') || href.StartsWith("https://", StringComparison.Ordinal)) continue;
            var target = href.Split('#')[0];
            Assert.True(SiteBuilder.Pages.Any(p => p.File == target), $"{file} links to {href}, which isn't a page of the site.");
        }
    }

    public static TheoryData<string> Files() => new(SiteBuilder.Pages.Select(p => p.File));

    [Fact]
    public async Task Download_links_point_at_the_release()
    {
        var page = await site.NewPageAsync(site.WithRelease, []);
        await page.GotoAsync("download.html");
        await Expect(page.Locator("main")).ToContainTextAsync("The latest version is v1.2.0, released 3 November 2026.");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Download for Windows" }))
            .ToHaveAttributeAsync("href", "https://github.com/stuartmccoll/ancilla/releases/download/v1.2.0/Ancilla-1.2.0-win-x64.msix");
        await Expect(page.Locator("main")).ToContainTextAsync("Ancilla-1.2.0-win-x64.msix, 59 MB");
        await Expect(page.Locator(".soon")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Without_a_release_the_downloads_are_coming_soon()
    {
        var page = await site.NewPageAsync(site.WithoutRelease, []);
        await page.GotoAsync("download.html");
        await Expect(page.Locator(".soon")).ToHaveTextAsync("Version 1.0 is coming soon");
        await Expect(page.GetByRole(AriaRole.Link, new() { NameRegex = new("^Download for") })).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "GitHub Releases" })).ToHaveAttributeAsync("href", SiteModel.Releases);
    }

    [Fact]
    public async Task The_header_stays_at_the_top_and_the_footer_at_the_bottom()
    {
        // A short page: the footer still ends at the bottom of the window.
        var page = await site.NewPageAsync(site.WithoutRelease, []);
        await page.SetViewportSizeAsync(1280, 1400);
        await page.GotoAsync("disclaimer.html");
        var footer = (await page.Locator("footer").BoundingBoxAsync())!;
        Assert.Equal(1400, footer.Y + footer.Height, 1);

        // A long page, scrolled to the end: the header is still at the top.
        await page.SetViewportSizeAsync(1280, 600);
        await page.GotoAsync("getting-started.html");
        await page.Mouse.WheelAsync(0, 5000);
        await Expect(page.Locator("footer")).ToBeInViewportAsync();
        var header = (await page.Locator("header").BoundingBoxAsync())!;
        Assert.Equal(0, header.Y, 1);
    }

    [Fact]
    public void Only_GitHub_download_links_are_kept()
    {
        Assert.DoesNotContain(SiteFixture.SampleRelease.Assets, a => a.Name == "elsewhere.msix");
        Assert.Null(Release.Parse("""{ "tag_name": "", "html_url": "", "assets": [] }"""));
    }
}
