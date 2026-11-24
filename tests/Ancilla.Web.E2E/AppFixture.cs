using System.Net;
using Ancilla.Core;
using Ancilla.Data;
using Ancilla.Testing;
using Ancilla.Theater;
using Ancilla.UI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;

namespace Ancilla.Web.E2E;

/// <summary>
/// The real app on a random loopback port. Its outgoing HTTP goes to a <see cref="FakeHalo"/>
/// instead of halowaypoint.com, and <c>HALO_SPARTAN_TOKEN</c> is the fake's token, so every new
/// page load signs in as Noble Six the way a developer's local setup does. Snapshots, films, the
/// archive, logs and settings go to a temporary folder. One app and one browser are shared by every test.
/// </summary>
public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ancilla-e2e-" + Guid.NewGuid().ToString("N"));
    private IPlaywright? _playwright;

    public FakeHalo Halo { get; } = new();
    /// <summary>Where the app writes its log during the tests.</summary>
    public string LogFolder => Path.Combine(_root, "logs");
    public Uri BaseUri { get; private set; } = null!;
    public IBrowser Browser { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection([new("HALO_SPARTAN_TOKEN", Spartans.Token)]));
        builder.ConfigureTestServices(services =>
        {
            // Every outgoing request lands on the fake. A new handler each time: the factory disposes them.
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(Halo.CreateHandler));
            // Replace the logging the app set up, so the log lands in the temporary folder.
            services.RemoveAll<DiagnosticLog>();
            services.RemoveAll<HaloDiagnostics>();
            services.AddSingleton(sp => new DiagnosticLog(LogFolder, sp.GetRequiredService<Redactor>()));
            services.AddSingleton(sp => new HaloDiagnostics(sp.GetRequiredService<Redactor>(), sp.GetRequiredService<DiagnosticLog>()));
            services.AddSingleton(new SnapshotStore(Path.Combine(_root, "snapshots.json")));
            services.AddSingleton(new FilmCache(Path.Combine(_root, "films")));
            services.RemoveAll<ArchiveStore>();
            // A factory, so the host disposes it when it stops and SQLite closes archive.db before the
            // folder is deleted (Windows can't delete a file that's still open).
            services.AddSingleton(_ => new ArchiveStore(Path.Combine(_root, "archive.db")));
            // Settings and the list of what's stored (which can delete it all) stay in the temporary folder too.
            services.RemoveAll<LocalSettings>();
            services.RemoveAll<LocalData>();
            services.AddSingleton(new LocalSettings(Path.Combine(_root, "settings.json")));
            services.AddSingleton(sp => new LocalData(sp.GetRequiredService<ArchiveStore>(), sp.GetRequiredService<SnapshotStore>(),
                sp.GetRequiredService<FilmCache>(), sp.GetRequiredService<DiagnosticLog>(), sp.GetRequiredService<LocalSettings>(),
                sp.GetRequiredService<IWaypointSignIn>(), _root));
        });
    }

    public async Task InitializeAsync()
    {
        UseKestrel(k => k.Listen(IPAddress.Loopback, 0));
        StartServer();
        var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        BaseUri = new Uri(addresses.Single().TrimEnd('/') + "/");

        // Downloads Chromium the first time; a no-op once it's there (CI installs it beforehand).
        var exit = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exit != 0) throw new InvalidOperationException($"Playwright couldn't install Chromium (exit code {exit}).");
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync();
    }

    /// <summary>A fresh browser context (cookies, storage) and page, kept offline apart from the app.</summary>
    public async Task<IPage> NewPageAsync()
    {
        var context = await Browser.NewContextAsync(new() { BaseURL = BaseUri.ToString(), Locale = "en-GB" });
        context.SetDefaultTimeout(15_000);
        // The only outside request the app makes is for Google Fonts; tests don't need it.
        await context.RouteAsync(url => !url.StartsWith(BaseUri.ToString(), StringComparison.Ordinal), route => route.AbortAsync());
        return await context.NewPageAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
        await DisposeAsync();
        // CI sets this so the privacy check can scan the log these tests produced.
        if (Environment.GetEnvironmentVariable("ANCILLA_E2E_LOGS") is { Length: > 0 } keep && Directory.Exists(LogFolder))
        {
            Directory.CreateDirectory(keep);
            foreach (var f in Directory.GetFiles(LogFolder)) File.Copy(f, Path.Combine(keep, Path.GetFileName(f)), overwrite: true);
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "app";
}
