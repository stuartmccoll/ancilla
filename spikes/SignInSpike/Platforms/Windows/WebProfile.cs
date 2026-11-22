using Microsoft.Web.WebView2.Core;

namespace SignInSpike;

/// <summary>
/// Windows: a WebView2 environment on a user data folder the spike chooses, apart from the
/// BlazorWebView's. A hidden controller (never shown) gives access to its cookie manager.
/// </summary>
public sealed class WebProfile : IWebProfile
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AncillaSignInSpike", "WebView2");

    private Task<CoreWebView2Environment>? _environment;
    private CoreWebView2Controller? _cookieController;

    public string Description =>
        "WebView2 user data folder " +
        Folder.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~", StringComparison.OrdinalIgnoreCase);

    public Task<CoreWebView2Environment> GetEnvironmentAsync() => _environment ??= CreateEnvironmentAsync();

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync() =>
        await CoreWebView2Environment.CreateWithOptionsAsync("", Folder, new CoreWebView2EnvironmentOptions());

    private async Task<CoreWebView2Controller> CreateHiddenControllerAsync()
    {
        var environment = await GetEnvironmentAsync();
        var window = (Microsoft.UI.Xaml.Window)Application.Current!.Windows[0].Handler!.PlatformView!;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var controller = await environment.CreateCoreWebView2ControllerAsync(
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd));
        controller.IsVisible = false;
        return controller;
    }

    private async Task<CoreWebView2CookieManager> CookiesAsync()
    {
        _cookieController ??= await CreateHiddenControllerAsync();
        return _cookieController.CoreWebView2.CookieManager;
    }

    public Task<IReadOnlyList<WebCookie>> GetCookiesAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var manager = await CookiesAsync();
        var cookies = await manager.GetCookiesAsync(SpikeService.Waypoint.AbsoluteUri);
        return (IReadOnlyList<WebCookie>)cookies.Select(c => new WebCookie(
            c.Name, c.Value, c.Domain, c.Path,
            c.IsSession ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)(c.Expires * 1000)),
            c.IsHttpOnly, c.IsSecure)).ToList();
    });

    public Task DeleteCookieAsync(WebCookie cookie) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var manager = await CookiesAsync();
        manager.DeleteCookiesWithDomainAndPath(cookie.Name, cookie.Domain, cookie.Path);
    });

    public Task<NavigationReport> LoadHiddenAsync(Uri url, string? userAgent, TimeSpan timeout, bool followSignIn,
        Action<string> onStep) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            var controller = await CreateHiddenControllerAsync();
            try
            {
                var core = controller.CoreWebView2;
                if (userAgent is not null) core.Settings.UserAgent = userAgent;

                var first = new TaskCompletionSource<NavigationReport>();
                var leftWaypoint = false;
                core.NavigationStarting += (s, e) =>
                {
                    if (SignInViewHandler.Request(e) is { } r)
                    {
                        onStep($"request {r.Host}{r.Path}");
                        // WebView2 completes a redirect chain as one navigation, so the trip to
                        // Microsoft only shows up here, never in NavigationCompleted.
                        if (!NavigationReport.IsWaypoint(r.Host)) leftWaypoint = true;
                    }
                    if (followSignIn && Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) &&
                        SilentAuth.WithPromptNone(u) is { } replacement)
                    {
                        e.Cancel = true;
                        onStep($"rewrote prompt to none: {SilentAuth.Describe(replacement)}");
                        s.Navigate(replacement.AbsoluteUri);
                    }
                };
                core.NavigationCompleted += (s, e) =>
                {
                    if (SignInViewHandler.IsCancellation(e)) return;
                    var r = SignInViewHandler.Report(s, e);
                    if (!NavigationReport.IsWaypoint(r.Host)) leftWaypoint = true;
                    if (!followSignIn || !r.Succeeded || (leftWaypoint && NavigationReport.IsWaypoint(r.Host)))
                        first.TrySetResult(r);
                    else
                        onStep($"finished {r.Host}{r.Path}{(r.Title is { } t ? $" \u201c{t}\u201d" : "")}");
                };
                core.Navigate(url.AbsoluteUri);

                var finished = await Task.WhenAny(first.Task, Task.Delay(timeout));
                var report = finished == first.Task
                    ? first.Task.Result
                    : new NavigationReport(url.Host, false, null, null, $"timed out after {timeout.TotalSeconds:0}s");

                // Let the page's own scripts finish any token request before the view goes away.
                await Task.Delay(TimeSpan.FromSeconds(8));
                return report;
            }
            finally
            {
                controller.Close();
            }
        });

    public Task<string?> CleanUpAsync() => Task.FromResult<string?>(null);

    public Task<string> DeleteProfileAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (_environment is not null)
        {
            // The folder is locked until every web view on it is closed and the browser process exits.
            var environment = await _environment;
            var exited = new TaskCompletionSource();
            environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
            _cookieController?.Close();
            _cookieController = null;
            _environment = null;
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        if (!Directory.Exists(Folder)) return "nothing to delete.";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(Folder, recursive: true);
                return $"folder deleted (attempt {attempt}). The next sign-in uses a new, empty profile.";
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"folder still locked ({ex.GetType().Name}). Quit the spike and delete it by hand.";
            }
        }
    });
}
