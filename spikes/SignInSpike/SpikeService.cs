using Ancilla.Core;

namespace SignInSpike;

/// <summary>What the token looked like when it was last read from the cookie store.</summary>
public sealed record TokenReading(
    DateTimeOffset FirstSeen,
    DateTimeOffset ReadAt,
    string Source,
    DateTimeOffset? CookieExpires,
    bool HttpOnly,
    bool Secure,
    string Domain,
    TokenFormat Format);

public sealed record CookieSummary(string Name, string Domain, DateTimeOffset? Expires, bool HttpOnly, bool Secure);

public sealed record ApiStep(string Name, bool Ok, string Detail);

public sealed record LogEntry(DateTimeOffset At, string Text);

/// <summary>Everything the status page shows. Nothing in here is secret.</summary>
public sealed class SpikeState
{
    public event Action? Changed;

    public TokenReading? Token { get; set; }
    public IReadOnlyList<CookieSummary> Cookies { get; set; } = [];
    public IReadOnlyList<ApiStep> ApiSteps { get; set; } = [];
    public TokenFormat? Pasted { get; set; }
    public List<string> HostsVisited { get; } = [];

    /// <summary>The halowaypoint.com path that started the last visible sign-in, learnt from it.</summary>
    public string? SignInEntry { get; set; } = Preferences.Default.Get<string?>("signin-entry", null);
    public string? UserAgentSeen { get; set; }
    public bool UseBrowserUserAgent { get; set; }
    public bool AutoRefresh { get; set; }
    public bool WatchLifetime { get; set; }
    public string? ProbeStatus { get; set; }
    public bool SignInOpen { get; set; }
    public bool Busy { get; set; }
    public List<LogEntry> Log { get; } = [];

    public void Note(string text)
    {
        Log.Insert(0, new LogEntry(DateTimeOffset.Now, text));
        Notify();
    }

    public void Notify() => Changed?.Invoke();
}

public sealed class SpikeService
{
    public static readonly Uri Waypoint = new("https://www.halowaypoint.com/");
    public const string TokenCookie = "343-spartan-token";

    /// <summary>A current desktop browser's user agent, for when the web view's own is refused.</summary>
    public static string BrowserUserAgent =>
        OperatingSystem.IsWindows()
            ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36 Edg/142.0.0.0"
            : "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";

    private readonly IWebProfile _profile;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // The token itself. It lives here, in memory, and nowhere else.
    private string? _token;

    public SpikeService(IWebProfile profile, HttpClient http, SpikeState state)
    {
        _profile = profile;
        _http = http;
        State = state;
        _ = LifetimeLoopAsync();
    }

    public async Task StartAsync()
    {
        if (await _profile.CleanUpAsync() is { } note) State.Note(note);
        await ReadCookiesAsync("at start-up");
    }

    public SpikeState State { get; }
    public string ProfileDescription => _profile.Description;
    public string? UserAgentOverride => State.UseBrowserUserAgent ? BrowserUserAgent : null;

    public async Task OpenSignInAsync()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page
            ?? throw new InvalidOperationException("No window to show the sign-in page in.");
        await MainThread.InvokeOnMainThreadAsync(() => page.Navigation.PushModalAsync(new SignInPage(this)));
        State.SignInOpen = true;
        _lastWaypointPath = null;
        _entryCaptured = false;
        State.Note($"Opened the sign-in page ({(State.UseBrowserUserAgent ? "browser" : "web view's own")} user agent).");
    }

    public async Task CloseSignInAsync()
    {
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is not null && page.Navigation.ModalStack.Count > 0)
            await MainThread.InvokeOnMainThreadAsync(() => page.Navigation.PopModalAsync());
        State.SignInOpen = false;
        State.Note("Closed the sign-in page.");
        await ReadCookiesAsync("after closing sign-in");
    }

    // The last Waypoint path requested during the visible sign-in, until the page leaves for Microsoft.
    private string? _lastWaypointPath;
    private bool _entryCaptured;

    public void OnSignInRequested(string host, string path)
    {
        State.HostsVisited.Add($"{DateTimeOffset.Now:HH:mm:ss}  \u2192 {host}{path}");
        NoteHost(host, path);
        State.Notify();
    }

    private void NoteHost(string host, string path)
    {
        if (NavigationReport.IsWaypoint(host))
        {
            _lastWaypointPath = path.Replace(" (redirect)", "");
            return;
        }
        if (_entryCaptured || _lastWaypointPath is null) return;
        _entryCaptured = true;
        State.SignInEntry = _lastWaypointPath;
        Preferences.Default.Set("signin-entry", _lastWaypointPath);
        State.Note($"Sign-in entry learnt: {_lastWaypointPath} (the last Waypoint path before {host}).");
    }

    public async Task OnSignInNavigatedAsync(NavigationReport report)
    {
        NoteHost(report.Host, report.Path);
        var entry = report.Succeeded ? report.Host + report.Path : $"{report.Host}{report.Path} (failed: {report.Error})";
        if (report.Title is { Length: > 0 }) entry += $" “{report.Title}”";
        State.HostsVisited.Add($"{DateTimeOffset.Now:HH:mm:ss}  {entry}");
        if (report.UserAgent is not null) State.UserAgentSeen = report.UserAgent;
        State.Notify();
        await ReadCookiesAsync($"after loading {report.Host}");
    }

    /// <summary>Reads the profile's cookies and updates the token reading. Returns whether the token changed.</summary>
    public async Task<bool> ReadCookiesAsync(string source)
    {
        IReadOnlyList<WebCookie> cookies;
        try { cookies = await _profile.GetCookiesAsync(); }
        catch (Exception ex)
        {
            State.Note($"Couldn't read cookies {source}: {ex.GetType().Name}");
            return false;
        }

        var waypoint = cookies.Where(c => c.Domain.TrimStart('.').EndsWith("halowaypoint.com", StringComparison.OrdinalIgnoreCase)).ToList();
        State.Cookies = waypoint
            .Select(c => new CookieSummary(c.Name, c.Domain, c.Expires, c.HttpOnly, c.Secure))
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        var spartan = waypoint.FirstOrDefault(c => c.Name == TokenCookie);
        var before = State.Token?.Format.Fingerprint;

        if (spartan is null)
        {
            if (_token is not null) State.Note($"Token cookie gone ({source}).");
            _token = null;
            State.Token = null;
            State.Notify();
            return before is not null;
        }

        var format = TokenFormat.Of(spartan.Value);
        _token = HaloWaypointClient.NormaliseToken(spartan.Value);
        var changed = before != format.Fingerprint;
        var now = DateTimeOffset.Now;
        State.Token = new TokenReading(changed ? now : State.Token!.FirstSeen, now, source, spartan.Expires,
            spartan.HttpOnly, spartan.Secure, spartan.Domain, format);

        if (changed)
            State.Note(before is null
                ? $"Token found {source}. {ExpiryText(spartan.Expires)}"
                : $"Token changed {source} ({before} → {format.Fingerprint}). {ExpiryText(spartan.Expires)}");
        else
            State.Notify();
        return changed;
    }

    public async Task TestApiAsync()
    {
        if (_token is null) { State.Note("No token to test."); return; }
        var steps = new List<ApiStep>();
        var client = new HaloWaypointClient(_http, _token);

        UserProfile? me = null;
        steps.Add(await Step("Profile (users/me)", async () => me = await client.GetMeAsync()));
        if (me is not null)
        {
            steps.Add(await Step("Clearance (flight)", () => client.GetClearanceAsync(me.Xuid)));
            steps.Add(await Step("Service record", () => client.GetServiceRecordAsync(me.Xuid)));
            steps.Add(await Step("Career rank (needs clearance)", () => client.GetCareerRankAsync(me.Xuid)));
        }

        State.ApiSteps = steps;
        State.Note($"API test: {steps.Count(s => s.Ok)} of {steps.Count} calls worked.");
    }

    // Shows a status code, never the exception message: the client's messages include URLs with the XUID.
    private static async Task<ApiStep> Step(string name, Func<Task> call)
    {
        try
        {
            await call();
            return new ApiStep(name, true, "OK");
        }
        catch (HttpRequestException ex)
        {
            return new ApiStep(name, false, ex.StatusCode is { } s ? $"{(int)s} {s}" : "network error");
        }
        catch (Exception ex)
        {
            return new ApiStep(name, false, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Q4: loads halowaypoint.com in a web view nobody sees and watches for a new token. With
    /// <paramref name="deleteTokenFirst"/>, removes the token cookie first, to see whether the
    /// site can mint a fresh one from the rest of the session.
    /// </summary>
    public async Task HiddenRefreshAsync(bool deleteTokenFirst, string why = "on request", bool viaSignIn = false)
    {
        if (!await _gate.WaitAsync(0)) { State.Note("A hidden refresh is already running."); return; }
        State.Busy = true;
        State.Notify();
        try
        {
            var before = State.Token;
            if (deleteTokenFirst)
            {
                var cookie = (await _profile.GetCookiesAsync()).FirstOrDefault(c => c.Name == TokenCookie);
                if (cookie is not null)
                {
                    await _profile.DeleteCookieAsync(cookie);
                    await ReadCookiesAsync("after deleting the token cookie");
                }
            }

            var target = viaSignIn && State.SignInEntry is { } entry ? new Uri(Waypoint, entry) : Waypoint;
            State.Note($"Hidden {(viaSignIn ? "sign-in" : "refresh")} started on {target.AbsolutePath} " +
                       $"({why}{(deleteTokenFirst ? ", token cookie deleted first" : "")}).");
            var report = await _profile.LoadHiddenAsync(target, UserAgentOverride, TimeSpan.FromSeconds(45),
                followSignIn: viaSignIn, onStep: step => State.Note($"  hidden: {step}"));
            State.Note(report.Succeeded
                ? $"Hidden web view ended on {report.Host}{report.Path}."
                : $"Hidden web view stopped on {report.Host}{report.Path}{(report.Title is { } t ? $" \u201c{t}\u201d" : "")}: {report.Error}");

            // The cookie might arrive with the page or from a later request; give it a while.
            var changed = false;
            for (var i = 0; i < 10 && !changed; i++)
            {
                changed = await ReadCookiesAsync("after hidden refresh");
                if (!changed) await Task.Delay(TimeSpan.FromSeconds(2));
            }

            var after = State.Token;
            State.Note((before, after) switch
            {
                (_, null) => "Hidden refresh: no token afterwards. Signing in again would be needed.",
                _ when changed => $"Hidden refresh: new token. {ExpiryText(after.CookieExpires)}",
                _ => $"Hidden refresh: same token as before. {ExpiryText(after.CookieExpires)}",
            });
        }
        catch (Exception ex)
        {
            State.Note($"Hidden refresh failed: {ex.GetType().Name}");
        }
        finally
        {
            State.Busy = false;
            _gate.Release();
            State.Notify();
        }
    }

    // Q4, lifetime: the cookie has no expiry and the token is encrypted, so the only way to learn
    // how long it lasts is to keep using it until the API refuses it.
    private string? _rejected;
    private int _hoursNoted;

    private async Task LifetimeLoopAsync()
    {
        var minutes = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync())
        {
            if (State.WatchLifetime && ++minutes % 5 == 0) await ProbeAsync();
        }
    }

    public async Task ProbeAsync()
    {
        if (_token is not { } token || State.Token is not { } reading) { State.ProbeStatus = "No token to check."; State.Notify(); return; }
        if (reading.Format.Fingerprint == _rejected) return;

        var age = (int)Math.Round((DateTimeOffset.Now - reading.FirstSeen).TotalMinutes);
        try
        {
            await new HaloWaypointClient(_http, token).GetMeAsync();
            State.ProbeStatus = $"Token {reading.Format.Fingerprint} still works at {DateTimeOffset.Now:HH:mm}, {age} min after it was first seen.";
            if (age / 60 > _hoursNoted) { _hoursNoted = age / 60; State.Note(State.ProbeStatus); }
            State.Notify();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            _rejected = reading.Format.Fingerprint;
            _hoursNoted = 0;
            State.ProbeStatus = $"Token {reading.Format.Fingerprint} refused ({(int)ex.StatusCode!}) {age} min after it was first seen " +
                                $"(first seen {reading.FirstSeen.ToLocalTime():HH:mm}).";
            State.Note(State.ProbeStatus);
            if (State.AutoRefresh)
                await HiddenRefreshAsync(deleteTokenFirst: false, why: "automatic, token refused", viaSignIn: State.SignInEntry is not null);
        }
        catch (HttpRequestException ex)
        {
            State.ProbeStatus = $"Check failed at {DateTimeOffset.Now:HH:mm}: {(ex.StatusCode is { } s ? $"{(int)s} {s}" : "network error")}. Will try again.";
            State.Notify();
        }
    }

    public async Task DeleteProfileAsync()
    {
        if (State.SignInOpen) await CloseSignInAsync();
        try
        {
            var outcome = await _profile.DeleteProfileAsync();
            State.Note($"Delete profile: {outcome}");
        }
        catch (Exception ex)
        {
            State.Note($"Delete profile failed: {ex.GetType().Name}: {ex.Message}");
        }
        State.HostsVisited.Clear();
        await ReadCookiesAsync("after deleting the profile");
    }

    /// <summary>Q6: a token pasted from the browser's developer tools, compared by format only.</summary>
    public void InspectPasted(string pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted)) return;
        State.Pasted = TokenFormat.Of(pasted);
        State.Note($"Pasted token inspected ({State.Pasted.Fingerprint}). It isn't kept.");
    }

    public static string ExpiryText(DateTimeOffset? expires) => expires is { } e
        ? $"Cookie expires {e.ToLocalTime():HH:mm}, in {Math.Round((e - DateTimeOffset.Now).TotalMinutes)} minutes."
        : "Session cookie (no expiry).";
}

/// <summary>The sign-in page, shown over the app's window.</summary>
public sealed class SignInPage : ContentPage
{
    public SignInPage(SpikeService spike)
    {
        var view = new SignInView { StartUrl = SpikeService.Waypoint, UserAgent = spike.UserAgentOverride };
        view.Navigated += async (_, report) => await spike.OnSignInNavigatedAsync(report);
        view.Requested += (_, r) => spike.OnSignInRequested(r.Host, r.Path);

        var status = new Label
        {
            Text = "Sign in to Halo Waypoint below. The spike shows facts about the token, never the token.",
            VerticalOptions = LayoutOptions.Center,
        };
        var close = new Button { Text = "Close sign-in" };
        close.Clicked += async (_, _) => await spike.CloseSignInAsync();

        void OnChanged() => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (spike.State.Token is { } t)
                status.Text = $"Token found ({t.Format.Fingerprint}). {SpikeService.ExpiryText(t.CookieExpires)} You can close this.";
        });
        spike.State.Changed += OnChanged;
        Disappearing += (_, _) => spike.State.Changed -= OnChanged;

        var bar = new Grid
        {
            Padding = new Thickness(12, 8),
            ColumnSpacing = 12,
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) },
        };
        bar.Add(status, 0);
        bar.Add(close, 1);

        var grid = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) } };
        grid.Add(bar, 0, 0);
        grid.Add(view, 0, 1);
        Content = grid;
    }
}
