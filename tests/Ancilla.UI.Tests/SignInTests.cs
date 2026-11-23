using System.Net;
using Ancilla.UI.Services;
using static Ancilla.Testing.Spartans;

namespace Ancilla.UI.Tests;

/// <summary>Token sources, staying signed in, and what happens when a token runs out.</summary>
public sealed class SignInTests : IDisposable
{
    private const string Fresh = "v4=cortana-refreshed-token";
    private readonly Harness _h = new();
    private readonly FakeWaypointSignIn _waypoint = new();

    public SignInTests() => _h.Waypoint = _waypoint;

    public void Dispose() => _h.Dispose();

    /// <summary>The fake's first token runs out; the next one the sign-in would find is <see cref="Fresh"/>.</summary>
    private void TokenRunsOut()
    {
        _h.Halo.AcceptedToken = Fresh;
        _waypoint.Cookie = Fresh;
    }

    // ---- Token sources ------------------------------------------------------------------

    [Fact]
    public async Task A_pasted_token_is_handed_over_once_and_cant_refresh()
    {
        var source = new PastedToken(Token);
        Assert.Equal(Token, await source.GetTokenAsync());
        Assert.Null(await source.GetTokenAsync());
        Assert.Null(await source.RefreshAsync());
        Assert.Null(await new PastedToken("   ").GetTokenAsync());
    }

    [Fact]
    public async Task The_environment_source_reads_the_variable_and_cant_refresh()
    {
        Assert.Null(await new EnvironmentToken(_h.Config).GetTokenAsync());
        using var env = new Harness(envToken: Token);
        var source = new EnvironmentToken(env.Config);
        Assert.Equal(Token, await source.GetTokenAsync());
        Assert.Null(await source.RefreshAsync());
    }

    [Fact]
    public async Task The_waypoint_source_shows_the_page_only_when_asked_and_refreshes_hidden()
    {
        await new WaypointToken(_waypoint, interactive: true).GetTokenAsync();
        await new WaypointToken(_waypoint, interactive: false).GetTokenAsync();
        await new WaypointToken(_waypoint, interactive: true).RefreshAsync();
        Assert.Equal(1, _waypoint.SignIns);
        Assert.Equal(2, _waypoint.SilentSignIns);
    }

    // ---- Signing in -----------------------------------------------------------------------

    [Fact]
    public async Task Signing_in_with_microsoft_uses_the_cookie_the_sign_in_found()
    {
        var s = _h.NewSession();
        Assert.True(await s.SignInAsync(new WaypointToken(_waypoint, interactive: true)));
        Assert.Equal(NobleSix.Gamertag, s.Me!.Gamertag);
        Assert.Equal(SignInMethod.Waypoint, s.Method);
        Assert.False(s.FromEnvironment);
    }

    [Fact]
    public async Task A_closed_sign_in_window_or_a_missing_cookie_leaves_you_signed_out()
    {
        var s = _h.NewSession();
        _waypoint.Cancelled = true;
        Assert.False(await s.SignInAsync(new WaypointToken(_waypoint, interactive: true)));

        _waypoint.Cancelled = false;
        _waypoint.Cookie = null;
        Assert.False(await s.SignInAsync(new WaypointToken(_waypoint, interactive: true)));
        Assert.False(s.IsSignedIn);
        Assert.Empty(_h.Halo.Requests);
    }

    [Fact]
    public async Task A_cookie_halo_refuses_is_a_401()
    {
        _waypoint.Cookie = "v4=flood-spore";
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _h.NewSession().SignInAsync(new WaypointToken(_waypoint, interactive: true)));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    // ---- Staying signed in ----------------------------------------------------------------

    [Fact]
    public async Task With_stay_signed_in_the_app_signs_in_by_itself_at_start_up()
    {
        _waypoint.StaySignedIn = true;
        _waypoint.HasMicrosoftSession = true;
        var s = _h.NewSession();
        Assert.True(await s.TrySignInAutomaticallyAsync());
        Assert.Equal(SignInMethod.Waypoint, s.Method);
        Assert.Equal(0, _waypoint.SignIns);   // never shows the page by itself
        Assert.Equal(1, _waypoint.SilentSignIns);
    }

    [Fact]
    public async Task Without_stay_signed_in_nothing_is_tried_at_start_up()
    {
        _waypoint.HasMicrosoftSession = true;
        Assert.False(await _h.NewSession().TrySignInAutomaticallyAsync());
        Assert.Equal(0, _waypoint.SilentSignIns);
    }

    [Fact]
    public async Task A_kept_sign_in_microsoft_has_forgotten_shows_the_sign_in_page_quietly()
    {
        _waypoint.StaySignedIn = true;
        Assert.False(await _h.NewSession().TrySignInAutomaticallyAsync());   // login_required: no session

        _waypoint.HasMicrosoftSession = true;
        _waypoint.Cookie = "v4=flood-spore";
        Assert.False(await _h.NewSession().TrySignInAutomaticallyAsync());   // a token Halo refuses
    }

    [Fact]
    public async Task The_environment_variable_comes_before_a_kept_sign_in()
    {
        using var env = new Harness(envToken: Token) { Waypoint = _waypoint };
        _waypoint.StaySignedIn = true;
        _waypoint.HasMicrosoftSession = true;
        var s = env.NewSession();
        Assert.True(await s.TrySignInAutomaticallyAsync());
        Assert.True(s.FromEnvironment);
        Assert.Equal(0, _waypoint.SilentSignIns);
    }

    [Fact]
    public async Task Signing_out_clears_the_sign_in_browser()
    {
        var s = _h.NewSession();
        await s.SignInAsync(new WaypointToken(_waypoint, interactive: true));
        await s.SignOutAsync();
        Assert.Equal(1, _waypoint.SignOuts);
        Assert.False(_waypoint.HasMicrosoftSession);
        Assert.Null(s.Method);
    }

    [Fact]
    public async Task Signing_out_on_the_web_host_has_no_browser_to_clear()
    {
        using var web = new Harness();
        var s = await web.SignedInAsync();
        await s.SignOutAsync();   // NoWaypointSignIn: nothing to do, nothing thrown
        Assert.False(s.IsSignedIn);
    }

    // ---- When the token runs out -----------------------------------------------------------

    [Fact]
    public async Task A_token_that_runs_out_is_replaced_by_a_hidden_sign_in()
    {
        var s = _h.NewSession();
        await s.SignInAsync(new WaypointToken(_waypoint, interactive: true));
        TokenRunsOut();

        var record = await s.GetServiceRecordAsync();

        Assert.NotNull(record);
        Assert.False(s.Expired);
        Assert.Equal(1, _waypoint.SilentSignIns);
        Assert.Equal(Fresh, _h.Halo.Requests.Last().Headers["x-343-authorization-spartan"]);
    }

    [Fact]
    public async Task When_the_hidden_sign_in_fails_the_session_expires_but_keeps_its_data()
    {
        var s = _h.NewSession();
        await s.SignInAsync(new WaypointToken(_waypoint, interactive: true));
        await s.LoadMoreMatchesAsync();
        var rows = s.Matches.Rows.Count;
        var changed = 0;
        s.Changed += () => changed++;

        TokenRunsOut();
        _waypoint.HasMicrosoftSession = false;   // Microsoft's session has gone too
        await Assert.ThrowsAsync<HttpRequestException>(() => s.GetServiceRecordAsync(refresh: true));

        Assert.True(s.Expired);
        Assert.True(s.IsSignedIn);
        Assert.Equal(1, changed);

        // Signing in again as the same player carries on.
        Assert.True(await s.SignInAsync(new WaypointToken(_waypoint, interactive: true)));
        Assert.False(s.Expired);
        Assert.Equal(rows, s.Matches.Rows.Count);
        await s.GetServiceRecordAsync(refresh: true);
    }

    [Fact]
    public async Task A_hidden_sign_in_that_throws_expires_the_session_without_the_error()
    {
        var s = _h.NewSession();
        await s.SignInAsync(new WaypointToken(_waypoint, interactive: true));
        TokenRunsOut();
        _waypoint.SilentFailure = new InvalidOperationException("web view crashed");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => s.GetServiceRecordAsync(refresh: true));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.True(s.Expired);
    }

    [Fact]
    public async Task A_pasted_token_that_runs_out_expires_the_session()
    {
        var s = await _h.SignedInAsync();
        _h.Halo.AcceptedToken = Fresh;
        await Assert.ThrowsAsync<HttpRequestException>(() => s.GetServiceRecordAsync(refresh: true));
        Assert.True(s.Expired);
        Assert.Equal(0, _waypoint.SilentSignIns);

        await s.SignInAsync(Fresh);
        Assert.False(s.Expired);
        Assert.Equal(SignInMethod.Pasted, s.Method);
    }

    [Fact]
    public async Task Signing_in_as_someone_else_after_expiry_starts_afresh()
    {
        var s = await _h.SignedInAsync();
        await s.LoadMoreMatchesAsync();
        _h.Halo.AcceptedToken = Fresh;
        await Assert.ThrowsAsync<HttpRequestException>(() => s.GetServiceRecordAsync(refresh: true));
        Assert.True(s.Expired);

        // Another account (the fake answers users/me with the same player, so pretend by signing out first).
        await s.SignOutAsync();
        Assert.False(s.Expired);
        Assert.Empty(s.Matches.Rows);
    }
}
