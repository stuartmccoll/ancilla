using CoreGraphics;
using Foundation;
using WebKit;

namespace SignInSpike;

/// <summary>
/// macOS: a <see cref="WKWebsiteDataStore"/> with its own identifier (macOS 14+), so its cookies
/// live apart from the BlazorWebView's default store and survive restarts until deleted.
/// </summary>
public sealed class WebProfile : IWebProfile
{
    private const string StoreKey = "webkit-store-id";
    private const string PendingRemovalKey = "webkit-store-remove";
    private WKWebsiteDataStore? _store;
    private NSUuid? _id;

    public string Description =>
        $"WKWebsiteDataStore {Id.AsString()[..8]}… (under the app's Library/WebKit/WebsiteDataStore)";

    private NSUuid Id
    {
        get
        {
            if (_id is not null) return _id;
            var saved = Preferences.Default.Get(StoreKey, "");
            if (!Guid.TryParse(saved, out _))
            {
                saved = Guid.NewGuid().ToString();
                Preferences.Default.Set(StoreKey, saved);
            }
            return _id = new NSUuid(saved);
        }
    }

    private WKWebsiteDataStore Store => _store ??= WKWebsiteDataStore.Create(Id);

    public WKWebViewConfiguration CreateConfiguration() => new() { WebsiteDataStore = Store };

    public Task<IReadOnlyList<WebCookie>> GetCookiesAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var cookies = await AllCookiesAsync();
        return (IReadOnlyList<WebCookie>)cookies.Select(c => new WebCookie(
            c.Name, c.Value, c.Domain, c.Path,
            c.ExpiresDate is { } d ? new DateTimeOffset(((DateTime)d).ToUniversalTime()) : null,
            c.IsHttpOnly, c.IsSecure)).ToList();
    });

    public Task DeleteCookieAsync(WebCookie cookie) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        foreach (var c in await AllCookiesAsync())
        {
            if (c.Name != cookie.Name || c.Domain != cookie.Domain || c.Path != cookie.Path) continue;
            var done = new TaskCompletionSource();
            Store.HttpCookieStore.DeleteCookie(c, () => done.TrySetResult());
            await done.Task;
        }
    });

    private Task<NSHttpCookie[]> AllCookiesAsync()
    {
        var done = new TaskCompletionSource<NSHttpCookie[]>();
        Store.HttpCookieStore.GetAllCookies(cookies => done.TrySetResult(cookies));
        return done.Task;
    }

    public Task<NavigationReport> LoadHiddenAsync(Uri url, string? userAgent, TimeSpan timeout, bool followSignIn,
        Action<string> onStep) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            // Never added to a window: the user can't see it.
            using var view = new WKWebView(new CGRect(0, 0, 1280, 800), CreateConfiguration());
            if (userAgent is not null) view.CustomUserAgent = userAgent;

            var first = new TaskCompletionSource<NavigationReport>();
            var leftWaypoint = false;
            var nav = new NavigationDelegate(
                r =>
                {
                    if (!NavigationReport.IsWaypoint(r.Host)) leftWaypoint = true;
                    if (!followSignIn || !r.Succeeded || (leftWaypoint && NavigationReport.IsWaypoint(r.Host)))
                        first.TrySetResult(r);
                    else
                        onStep($"finished {r.Host}{r.Path}{(r.Title is { } t ? $" \u201c{t}\u201d" : "")}");
                },
                (host, path) => onStep($"request {host}{path}"),
                followSignIn ? SilentAuth.WithPromptNone : null,
                rewritten => onStep($"rewrote prompt to none: {rewritten}"));
            view.NavigationDelegate = nav;
            view.LoadRequest(new NSUrlRequest(new NSUrl(url.AbsoluteUri)));

            var finished = await Task.WhenAny(first.Task, Task.Delay(timeout));
            var report = finished == first.Task
                ? first.Task.Result
                : new NavigationReport(view.Url?.Host ?? url.Host, false, NavigationReport.SafeTitle(view.Url?.Host ?? "", view.Title),
                    null, $"timed out after {timeout.TotalSeconds:0}s", view.Url?.Path ?? "/");

            // Let the page's own scripts finish any token request before the view goes away.
            await Task.Delay(TimeSpan.FromSeconds(8));
            view.StopLoading();
            view.NavigationDelegate = null!;
            GC.KeepAlive(nav);
            return report;
        });

    /// <summary>
    /// WebKit won't remove a store its network process still holds, so sign-out happens in two
    /// steps: wipe the store's data now (that works while it's in use), switch to a new store, and
    /// remove the old store's folder at the next start-up, before anything opens it.
    /// </summary>
    public Task<string> DeleteProfileAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var wiped = new TaskCompletionSource();
        Store.RemoveDataOfTypes(WKWebsiteDataStore.AllWebsiteDataTypes, NSDate.DistantPast, () => wiped.TrySetResult());
        await wiped.Task;
        var left = (await AllCookiesAsync()).Length;

        Preferences.Default.Set(PendingRemovalKey, Id.AsString());
        Preferences.Default.Remove(StoreKey);
        _id = null;
        _store = null;
        return $"store data wiped ({left} cookies left). The old store's folder is removed at the next start-up; " +
               "the next sign-in uses a new, empty store.";
    });

    public Task<string?> CleanUpAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var pending = Preferences.Default.Get<string?>(PendingRemovalKey, null);
        if (pending is null || !Guid.TryParse(pending, out _)) return (string?)null;

        var done = new TaskCompletionSource<NSError?>();
        WKWebsiteDataStore.Remove(new NSUuid(pending), error => done.TrySetResult(error));
        var error = await done.Task;
        if (error is not null)
            return $"Couldn't remove the old sign-in store at start-up ({error.Domain} {error.Code}: {error.LocalizedDescription}).";

        Preferences.Default.Remove(PendingRemovalKey);
        return "Removed the old sign-in store at start-up.";
    });
}

/// <summary>Reports each finished or failed main-frame navigation.</summary>
internal sealed class NavigationDelegate(
    Action<NavigationReport> report,
    Action<string, string>? request = null,
    Func<Uri, Uri?>? rewrite = null,
    Action<string>? onRewrite = null)
    : WKNavigationDelegate
{
    // WebKit asks here for every main-frame navigation, server redirects included.
    public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction,
        Action<WKNavigationActionPolicy> decisionHandler)
    {
        if (navigationAction.TargetFrame?.MainFrame != false &&
            Uri.TryCreate(navigationAction.Request.Url?.AbsoluteString, UriKind.Absolute, out var u))
        {
            request?.Invoke(u.Host, SilentAuth.Describe(u)[u.Host.Length..]);
            if (rewrite?.Invoke(u) is { } replacement)
            {
                decisionHandler(WKNavigationActionPolicy.Cancel);
                onRewrite?.Invoke(SilentAuth.Describe(replacement));
                webView.LoadRequest(new NSUrlRequest(new NSUrl(replacement.AbsoluteUri)));
                return;
            }
        }
        decisionHandler(WKNavigationActionPolicy.Allow);
    }

    public override void DidReceiveServerRedirectForProvisionalNavigation(WKWebView webView, WKNavigation navigation)
    {
        if (webView.Url is { } u) request?.Invoke(u.Host ?? "?", (u.Path ?? "/") + " (redirect)");
    }

    public override async void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
    {
        var host = webView.Url?.Host ?? "?";
        string? userAgent = null;
        try { userAgent = (await webView.EvaluateJavaScriptAsync("navigator.userAgent"))?.ToString(); }
        catch (NSErrorException) { }
        report(new NavigationReport(host, true, NavigationReport.SafeTitle(host, webView.Title), userAgent, null,
            webView.Url?.Path ?? "/"));
    }

    public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error)
    {
        if (!IsCancellation(error))
            report(new NavigationReport(webView.Url?.Host ?? "?", false, null, null, $"{error.Domain} {error.Code}"));
    }

    public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error)
    {
        if (!IsCancellation(error))
            report(new NavigationReport(webView.Url?.Host ?? "?", false, null, null, $"{error.Domain} {error.Code}"));
    }

    // A navigation we cancelled (to rewrite it) or that a newer one replaced: not a failure.
    private static bool IsCancellation(NSError e) =>
        (e.Domain == "WebKitErrorDomain" && e.Code == 102) || (e.Domain == "NSURLErrorDomain" && e.Code == -999);
}

/// <summary>Opens pop-ups (window.open, target=_blank) in the same view.</summary>
internal sealed class PopupDelegate : WKUIDelegate
{
    public override WKWebView CreateWebView(WKWebView webView, WKWebViewConfiguration configuration,
        WKNavigationAction navigationAction, WKWindowFeatures windowFeatures)
    {
        webView.LoadRequest(navigationAction.Request);
        return null!;
    }
}
