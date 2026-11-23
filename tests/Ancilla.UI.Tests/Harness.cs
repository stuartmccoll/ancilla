using Ancilla.Core;
using Ancilla.Data;
using Ancilla.Testing;
using Ancilla.Theater;
using Ancilla.UI.Services;
using Microsoft.Extensions.Configuration;

namespace Ancilla.UI.Tests;

/// <summary>
/// What the web app's singletons would be, wired to a <see cref="FakeHalo"/>, with snapshots and
/// films in a temporary folder that's deleted afterwards.
/// </summary>
public sealed class Harness : IDisposable
{
    public Harness(HaloWorld? world = null, string? envToken = null)
    {
        Halo = new FakeHalo(world);
        Root = Path.Combine(Path.GetTempPath(), "ancilla-tests-" + Guid.NewGuid().ToString("N"));
        Snapshots = new SnapshotStore(Path.Combine(Root, "snapshots.json"));
        Films = new FilmCache(Path.Combine(Root, "films"));
        Archive = new ArchiveStore(Path.Combine(Root, "archive.db"));
        Config = new ConfigurationBuilder()
            .AddInMemoryCollection(envToken is null ? [] : [new("HALO_SPARTAN_TOKEN", envToken)])
            .Build();
    }

    public FakeHalo Halo { get; }
    public string Root { get; }
    public HaloCache Cache { get; } = new();
    public AssetCache Assets { get; } = new();
    public SnapshotStore Snapshots { get; }
    public FilmCache Films { get; }
    public ArchiveStore Archive { get; }
    public IConfiguration Config { get; }
    public Redactor Redactor { get; } = new();
    public DiagnosticLog Log => _log ??= new DiagnosticLog(Path.Combine(Root, "logs"), Redactor);
    private DiagnosticLog? _log;
    public HaloDiagnostics Diagnostics => _diagnostics ??= new HaloDiagnostics(Redactor, Log);
    private HaloDiagnostics? _diagnostics;
    public DiagnosticsReport Report => new(Diagnostics, Cache, Assets, Archive, Films);

    public HaloWorld World => Halo.World;

    /// <summary>The embedded sign-in sessions see. The web host's by default; set a <see cref="FakeWaypointSignIn"/> to act as the desktop app.</summary>
    public IWaypointSignIn Waypoint { get; set; } = new NoWaypointSignIn();

    public HaloSession NewSession() => new(Halo.Factory, Cache, Assets, Films, Config, Diagnostics, Archive, Snapshots, Waypoint) { Retry = new RetryPolicy(4, TimeSpan.FromMilliseconds(1)) };

    public async Task<HaloSession> SignedInAsync()
    {
        var s = NewSession();
        await s.SignInAsync(Spartans.Token);
        return s;
    }

    public void Dispose()
    {
        Archive.Dispose();   // lets go of the database file so the folder can be deleted
        // A late background request may still be writing its log line, so try again.
        for (var attempt = 0; attempt < 5 && Directory.Exists(Root); attempt++)
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { Thread.Sleep(50); }
        }
    }
}
