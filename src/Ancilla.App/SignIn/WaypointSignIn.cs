using Ancilla.Core;
using Ancilla.UI.Services;

namespace Ancilla.App.SignIn;

/// <summary>
/// The sign-in browser's cookie store, kept apart from the app's own BlazorWebView: a WebView2 user
/// data folder. Persistent, it keeps
/// Microsoft's sign-in on disk between runs; otherwise it lives in memory and goes when the app quits.
/// </summary>
public interface IWebProfile
{
    /// <summary>Which store the next web view uses. Switching off removes what the persistent one kept.</summary>
    Task UsePersistentAsync(bool persistent);

    /// <summary>The <c>343-spartan-token</c> cookie's value for www.halowaypoint.com, or null. Secret: never log it.</summary>
    Task<string?> ReadTokenAsync();

    Task DeleteTokenAsync();

    /// <summary>
    /// Loads <paramref name="url"/> in a web view nobody sees, with Microsoft's sign-in asked to
    /// answer without showing anything (<see cref="SilentAuth"/>), and waits until it has been to
    /// Microsoft and come back to halowaypoint.com. False on a timeout or a failed load.
    /// </summary>
    Task<bool> LoadHiddenAsync(Uri url, TimeSpan timeout);

    /// <summary>Signs out: removes everything both stores hold. Close the sign-in page first.</summary>
    Task ClearAsync();

    /// <summary>Start-up work: removes stores left from an earlier sign-out, or from a run without "Stay signed in".</summary>
    Task CleanUpAsync(bool persistent);
}

/// <summary>A native web view on <see cref="IWebProfile"/>'s store, for halowaypoint.com's sign-in page.</summary>
public sealed class SignInView : View
{
    public required Uri StartUrl { get; init; }

    /// <summary>Raised after each main-frame page loads, with its host only.</summary>
    public event EventHandler<string>? PageLoaded;

    internal void ReportLoaded(string host) => PageLoaded?.Invoke(this, host);
}

/// <summary>
/// Microsoft's OAuth authorise requests. For a hidden sign-in, prompt is swapped for prompt=none:
/// the standard silent sign-in, which returns straight to halowaypoint.com with a new token, or
/// with error=login_required when Microsoft's session has gone, but never shows a page that needs you.
/// </summary>
public static class SilentAuth
{
    public static bool IsWaypoint(string? host) =>
        host is not null && (host.Equals("halowaypoint.com", StringComparison.OrdinalIgnoreCase) ||
                             host.EndsWith(".halowaypoint.com", StringComparison.OrdinalIgnoreCase));

    public static bool IsAuthorize(Uri u) =>
        (u.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.EndsWith("/authorize", StringComparison.Ordinal)) ||
        (u.Host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.EndsWith("oauth20_authorize.srf", StringComparison.Ordinal));

    /// <summary>The same request with prompt=none, or null when it isn't an authorise request or already says none.</summary>
    public static Uri? WithPromptNone(Uri u)
    {
        if (!IsAuthorize(u)) return null;
        var pairs = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
        var i = pairs.FindIndex(p => p.StartsWith("prompt=", StringComparison.Ordinal));
        if (i >= 0 && pairs[i] == "prompt=none") return null;
        if (i >= 0) pairs[i] = "prompt=none"; else pairs.Add("prompt=none");
        return new UriBuilder(u) { Query = string.Join("&", pairs) }.Uri;
    }
}

/// <summary>
/// The desktop app's sign-in: halowaypoint.com's own sign-in page in a window over the app, then the
/// <c>343-spartan-token</c> cookie it sets. Later tokens come from a hidden sign-in while Microsoft's
/// session lasts. Only the token's value leaves here, straight to the API client.
/// </summary>
public sealed class WaypointSignIn : IWaypointSignIn
{
    private const string StayKey = "stay-signed-in";
    private static readonly Uri Entry = new("https://www.halowaypoint.com/sign-in");
    private static readonly TimeSpan HiddenTimeout = TimeSpan.FromSeconds(45);

    private readonly IWebProfile _profile;
    private readonly HaloDiagnostics _diagnostics;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task _ready;

    public WaypointSignIn(IWebProfile profile, HaloDiagnostics diagnostics)
    {
        _profile = profile;
        _diagnostics = diagnostics;
        _ready = StartAsync(Preferences.Default.Get(StayKey, false));
    }

    private async Task StartAsync(bool persistent)
    {
        await _profile.CleanUpAsync(persistent);
        await _profile.UsePersistentAsync(persistent);
    }

    public bool IsAvailable => true;

    public bool StaySignedIn
    {
        get => Preferences.Default.Get(StayKey, false);
        set
        {
            if (value == StaySignedIn) return;
            // Off is the default, so it's removed rather than kept.
            if (value) Preferences.Default.Set(StayKey, true);
            else Preferences.Default.Remove(StayKey);
            var previous = _ready;
            _ready = SwitchAsync(previous, value);
        }
    }

    private async Task SwitchAsync(Task previous, bool persistent)
    {
        try { await previous; } catch (Exception) { /* logged when it failed */ }
        await _profile.UsePersistentAsync(persistent);
    }

    public async Task<string?> SignInAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await _ready;
            await _profile.DeleteTokenAsync();   // so a token found afterwards is a new one
            var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = new SignInPage(Entry, _profile, done);
            await MainThread.InvokeOnMainThreadAsync(() => Host().Navigation.PushModalAsync(page));
            string? token;
            try
            {
                using (ct.Register(() => done.TrySetResult(null)))
                    token = await done.Task;
            }
            finally
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (Host().Navigation.ModalStack.Contains(page)) await Host().Navigation.PopModalAsync();
                });
            }
            _diagnostics.Info(token is null ? "Sign-in window closed without a token" : "Sign-in window found a token");
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> SilentSignInAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await _ready;
            await _profile.DeleteTokenAsync();
            var returned = await _profile.LoadHiddenAsync(Entry, HiddenTimeout);
            // The cookie comes with the last redirect, but give it a moment in case it's set by a later request.
            for (var i = 0; returned && i < 5; i++)
            {
                if (await _profile.ReadTokenAsync() is { } token)
                {
                    _diagnostics.Info("Hidden sign-in found a token");
                    return token;
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            _diagnostics.Info(returned ? "Hidden sign-in came back without a token" : "Hidden sign-in didn't come back");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await _ready;
            await _profile.ClearAsync();
            _diagnostics.Info("Sign-in browser cleared");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Page Host() =>
        Application.Current?.Windows.FirstOrDefault()?.Page ?? throw new InvalidOperationException("No window to sign in from.");
}

/// <summary>halowaypoint.com's sign-in page, over the app's window, with a way to cancel.</summary>
internal sealed class SignInPage : ContentPage
{
    public SignInPage(Uri start, IWebProfile profile, TaskCompletionSource<string?> done)
    {
        Title = "Sign in to Halo Waypoint";
        var view = new SignInView { StartUrl = start };
        view.PageLoaded += async (_, host) =>
        {
            if (!SilentAuth.IsWaypoint(host) || done.Task.IsCompleted) return;
            try
            {
                if (await profile.ReadTokenAsync() is { } token) done.TrySetResult(token);
            }
            catch (Exception) { /* the next page load tries again */ }
        };

        var cancel = new Button { Text = "Cancel" };
        cancel.Clicked += (_, _) => done.TrySetResult(null);

        var bar = new Grid
        {
            Padding = new Thickness(16, 10),
            ColumnSpacing = 12,
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
        };
        bar.Add(new Label
        {
            Text = "Sign in to Halo Waypoint below. Your password goes to Microsoft; Ancilla only reads the sign-in token Halo Waypoint sets.",
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.WordWrap,
        }, 0);
        bar.Add(cancel, 1);

        var grid = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };
        grid.Add(bar, 0, 0);
        grid.Add(view, 0, 1);
        Content = grid;
        // However the page goes away, a sign-in still waiting ends without a token.
        Disappearing += (_, _) => done.TrySetResult(null);
    }
}
