using Ancilla.UI.Components;
using Ancilla.UI.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.FileProviders;

namespace Ancilla.App;

/// <summary>The window's only page: a web view showing Ancilla.UI's router.</summary>
public sealed class MainPage : ContentPage
{
    public MainPage(AssetCache assets)
    {
        var view = new AncillaWebView(assets) { HostPage = "wwwroot/index.html" };
        view.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Routes) });
        Content = view;
    }

    /// <summary>
    /// The web view has no HTTP server, so token-gated Halo images (which the web host serves from
    /// <c>/assets</c>) come from the same in-memory <see cref="AssetCache"/> through a file provider.
    /// </summary>
    private sealed class AncillaWebView(AssetCache assets) : BlazorWebView
    {
        public override IFileProvider CreateFileProvider(string contentRootDir) =>
            new CompositeFileProvider(new AssetFileProvider(assets), base.CreateFileProvider(contentRootDir));
    }
}
