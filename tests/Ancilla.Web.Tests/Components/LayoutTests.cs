using Ancilla.Testing;
using Ancilla.Web.Components.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Ancilla.Web.Tests.Components;

public sealed class LayoutTests : ComponentTest
{
    private IRenderedComponent<MainLayout> RenderLayout() =>
        Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<p id=\"body\">Spartans never die.</p>"))));

    [Fact]
    public void Signed_out_shows_sign_in_and_no_sections()
    {
        SignedOut();
        var cut = RenderLayout();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("section.signin")), Wait);
        Assert.Empty(cut.FindAll("nav.sections"));
        Assert.Empty(cut.FindAll("#body"));
    }

    [Fact]
    public void The_disclaimer_is_always_in_the_footer()
    {
        SignedOut();
        var cut = RenderLayout();
        Assert.Contains("unofficial fan project", cut.Find("footer").TextContent);
        Assert.Contains("Not affiliated with, endorsed or sponsored by", cut.Find("footer").TextContent);
    }

    [Fact]
    public async Task Signed_in_shows_sections_the_player_and_the_page()
    {
        await SignInAsync();
        var cut = RenderLayout();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#body")), Wait);
        Assert.Equal(["Home", "Progress", "Stats", "Matches", "About"], cut.FindAll("nav.sections a").Select(a => a.TextContent));
        Assert.Equal("Noble Six", cut.Find(".user-btn .gt").TextContent);
        Assert.Equal("page", cut.FindAll("nav.sections a")[0].GetAttribute("aria-current"));
    }

    [Theory]
    [InlineData("matches/trends", "Matches", "Trends")]
    [InlineData("trends", "Matches", "Trends")]
    [InlineData("matches/000ba771-0000-4000-8000-000000000001", "Matches", "History")]
    [InlineData("players/123", "Matches", "Players")]
    [InlineData("medals", "Stats", "Medals")]
    [InlineData("stats", "Stats", "Service record")]
    public async Task The_current_section_and_tab_are_marked(string path, string section, string tab)
    {
        await SignInAsync();
        Services.GetRequiredService<NavigationManager>().NavigateTo(path);
        var cut = RenderLayout();
        cut.WaitForAssertion(() => Assert.Equal(section, cut.Find("nav.sections a.active").TextContent), Wait);
        Assert.Equal(tab, cut.Find("nav.tabs a[aria-current=page]").TextContent);
    }

    [Fact]
    public async Task Sections_without_tabs_have_no_tab_bar()
    {
        await SignInAsync();
        Services.GetRequiredService<NavigationManager>().NavigateTo("progress");
        var cut = RenderLayout();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#body")), Wait);
        Assert.Empty(cut.FindAll(".subbar"));
    }

    [Fact]
    public async Task The_menu_picks_a_theme_and_closes_on_escape()
    {
        await SignInAsync();
        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.Find(".user-btn"), Wait);
        OnRenderer(cut, () => cut.Find(".user-btn").Click());
        Assert.Equal("true", cut.Find(".user-btn").GetAttribute("aria-expanded"));
        Assert.Equal("B312", cut.Find(".menu .stag").TextContent);
        Assert.Equal(["Basic", "Halo", "XBOX"], cut.FindAll(".theme-opt .theme-name").Select(n => n.TextContent));

        OnRenderer(cut, () => cut.FindAll(".theme-opt").Single(b => b.TextContent.Contains("Halo")).Click());
        Assert.Equal("halo", JSInterop.VerifyInvoke("haloTheme.set").Arguments[0]);
        Assert.Equal("true", cut.FindAll(".theme-opt").Single(b => b.TextContent.Contains("Halo")).GetAttribute("aria-checked"));

        OnRenderer(cut, () => cut.Find("#user-menu").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" }));
        Assert.Empty(cut.FindAll("#user-menu"));
    }

    [Fact]
    public async Task Signing_out_returns_to_sign_in()
    {
        await SignInAsync();
        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.Find(".user-btn"), Wait);
        OnRenderer(cut, () => cut.Find(".user-btn").Click());
        OnRenderer(cut, () => cut.FindAll(".menu-item").Single(b => b.TextContent == "Sign out").Click());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("section.signin")), Wait);
        Assert.False(Session.IsSignedIn);
    }
}

public sealed class EnvironmentSignInTests() : ComponentTest(new Harness(envToken: Spartans.Token))
{
    [Fact]
    public void The_environment_token_signs_in_and_says_so()
    {
        SignedOut();
        var cut = Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<p id=\"body\"></p>"))));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#body")), Wait);
        OnRenderer(cut, () => cut.Find(".user-btn").Click());
        Assert.Contains("Signed in with HALO_SPARTAN_TOKEN", cut.Find(".menu-note").TextContent);
    }
}

public sealed class BadEnvironmentTokenTests() : ComponentTest(new Harness(envToken: "v4=gravemind"))
{
    [Fact]
    public void A_bad_environment_token_falls_back_to_sign_in_with_a_reason()
    {
        SignedOut();
        var cut = Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => { })));
        cut.WaitForAssertion(() => Assert.StartsWith("HALO_SPARTAN_TOKEN didn't work: Halo Waypoint rejected the token.",
            cut.Find("section.signin p.error").TextContent), Wait);
        Assert.DoesNotContain("gravemind", cut.Markup);
    }
}
