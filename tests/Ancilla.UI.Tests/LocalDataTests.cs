using System.Text.Json;
using Ancilla.Core;
using Ancilla.Testing;
using Ancilla.UI.Services;

namespace Ancilla.UI.Tests;

public sealed class LocalSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ancilla-settings-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Nothing_is_written_until_something_changes()
    {
        var settings = new LocalSettings(File_);
        Assert.Null(settings.FilmCacheLimitMb);
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void A_change_is_kept_for_next_time_and_erasing_forgets_it()
    {
        _ = new LocalSettings(File_) { FilmCacheLimitMb = 512 };
        var again = new LocalSettings(File_);
        Assert.Equal(512, again.FilmCacheLimitMb);

        again.Erase();
        Assert.Null(again.FilmCacheLimitMb);
        Assert.False(File.Exists(File_));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"filmCacheLimitMb": -5}""")]
    public void A_damaged_file_means_the_defaults(string text)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(File_, text);
        Assert.Null(new LocalSettings(File_).FilmCacheLimitMb);
    }

    [Fact]
    public void A_limit_must_be_positive()
    {
        var settings = new LocalSettings(File_);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.FilmCacheLimitMb = 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}

public sealed class LocalDataTests : IDisposable
{
    private readonly Harness _h = new();

    /// <summary>Signs in and fills every kind of file: archive, film, log, settings and the old snapshots file.</summary>
    private async Task<HaloSession> FilledAsync()
    {
        var session = await _h.SignedInAsync();
        await session.GetRecentMatchesAsync(MatchHistoryType.Matchmaking, 5);
        if (session.BackgroundSync is { } sync) await sync;
        var film = _h.Films.MatchDir(_h.World.Games[0].Id);
        Directory.CreateDirectory(film);
        File.WriteAllBytes(Path.Combine(film, "chunk000-type1.zlib"), new byte[1024]);
        File.WriteAllText(_h.Films.MedalsPath, "{}");
        _h.Settings.FilmCacheLimitMb = 512;
        await new SnapshotStore(_h.Snapshots.FilePath).AppendAsync(new ProgressSnapshot(DateTimeOffset.UtcNow, 10, 1000));
        _h.Log.Write("Spartan-II reporting");
        return session;
    }

    private string[] Everything() =>
        Directory.Exists(_h.Root)
            ? Directory.EnumerateFileSystemEntries(_h.Root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(_h.Root, p).Replace('\\', '/')).Order().ToArray()
            : [];

    [Fact]
    public async Task Lists_each_kind_of_file_with_its_size_and_no_absolute_paths()
    {
        await FilledAsync();
        var items = _h.Data.Items();
        Assert.Equal(["Match archive", "Theater films", "Log files", "Settings", "Old snapshots file"], items.Select(i => i.Name));
        Assert.All(items, i => Assert.True(i.Bytes > 0, i.Name));
        Assert.Contains(items, i => i.Location == _h.Archive.DisplayPath);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (_h.Root.StartsWith(home)) Assert.All(items, i => Assert.DoesNotContain(home, i.Location));
    }

    [Fact]
    public void Nothing_stored_yet_lists_empty_and_no_old_snapshots_file()
    {
        var items = _h.Data.Items();
        Assert.DoesNotContain(items, i => i.Name == "Old snapshots file");
        Assert.All(items.Where(i => i.Name != "Match archive"), i => Assert.Equal(0, i.Bytes));
    }

    [Fact]
    public async Task Deleting_everything_leaves_no_files_behind()
    {
        _h.Waypoint = new FakeWaypointSignIn { StaySignedIn = true };
        var session = await FilledAsync();
        Assert.Equal(
            ["archive.db", "films", $"films/{_h.World.Games[0].Id}", $"films/{_h.World.Games[0].Id}/chunk000-type1.zlib",
             "films/medals.json", "logs", "settings.json", "snapshots.json"],
            Everything().Where(p => !p.StartsWith("archive.db-") && !p.StartsWith("logs/")));
        Assert.Contains(Everything(), p => p.StartsWith("logs/ancilla-"));

        var failed = await _h.Data.EraseAsync(session);

        Assert.Empty(failed);
        Assert.Empty(Everything());
        Assert.False(Directory.Exists(_h.Root));
        Assert.False(session.IsSignedIn);
        Assert.Equal("Everything Ancilla kept on this device has been deleted.", session.Notice);
        var waypoint = (FakeWaypointSignIn)_h.Waypoint;
        Assert.Equal(1, waypoint.SignOuts);
        Assert.False(waypoint.StaySignedIn);
        Assert.Null(_h.Settings.FilmCacheLimitMb);
    }

    [Fact]
    public async Task After_deleting_everything_signing_in_starts_afresh()
    {
        var session = await FilledAsync();
        await _h.Data.EraseAsync(session);

        await session.SignInAsync(Spartans.Token);
        Assert.Null(session.Notice);
        Assert.Equal(0, session.ArchiveSummary().Matches);
        Assert.Empty(await session.Snapshots.LoadAsync());
    }

    [Fact]
    public async Task Deleting_leaves_other_files_in_the_folder_alone()
    {
        var session = await FilledAsync();
        var mine = Path.Combine(_h.Root, "halsey-notes.txt");
        File.WriteAllText(mine, "Spartan-II programme");

        await _h.Data.EraseAsync(session);

        Assert.Equal(["halsey-notes.txt"], Everything());
    }

    [Fact]
    public async Task Exporting_everything_has_matches_snapshots_and_settings_but_no_other_players()
    {
        var session = await FilledAsync();
        await session.Snapshots.AppendAsync(new ProgressSnapshot(DateTimeOffset.UtcNow, 12, 3000));

        var json = await _h.Data.ExportAsync(session, "halo");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("halo", root.GetProperty("settings").GetProperty("theme").GetString());
        Assert.Equal(512, root.GetProperty("settings").GetProperty("filmCacheLimitMb").GetInt32());
        Assert.Equal(session.ExportRows().Count, root.GetProperty("matches").GetArrayLength());
        Assert.True(root.GetProperty("progressSnapshots").GetArrayLength() > 0);
        var others = _h.World.Games.SelectMany(g => g.Stats.Players).Select(p => p.Xuid).OfType<string>()
            .Where(x => x != Spartans.NobleSix.Xuid).Distinct().ToList();
        Assert.NotEmpty(others);
        Assert.All(others, x => Assert.DoesNotContain(x, json));
    }

    public void Dispose() => _h.Dispose();
}
