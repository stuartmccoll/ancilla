using Deque.AxeCore.Commons;
using Deque.AxeCore.Playwright;
using Ancilla.Testing;
using Ancilla.Web.Services;
using static Microsoft.Playwright.Assertions;

namespace Ancilla.Web.E2E;

/// <summary>
/// axe-core's WCAG 2.1 A and AA rules, colour contrast included, on the main pages in every theme.
/// </summary>
[Collection(AppCollection.Name)]
public sealed class AccessibilityTests(AppFixture app)
{
    public static readonly string[] Pages =
    [
        "", "progress", "stats", "stats/medals", "matches", "matches/trends", "matches/insights", "matches/players",
        $"matches/{HaloWorld.Ids.Match(0)}", $"matches/{HaloWorld.Ids.Match(1)}/timeline", $"matches/players/{Spartans.Carter.Xuid}", "about",
    ];

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var theme in Themes.All)
        foreach (var page in Pages)
            data.Add(theme.Id, page);
        return data;
    }

    private static readonly AxeRunOptions Options = new()
    {
        RunOnly = new RunOnlyOptions { Type = "tag", Values = ["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Pages_pass_axe(string theme, string path)
    {
        var page = await app.NewPageAsync();
        await page.Context.AddCookiesAsync([new() { Name = Themes.CookieName, Value = theme, Url = app.BaseUri.ToString() }]);
        await page.OpenAsync(path);
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", theme);
        await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);
        await Expect(page.Locator(".skeleton")).ToHaveCountAsync(0);   // let the page finish filling in

        var result = await page.RunAxe(Options);
        var problems = result.Violations
            .SelectMany(v => v.Nodes.Select(n => $"{v.Id} ({v.Impact}): {v.Help} at {string.Join(" ", n.Target)}"))
            .ToList();
        Assert.True(problems.Count == 0, $"{theme} /{path}:\n" + string.Join("\n", problems));
    }

    [Fact]
    public async Task The_sign_in_form_passes_axe()
    {
        var page = await app.NewPageAsync();
        await page.OpenAsync("");
        await page.Locator(".user-btn").ClickAsync();
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await Expect(page.Locator("main h1")).ToHaveTextAsync("Sign in");
        var result = await page.RunAxe(Options);
        Assert.Empty(result.Violations.Select(v => v.Id));
    }
}
