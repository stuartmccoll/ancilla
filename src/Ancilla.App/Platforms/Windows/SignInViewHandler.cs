using Ancilla.App.SignIn;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;

namespace Ancilla.App.Platforms;

/// <summary>The sign-in page's WebView2, on the sign-in profile's environment (InPrivate without "Stay signed in").</summary>
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
            await view.EnsureCoreWebView2Async(await profile.GetEnvironmentAsync(), await profile.ControllerOptionsAsync());
            var core = view.CoreWebView2;
            // Pop-ups (window.open, target=_blank) open in the same view.
            core.NewWindowRequested += (s, e) => { e.Handled = true; s.Navigate(e.Uri); };
            core.NavigationCompleted += (s, e) =>
            {
                if (e.IsSuccess && Uri.TryCreate(s.Source, UriKind.Absolute, out var u)) VirtualView?.ReportLoaded(u.Host);
            };
            core.Navigate(VirtualView.StartUrl.AbsoluteUri);
        }
        catch (Exception)
        {
            // The page stays blank; Cancel still closes it, and pasting a token still works.
        }
    }

    protected override void DisconnectHandler(WebView2 platformView)
    {
        platformView.Close();
        base.DisconnectHandler(platformView);
    }
}
