using Ancilla.Core;
using Ancilla.Data;
using Ancilla.Theater;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ancilla.UI.Services;

public static class AncillaServices
{
    /// <summary>
    /// The services both hosts share. Each host adds its own <see cref="HaloSession"/> (one per
    /// circuit on the web host, one for the app in the desktop shell), <see cref="IThemePreference"/>
    /// and <see cref="IFileExport"/>.
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
        services.AddSingleton(new ArchiveStore(config["Archive"] ?? ArchiveStore.DefaultPath));
        // Theater films (9–30 MB each, every player's gamertag and XUID) are cached on disk outside the
        // repo, in the same folder Ancilla.Theater.Cli uses, and downloaded only when a timeline is asked for.
        services.AddSingleton(new FilmCache(config["Films"] ?? FilmCache.DefaultRoot));
        // Local log files with tokens, XUIDs, gamertags and paths redacted. See About > Your token and privacy.
        var redactor = new Redactor();
        var log = new DiagnosticLog(config["Logs"] ?? DiagnosticLog.DefaultFolder, redactor);
        services.AddSingleton(redactor);
        services.AddSingleton(log);
        services.AddSingleton(new HaloDiagnostics(redactor, log));
        services.AddSingleton<DiagnosticsReport>();
        services.AddSingleton<HaloCache>();
        services.AddSingleton<AssetCache>();
        return services;
    }
}
