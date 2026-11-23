using Ancilla.Testing;
using Ancilla.UI.Components.Layout;
using Ancilla.UI.Components.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Ancilla.UI.Tests.Components;

/// <summary>The sign-in page as the desktop app shows it, with halowaypoint.com's sign-in embedded.</summary>
public sealed class DesktopSignInTests : ComponentTest
{
    private const string Fresh = "v4=cortana-refreshed-token";
    private readonly FakeWaypointSignIn _waypoint;

    public DesktopSignInTests() : this(new FakeWaypointSignIn()) { }

    private DesktopSignInTests(FakeWaypointSignIn waypoint) : base(new Harness { Waypoint = waypoint }) => _waypoint = waypoint;

    private IRenderedComponent<MainLayout> RenderLayout() =>
        Render<MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddMarkupContent(0, "<p id=\"body\">Spartans never die.</p>"))));

    [Fact]
    public void Offers_microsoft_sign_in_first_and_pasting_as_the_fallback()
    {
        SignedOut();
        var cut = Render<SignIn>();
        Assert.Equal("Sign in with Microsoft", cut.Find("button.primary").TextContent.Trim());
        Assert.Equal("Paste a token instead", cut.Find("details.paste summary").TextContent);
        Assert.NotNull(cut.Find("details.paste input#token"));
        Assert.Equal(4, cut.FindAll("details.paste ol.steps li").Count);
    }

    [Fact]
    public void Signing_in_with_microsoft_signs_in()
    {
        SignedOut();
        var cut = Render<SignIn>();
        Click(cut, "button.primary", "Sign in with Microsoft");
        cut.WaitForAssertion(() => Assert.True(Session.IsSignedIn), Wait);
        Assert.Equal(1, _waypoint.SignIns);
    }

    [Fact]
    public void A_sign_in_that_didnt_finish_says_so()
    {
        SignedOut();
        _waypoint.Cancelled = true;
        var cut = Render<SignIn>();
        Click(cut, "button.primary", "Sign in with Microsoft");
        cut.WaitForAssertion(() => Assert.StartsWith("Sign-in didn't finish", cut.Find("p.error[role=alert]").TextContent), Wait);
        Assert.False(Session.IsSignedIn);
    }

    [Fact]
    public void Stay_signed_in_is_remembered_and_starts_off()
    {
        SignedOut();
        var cut = Render<SignIn>();
        var box = cut.Find("label.stay input[type=checkbox]");
        Assert.False(box.HasAttribute("checked"));
        box.Change(true);
        Assert.True(_waypoint.StaySignedIn);
        cut.Find("label.stay input[type=checkbox]").Change(false);
        Assert.False(_waypoint.StaySignedIn);
    }

    [Fact]
    public void A_kept_sign_in_signs_in_at_start_up_without_the_page()
    {
        _waypoint.StaySignedIn = true;
        _waypoint.HasMicrosoftSession = true;
        SignedOut();
        var cut = RenderLayout();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#body")), Wait);
        Assert.Equal(0, _waypoint.SignIns);
    }

    [Fact]
    public async Task When_the_sign_in_runs_out_the_page_asks_again_and_then_carries_on()
    {
        SignedOut();
        Assert.True(await Session.SignInAsync(new Services.WaypointToken(_waypoint, interactive: true)));
        var cut = RenderLayout();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#body")), Wait);

        H.Halo.AcceptedToken = Fresh;
        _waypoint.HasMicrosoftSession = false;
        await Assert.ThrowsAsync<HttpRequestException>(() => Session.GetServiceRecordAsync(refresh: true));

        cut.WaitForAssertion(() => Assert.Contains("Your sign-in has run out", cut.Find("section.signin p.expired").TextContent), Wait);
        Assert.Empty(cut.FindAll("#body"));

        _waypoint.Cookie = Fresh;
        Click(cut, "button.primary", "Sign in with Microsoft");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#body")), Wait);
        Assert.False(Session.Expired);
    }

    [Fact]
    public async Task The_token_never_reaches_the_page_or_its_scripts()
    {
        _waypoint.StaySignedIn = true;
        _waypoint.HasMicrosoftSession = true;
        SignedOut();
        var cut = RenderLayout();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#body")), Wait);

        // A refresh mid-session, too.
        H.Halo.AcceptedToken = Fresh;
        _waypoint.Cookie = Fresh;
        await Session.GetServiceRecordAsync(refresh: true);
        OnRenderer(cut, () => cut.Find(".user-btn").Click());

        foreach (var secret in new[] { Spartans.Token["v4=".Length..], Fresh["v4=".Length..] })
        {
            Assert.DoesNotContain(secret, cut.Markup);
            Assert.DoesNotContain(JSInterop.Invocations, i =>
                i.Arguments.Any(a => a?.ToString()?.Contains(secret, StringComparison.Ordinal) == true));
        }
    }

    [Fact]
    public void Signing_out_clears_the_sign_in_browser()
    {
        _waypoint.StaySignedIn = true;
        _waypoint.HasMicrosoftSession = true;
        SignedOut();
        var cut = RenderLayout();
        cut.WaitForAssertion(() => cut.Find(".user-btn"), Wait);
        OnRenderer(cut, () => cut.Find(".user-btn").Click());
        OnRenderer(cut, () => cut.FindAll(".menu-item").Single(b => b.TextContent == "Sign out").Click());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("section.signin")), Wait);
        Assert.Equal(1, _waypoint.SignOuts);
    }
}

/// <summary>The web host can't embed halowaypoint.com, so it only offers pasting.</summary>
public sealed class WebSignInTests : ComponentTest
{
    [Fact]
    public void Only_offers_the_guided_paste()
    {
        SignedOut();
        var cut = Render<SignIn>();
        Assert.Empty(cut.FindAll("button.primary"));
        Assert.Empty(cut.FindAll("details.paste"));
        Assert.Empty(cut.FindAll("label.stay"));
        Assert.Equal(4, cut.FindAll("ol.steps li").Count);
        Assert.NotNull(cut.Find("input#token"));
    }
}
