using Ancilla.App.SignIn;
using CoreGraphics;
using Foundation;
using Microsoft.Maui.Handlers;
using WebKit;

namespace Ancilla.App.Platforms;

/// <summary>The sign-in page's WKWebView, on the sign-in profile's store.</summary>
public sealed class SignInViewHandler() : ViewHandler<SignInView, WKWebView>(ViewMapper)
{
    // WebKit holds its delegates weakly; keep them alive here.
    private NavigationDelegate? _navigation;
    private PopupDelegate? _popups;

    protected override WKWebView CreatePlatformView()
    {
        var profile = (WebProfile)MauiContext!.Services.GetRequiredService<IWebProfile>();
        return new WKWebView(CGRect.Empty, profile.CreateConfiguration());
    }

    protected override void ConnectHandler(WKWebView platformView)
    {
        base.ConnectHandler(platformView);
        platformView.NavigationDelegate = _navigation = new NavigationDelegate(host => VirtualView?.ReportLoaded(host));
        platformView.UIDelegate = _popups = new PopupDelegate();
        platformView.LoadRequest(new NSUrlRequest(new NSUrl(VirtualView.StartUrl.AbsoluteUri)));
    }

    protected override void DisconnectHandler(WKWebView platformView)
    {
        platformView.StopLoading();
        platformView.NavigationDelegate = null!;
        platformView.UIDelegate = null!;
        _navigation = null;
        _popups = null;
        base.DisconnectHandler(platformView);
    }
}
