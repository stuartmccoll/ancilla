using Ancilla.Core;
using Ancilla.Testing;
using Ancilla.Theater;
using Ancilla.Web.Services;
using Microsoft.Extensions.Configuration;

namespace Ancilla.Web.Tests;

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
    public IConfiguration Config { get; }

    public HaloWorld World => Halo.World;

    public HaloSession NewSession() => new(Halo.Factory, Cache, Assets, Films, Config);

    public async Task<HaloSession> SignedInAsync()
    {
        var s = NewSession();
        await s.SignInAsync(Spartans.Token);
        return s;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
