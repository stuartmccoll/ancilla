using Ancilla.Testing;
using static Microsoft.Playwright.Assertions;

namespace Ancilla.Web.E2E;

/// <summary>The token stays on the server; token-gated images are only served once fetched.</summary>
[Collection(AppCollection.Name)]
public sealed class SecurityTests(AppFixture app)
{
    private static readonly string TokenBody = Spartans.Token["v4=".Length..];

    [Fact]
    public async Task The_token_never_reaches_the_browser()
    {
        var page = await app.NewPageAsync();
        var seen = new List<string>();
        page.Request += (_, r) => seen.Add(r.Url + " " + string.Join(' ', r.Headers.Values));
        page.WebSocket += (_, ws) => ws.FrameReceived += (_, f) => seen.Add(f.Text ?? "");

        await page.OpenAsync("");
        await page.GetByRole(Microsoft.Playwright.AriaRole.Link, new() { Name = "Progress" }).First.ClickAsync();
        await Expect(page.Locator("main h1")).ToHaveTextAsync("Progress");
        await page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

        Assert.DoesNotContain(TokenBody, await page.ContentAsync());
        Assert.DoesNotContain(await page.Context.CookiesAsync(), c => c.Value.Contains(TokenBody));
        var storage = await page.EvaluateAsync<string>("() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage })");
        Assert.DoesNotContain(TokenBody, storage);
        Assert.DoesNotContain(seen, s => s.Contains(TokenBody));
    }

    [Fact]
    public async Task Pasted_tokens_are_not_echoed_back()
    {
        var page = await app.NewPageAsync();
        await page.OpenAsync("");
        await page.Locator(".user-btn").ClickAsync();
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await page.GetByLabel("Spartan token").FillAsync("v4=high-charity-secret");
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Expect(page.GetByRole(Microsoft.Playwright.AriaRole.Alert)).ToBeVisibleAsync();
        Assert.Equal("password", await page.GetByLabel("Spartan token").GetAttributeAsync("type"));
        var html = await page.EvaluateAsync<string>("() => document.body.innerText");
        Assert.DoesNotContain("high-charity-secret", html);
    }

    [Fact]
    public async Task Assets_are_served_only_after_a_session_fetched_them()
    {
        using var http = new HttpClient { BaseAddress = app.BaseUri };
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await http.GetAsync("assets/waypoint/medals/images/not-fetched.png")).StatusCode);

        var page = await app.NewPageAsync();
        await page.OpenAsync("stats/medals");
        await page.Locator(".medal-card").First.WaitForAsync();
        var res = await http.GetAsync("assets/waypoint/" + HaloWorld.Medals.SheetPath);
        Assert.True(res.IsSuccessStatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("immutable", res.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Only_the_fake_was_called()
    {
        var page = await app.NewPageAsync();
        await page.OpenAsync("");
        Assert.NotEmpty(app.Halo.Requests);
        Assert.All(app.Halo.Requests, r => Assert.EndsWith(".svc.halowaypoint.com", r.Uri.Host));
    }
}
