using Microsoft.AspNetCore.Components.WebView.Maui;

namespace SignInSpike;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureMauiHandlers(h => h.AddHandler<SignInView, SignInViewHandler>());

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        builder.Services.AddSingleton(new HttpClient());
        builder.Services.AddSingleton<SpikeState>();
        builder.Services.AddSingleton<IWebProfile, WebProfile>();
        builder.Services.AddSingleton<SpikeService>();

        return builder.Build();
    }
}

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = "Ancilla sign-in spike", Width = 1000, Height = 900 };
}

public sealed class MainPage : ContentPage
{
    public MainPage()
    {
        var blazor = new BlazorWebView { HostPage = "wwwroot/index.html" };
        blazor.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Components.Main) });
        Content = blazor;
    }
}
