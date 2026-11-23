using Ancilla.App.SignIn;
using Microsoft.Web.WebView2.Core;

namespace Ancilla.App.Platforms;

/// <summary>
/// Windows: a WebView2 environment on a user data folder of its own, apart from the BlazorWebView's.
/// With "Stay signed in", web views use its normal profile, which keeps Microsoft's sign-in in that
/// folder (Chromium encrypts cookies there with a key Windows protects for your account). Without
/// it, they're InPrivate, which keeps browsing data in memory, and the folder is removed at sign-out
/// and at the next start-up. A hidden controller (never shown) holds the cookie manager.
/// </summary>
public sealed class WebProfile : IWebProfile
{
    private const string TokenCookie = "343-spartan-token";
    private const string Waypoint = "https://www.halowaypoint.com";

    internal static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ancilla", "SignIn");

    private Task<CoreWebView2Environment>? _environment;
    private CoreWebView2Controller? _cookieController;
    private bool _persistent;

    // Main thread only.
    internal Task<CoreWebView2Environment> GetEnvironmentAsync() => _environment ??= CreateEnvironmentAsync();

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync() =>
        await CoreWebView2Environment.CreateWithOptionsAsync("", Folder, new CoreWebView2EnvironmentOptions());

    internal async Task<CoreWebView2ControllerOptions> ControllerOptionsAsync()
    {
        var options = (await GetEnvironmentAsync()).CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = !_persistent;
        return options;
    }

    private async Task<CoreWebView2Controller> CreateHiddenControllerAsync()
    {
        var environment = await GetEnvironmentAsync();
        var window = (Microsoft.UI.Xaml.Window)Application.Current!.Windows[0].Handler!.PlatformView!;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var controller = await environment.CreateCoreWebView2ControllerAsync(
            CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)hwnd), await ControllerOptionsAsync());
        controller.IsVisible = false;
        return controller;
    }

    // The cookie controller also keeps an InPrivate session alive between sign-ins while the app runs.
    private async Task<CoreWebView2CookieManager> CookiesAsync()
    {
        _cookieController ??= await CreateHiddenControllerAsync();
        return _cookieController.CoreWebView2.CookieManager;
    }

    public Task UsePersistentAsync(bool persistent) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (persistent == _persistent) return;
        if (!persistent) await ClearProfileAsync();   // turning "Stay signed in" off
        _cookieController?.Close();
        _cookieController = null;
        _persistent = persistent;
    });

    public Task<string?> ReadTokenAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
        (await (await CookiesAsync()).GetCookiesAsync(Waypoint)).FirstOrDefault(c => c.Name == TokenCookie)?.Value);

    public Task DeleteTokenAsync() => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var manager = await CookiesAsync();
        foreach (var c in (await manager.GetCookiesAsync(Waypoint)).Where(c => c.Name == TokenCookie))
            manager.DeleteCookie(c);
    });

    public Task<bool> LoadHiddenAsync(Uri url, TimeSpan timeout) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        var controller = await CreateHiddenControllerAsync();
        try
        {
            var core = controller.CoreWebView2;
            var back = new TaskCompletionSource<bool>();
            var leftWaypoint = false;
            core.NavigationStarting += (s, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)) return;
                // WebView2 completes a redirect chain as one navigation, so the trip to Microsoft
                // only shows up here, never in NavigationCompleted.
                if (!SilentAuth.IsWaypoint(u.Host)) leftWaypoint = true;
                if (SilentAuth.WithPromptNone(u) is { } replacement)
                {
                    e.Cancel = true;
                    s.Navigate(replacement.AbsoluteUri);
                }
            };
            core.NavigationCompleted += (s, e) =>
            {
                if (IsCancellation(e)) return;
                if (!e.IsSuccess) back.TrySetResult(false);
                else if (leftWaypoint && Uri.TryCreate(s.Source, UriKind.Absolute, out var u) && SilentAuth.IsWaypoint(u.Host))
                    back.TrySetResult(true);
            };
            core.Navigate(url.AbsoluteUri);
            return await Task.WhenAny(back.Task, Task.Delay(timeout)) == back.Task && back.Task.Result;
        }
        finally
        {
            controller.Close();
        }
    });

    // A navigation we cancelled (to rewrite it) or that a newer one replaced: not a failure.
    internal static bool IsCancellation(CoreWebView2NavigationCompletedEventArgs e) =>
        !e.IsSuccess && e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled;

    public Task ClearAsync() => MainThread.InvokeOnMainThreadAsync(ClearProfileAsync);

    // Main thread. The folder is locked until every web view on it is closed and the browser process exits.
    private async Task ClearProfileAsync()
    {
        if (_cookieController is { } controller)
        {
            try { await controller.CoreWebView2.Profile.ClearBrowsingDataAsync(); }
            catch (Exception) { /* the folder goes next anyway */ }
        }
        if (_environment is not null)
        {
            var environment = await _environment;
            var exited = new TaskCompletionSource();
            environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
            _cookieController?.Close();
            _cookieController = null;
            _environment = null;
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        }
        await DeleteFolderAsync();
    }

    private static async Task DeleteFolderAsync()
    {
        for (var attempt = 1; Directory.Exists(Folder); attempt++)
        {
            try { Directory.Delete(Folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;   // still locked: the next start-up removes it, before anything opens it
            }
        }
    }

    public Task CleanUpAsync(bool persistent) =>
        persistent ? Task.CompletedTask : MainThread.InvokeOnMainThreadAsync(DeleteFolderAsync);
}
