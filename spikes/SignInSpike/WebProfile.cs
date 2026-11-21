namespace SignInSpike;

/// <summary>
/// One cookie from the sign-in web view's store. <see cref="Value"/> is secret: never render, log
/// or format it. <see cref="ToString"/> leaves it out so string interpolation can't leak it.
/// </summary>
public sealed class WebCookie(string name, string value, string domain, string path,
    DateTimeOffset? expires, bool httpOnly, bool secure)
{
    public string Name { get; } = name;
    public string Value { get; } = value;
    public string Domain { get; } = domain;
    public string Path { get; } = path;
    public DateTimeOffset? Expires { get; } = expires;
    public bool HttpOnly { get; } = httpOnly;
    public bool Secure { get; } = secure;

    public override string ToString() => $"{Name} ({Domain})";
}

/// <summary>What one finished navigation looked like. Hosts and titles only, never full URLs.</summary>
public sealed record NavigationReport(string Host, bool Succeeded, string? Title, string? UserAgent, string? Error, string Path = "/")
{
    public static bool IsWaypoint(string host) => host.EndsWith("halowaypoint.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Page titles on halowaypoint.com can name the player, so only other sites' titles are kept.</summary>
    public static string? SafeTitle(string host, string? title) =>
        IsWaypoint(host) ? null : title;
}

/// <summary>
/// The sign-in web view's profile: a cookie store Ancilla owns, kept apart from the BlazorWebView's.
/// macOS: a <c>WKWebsiteDataStore</c> with its own identifier. Windows: a WebView2 user data folder.
/// </summary>
public interface IWebProfile
{
    /// <summary>Where the profile lives, safe to show (home folder as ~).</summary>
    string Description { get; }

    Task<IReadOnlyList<WebCookie>> GetCookiesAsync();

    Task DeleteCookieAsync(WebCookie cookie);

    /// <summary>
    /// Loads a page in a web view the user never sees, then lets it go. With
    /// <paramref name="followSignIn"/>, it waits until the page has gone off to another site
    /// (Microsoft) and come back to halowaypoint.com, rather than for the first page.
    /// <paramref name="onStep"/> gets "host/path" (never the query) for each request.
    /// </summary>
    Task<NavigationReport> LoadHiddenAsync(Uri url, string? userAgent, TimeSpan timeout, bool followSignIn,
        Action<string> onStep);

    /// <summary>Signs out: removes everything in the profile. Close the sign-in page first.</summary>
    Task<string> DeleteProfileAsync();

    /// <summary>Start-up work left over from a sign-out (macOS removes the old store here). Null if none.</summary>
    Task<string?> CleanUpAsync();
}

/// <summary>A native web view for halowaypoint.com, using <see cref="IWebProfile"/>'s store.</summary>
public sealed class SignInView : View
{
    public required Uri StartUrl { get; init; }
    public string? UserAgent { get; init; }

    public event EventHandler<NavigationReport>? Navigated;

    /// <summary>Each main-frame request, as host and path (never the query, which holds one-time state).</summary>
    public event EventHandler<(string Host, string Path)>? Requested;

    internal void ReportNavigation(NavigationReport report) => Navigated?.Invoke(this, report);

    internal void ReportRequest(string host, string path) => Requested?.Invoke(this, (host, path));
}

/// <summary>
/// Microsoft's OAuth authorise requests. Logs only parameter names (and prompt's value), and for a
/// hidden sign-in swaps prompt for prompt=none: the standard silent sign-in, which either returns
/// straight to the app or fails with login_required, but never shows a page that needs a click.
/// </summary>
public static class SilentAuth
{
    public static bool IsAuthorize(Uri u) =>
        (u.Host.Equals("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.EndsWith("/authorize", StringComparison.Ordinal)) ||
        (u.Host.Equals("login.live.com", StringComparison.OrdinalIgnoreCase) && u.AbsolutePath.EndsWith("oauth20_authorize.srf", StringComparison.Ordinal));

    /// <summary>Host and path, plus parameter names (never values, except prompt and error) for Microsoft requests.</summary>
    public static string Describe(Uri u)
    {
        var text = u.Host + u.AbsolutePath;
        if (NavigationReport.IsWaypoint(u.Host) && !u.Query.Contains("error=")) return text;
        var names = Pairs(u).Select(p =>
        {
            var name = p.Split('=')[0];
            return name is "prompt" or "error" ? p : name;
        }).ToList();
        return names.Count == 0 ? text : $"{text} ?{string.Join(",", names)}";
    }

    public static Uri? WithPromptNone(Uri u)
    {
        if (!IsAuthorize(u)) return null;
        var pairs = Pairs(u);
        var i = pairs.FindIndex(p => p.StartsWith("prompt=", StringComparison.Ordinal));
        if (i >= 0 && pairs[i] == "prompt=none") return null;
        if (i >= 0) pairs[i] = "prompt=none"; else pairs.Add("prompt=none");
        return new UriBuilder(u) { Query = string.Join("&", pairs) }.Uri;
    }

    private static List<string> Pairs(Uri u) =>
        u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
}
