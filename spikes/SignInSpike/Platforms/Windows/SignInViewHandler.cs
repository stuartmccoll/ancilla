using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace SignInSpike;

public sealed class SignInViewHandler() : ViewHandler<SignInView, WebView2>(ViewMapper)
{
    protected override WebView2 CreatePlatformView() => new();

    protected override void ConnectHandler(WebView2 platformView)
    {
        base.ConnectHandler(platformView);
        _ = InitialiseAsync(platformView);
    }

    private async Task InitialiseAsync(WebView2 view)
    {
        try
        {
            var profile = (WebProfile)MauiContext!.Services.GetRequiredService<IWebProfile>();
            await view.EnsureCoreWebView2Async(await profile.GetEnvironmentAsync());
            var core = view.CoreWebView2;
            if (VirtualView.UserAgent is { } ua) core.Settings.UserAgent = ua;

            // Pop-ups (window.open, target=_blank) open in the same view.
            core.NewWindowRequested += (s, e) => { e.Handled = true; s.Navigate(e.Uri); };
            core.NavigationStarting += (_, e) =>
            {
                if (Request(e) is { } r) VirtualView?.ReportRequest(r.Host, r.Path);
            };
            core.NavigationCompleted += (s, e) =>
            {
                if (!IsCancellation(e)) VirtualView?.ReportNavigation(Report(s, e));
            };
            core.Navigate(VirtualView.StartUrl.AbsoluteUri);
        }
        catch (Exception ex)
        {
            VirtualView?.ReportNavigation(new NavigationReport("(start)", false, null, null, ex.GetType().Name));
        }
    }

    internal static NavigationReport Report(CoreWebView2 core, CoreWebView2NavigationCompletedEventArgs e)
    {
        Uri.TryCreate(core.Source, UriKind.Absolute, out var u);
        var host = u?.Host ?? "?";
        return new NavigationReport(host, e.IsSuccess, NavigationReport.SafeTitle(host, core.DocumentTitle),
            core.Settings.UserAgent, e.IsSuccess ? null : e.WebErrorStatus.ToString(), u?.AbsolutePath ?? "/");
    }

    // A navigation we cancelled (to rewrite it) or that a newer one replaced: not a failure.
    internal static bool IsCancellation(CoreWebView2NavigationCompletedEventArgs e) =>
        !e.IsSuccess && e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled;

    internal static (string Host, string Path)? Request(CoreWebView2NavigationStartingEventArgs e) =>
        Uri.TryCreate(e.Uri, UriKind.Absolute, out var u)
            ? (u.Host, SilentAuth.Describe(u)[u.Host.Length..] + (e.IsRedirected ? " (redirect)" : ""))
            : null;

    protected override void DisconnectHandler(WebView2 platformView)
    {
        platformView.Close();
        base.DisconnectHandler(platformView);
    }
}
