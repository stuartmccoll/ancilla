using Ancilla.App.SignIn;
using CoreGraphics;
using Foundation;
using WebKit;

namespace Ancilla.App.Platforms;

/// <summary>
/// macOS: with "Stay signed in", a <see cref="WKWebsiteDataStore"/> with its own identifier, inside
/// the app's sandbox container, apart from the BlazorWebView's default store. Without it, a
/// non-persistent store that WebKit keeps in memory only.
/// </summary>
public sealed class WebProfile : IWebProfile
{
    private const string StoreKey = "signin-store-id";
    private const string PendingRemovalKey = "signin-store-remove";
    private const string TokenCookie = "343-spartan-token";

    private bool _persistent;
    private WKWebsiteDataStore? _store;

    private static NSUuid StoreId()
    {
        var saved = Preferences.Default.Get(StoreKey, "");
        if (!Guid.TryParse(saved, out _))
        {
            saved = Guid.NewGuid().ToString();
            Preferences.Default.Set(StoreKey, saved);
        }
        return new NSUuid(saved);
    }

    // Main thread only. One store for the app's life, so an in-memory session lasts while the app runs.
    private WKWebsiteDataStore Store => _store ??= _persistent ? WKWebsiteDataStore.Create(StoreId()) : WKWebsiteDataStore.NonPersistentDataStore;

    internal WKWebViewConfiguration CreateConfiguration() => new() { WebsiteDataStore = Store };

    public Task UsePersistentAsync(bool persistent) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (persistent == _persistent && _store is not null) return;
        if (!persistent) await ForgetPersistentAsync();   // turning "Stay signed in" off
        _persistent = persistent;
        _store = null;
    });

    public Task<string?> ReadTokenAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
        (await AllCookiesAsync()).FirstOrDefault(IsToken)?.Value);

    public Task DeleteTokenAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        foreach (var c in (await AllCookiesAsync()).Where(IsToken))
        {
            var done = new TaskCompletionSource();
            Store.HttpCookieStore.DeleteCookie(c, () => done.TrySetResult());
            await done.Task;
        }
    });

    private static bool IsToken(NSHttpCookie c) => c.Name == TokenCookie && SilentAuth.IsWaypoint(c.Domain.TrimStart('.'));

    private Task<NSHttpCookie[]> AllCookiesAsync()
    {
        var done = new TaskCompletionSource<NSHttpCookie[]>();
        Store.HttpCookieStore.GetAllCookies(cookies => done.TrySetResult(cookies));
        return done.Task;
    }

    public Task<bool> LoadHiddenAsync(Uri url, TimeSpan timeout) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        // Never added to a window: nobody sees it.
        using var view = new WKWebView(new CGRect(0, 0, 1024, 768), CreateConfiguration());
        var back = new TaskCompletionSource<bool>();
        var leftWaypoint = false;
        var nav = new NavigationDelegate(
            host =>
            {
                if (!SilentAuth.IsWaypoint(host)) leftWaypoint = true;
                else if (leftWaypoint) back.TrySetResult(true);
            },
            () => back.TrySetResult(false),
            SilentAuth.WithPromptNone);
        view.NavigationDelegate = nav;
        view.LoadRequest(new NSUrlRequest(new NSUrl(url.AbsoluteUri)));

        var returned = await Task.WhenAny(back.Task, Task.Delay(timeout)) == back.Task && back.Task.Result;
        view.StopLoading();
        view.NavigationDelegate = null!;
        GC.KeepAlive(nav);
        return returned;
    });

    /// <summary>
    /// WebKit won't remove a store its network process still holds, so this wipes the store's data
    /// now (that works while it's in use) and, for a persistent store, removes its folder at the
    /// next start-up, before anything opens it. The next sign-in uses a new, empty store.
    /// </summary>
    public Task ClearAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (!_persistent && _store is not null) await WipeAsync(_store);
        await ForgetPersistentAsync();
        _store = null;
    });

    // Main thread. Wipes the persistent store, if there is one, and queues its folder for removal.
    private async Task ForgetPersistentAsync()
    {
        if (Preferences.Default.Get(StoreKey, "") is not { Length: > 0 } id || !Guid.TryParse(id, out _)) return;
        await WipeAsync(_persistent && _store is not null ? _store : WKWebsiteDataStore.Create(new NSUuid(id)));
        QueueRemoval(id);
        Preferences.Default.Remove(StoreKey);
        if (_persistent) _store = null;
    }

    private static void QueueRemoval(string id) =>
        Preferences.Default.Set(PendingRemovalKey, string.Join(",", PendingRemovals().Append(id).Distinct()));

    private static IEnumerable<string> PendingRemovals() =>
        Preferences.Default.Get(PendingRemovalKey, "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Where(id => Guid.TryParse(id, out _));

    private static Task WipeAsync(WKWebsiteDataStore store)
    {
        var wiped = new TaskCompletionSource();
        store.RemoveDataOfTypes(WKWebsiteDataStore.AllWebsiteDataTypes, NSDate.DistantPast, () => wiped.TrySetResult());
        return wiped.Task;
    }

    public Task CleanUpAsync(bool persistent) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        // Without "Stay signed in" nothing should be on disk, even if an earlier run kept something.
        if (!persistent && Preferences.Default.Get(StoreKey, "") is { Length: > 0 } kept && Guid.TryParse(kept, out _))
        {
            QueueRemoval(kept);
            Preferences.Default.Remove(StoreKey);
        }

        var left = new List<string>();
        foreach (var id in PendingRemovals())
        {
            var done = new TaskCompletionSource<NSError?>();
            WKWebsiteDataStore.Remove(new NSUuid(id), error => done.TrySetResult(error));
            if (await done.Task is not null) left.Add(id);   // tried again at the next start-up
        }
        if (left.Count == 0) Preferences.Default.Remove(PendingRemovalKey);
        else Preferences.Default.Set(PendingRemovalKey, string.Join(",", left));
    });
}

/// <summary>Reports where each main-frame page load ended up, and can rewrite requests before they go.</summary>
internal sealed class NavigationDelegate(Action<string> loaded, Action? failed = null, Func<Uri, Uri?>? rewrite = null)
    : WKNavigationDelegate
{
    // WebKit asks here for every main-frame navigation, server redirects included.
    public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction,
        Action<WKNavigationActionPolicy> decisionHandler)
    {
        if (rewrite is not null && navigationAction.TargetFrame?.MainFrame != false &&
            Uri.TryCreate(navigationAction.Request.Url?.AbsoluteString, UriKind.Absolute, out var u))
        {
            if (!SilentAuth.IsWaypoint(u.Host)) loaded(u.Host);   // a trip to Microsoft, redirects included
            if (rewrite(u) is { } replacement)
            {
                decisionHandler(WKNavigationActionPolicy.Cancel);
                webView.LoadRequest(new NSUrlRequest(new NSUrl(replacement.AbsoluteUri)));
                return;
            }
        }
        decisionHandler(WKNavigationActionPolicy.Allow);
    }

    public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation) =>
        loaded(webView.Url?.Host ?? "");

    public override void DidFailNavigation(WKWebView webView, WKNavigation navigation, NSError error)
    {
        if (!IsCancellation(error)) failed?.Invoke();
    }

    public override void DidFailProvisionalNavigation(WKWebView webView, WKNavigation navigation, NSError error)
    {
        if (!IsCancellation(error)) failed?.Invoke();
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
