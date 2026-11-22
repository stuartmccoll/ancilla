using Ancilla.Core;
using Ancilla.UI.Services;
using Microsoft.Extensions.Configuration;

namespace Ancilla.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Only the token is read from the environment, as the web host does; the folder settings keep
        // their defaults. It signs in whoever runs the app, which on a desktop is just you.
        builder.Configuration.AddInMemoryCollection(
            [new("HALO_SPARTAN_TOKEN", Environment.GetEnvironmentVariable("HALO_SPARTAN_TOKEN"))]);

        builder.Services.AddMauiBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif

        builder.Services.AddAncilla(builder.Configuration);
        // One window, one player: the session lives as long as the app.
        builder.Services.AddSingleton<HaloSession>();
        builder.Services.AddSingleton<IThemePreference, SettingsThemePreference>();
        builder.Services.AddSingleton<IFileExport, DownloadsFileExport>();
        builder.Services.AddSingleton<MainPage>();

        var app = builder.Build();
        app.Services.GetRequiredService<HaloDiagnostics>().Info($"Ancilla {DiagnosticsReport.AppVersion} started");
        return app;
    }
}
