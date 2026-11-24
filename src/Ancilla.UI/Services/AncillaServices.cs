using Ancilla.Core;
using Ancilla.Data;
using Ancilla.Theater;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ancilla.UI.Services;

public static class AncillaServices
{
    /// <summary>
    /// The services both hosts share. Each host adds its own <see cref="HaloSession"/> (one per
    /// circuit on the web host, one for the app in the desktop shell), <see cref="IThemePreference"/>,
    /// <see cref="IFileExport"/> and <see cref="IWaypointSignIn"/>.
    /// </summary>
    public static IServiceCollection AddAncilla(this IServiceCollection services, IConfiguration config)
    {
        // Halo API calls run in the app's .NET process, so the Spartan token never reaches page
        // scripts and there are no CORS issues with halowaypoint.com. Automatic redirects are off: a
        // redirected request would carry the token's header to whatever host it was sent to.
        services.AddHttpClient();
        services.ConfigureHttpClientDefaults(http =>
            http.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false }));
        // The old progress snapshots file. Its snapshots are imported into the archive once, then it is left alone.
        services.AddSingleton(new SnapshotStore(config["Snapshots"] ?? SnapshotStore.DefaultPath));
        // Every match, skill result and snapshot you load, in SQLite on this machine (see About > What's stored).
        // Made by the container (not passed in ready-made) so the container disposes it on shutdown, which
        // clears its connection pool and lets go of the file. It also isn't opened if a test replaces it.
        services.AddSingleton(_ => new ArchiveStore(config["Archive"] ?? ArchiveStore.DefaultPath));
        // Theater films (9–30 MB each, every player's gamertag and XUID) are cached on disk outside the
        // repo, in the same folder Ancilla.Theater.Cli uses, and downloaded only when a timeline is asked for.
        services.AddSingleton(new FilmCache(config["Films"] ?? FilmCache.DefaultRoot));
        // Local log files with tokens, XUIDs, gamertags and paths redacted. See About > Your token and privacy.
        var redactor = new Redactor();
        var log = new DiagnosticLog(config["Logs"] ?? DiagnosticLog.DefaultFolder, redactor);
        services.AddSingleton(redactor);
        services.AddSingleton(log);
        services.AddSingleton(new HaloDiagnostics(redactor, log));
        // Errors logged through ILogger (Blazor's unhandled exceptions among them) go to the same files.
        services.AddSingleton<ILoggerProvider>(new DiagnosticLoggerProvider(log));
        services.AddSingleton<DiagnosticsReport>();
        // Choices made on the Settings page (the film cache limit), next to the archive.
        services.AddSingleton(new LocalSettings(config["Settings"] ?? LocalSettings.DefaultPath));
        // Lists, exports and deletes all of the above. Only Ancilla's own folder is ever removed.
        services.AddSingleton(sp => new LocalData(sp.GetRequiredService<ArchiveStore>(), sp.GetRequiredService<SnapshotStore>(),
            sp.GetRequiredService<FilmCache>(), log, sp.GetRequiredService<LocalSettings>(), sp.GetRequiredService<IWaypointSignIn>(),
            LocalData.DefaultFolder));
        // The theme this circuit or window shows, so the menu's picker and Settings' agree.
        services.AddScoped<ThemeChoice>();
        services.AddSingleton<HaloCache>();
        services.AddSingleton<AssetCache>();
        return services;
    }
}
