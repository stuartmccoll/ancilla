namespace Ancilla.Core.Tests;

public sealed class SnapshotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ancilla-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "nested", "snapshots.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static readonly DateTimeOffset Day0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_missing_file_is_empty_history()
    {
        Assert.Empty(await new SnapshotStore(FilePath).LoadAsync());
    }

    [Fact]
    public async Task Appends_only_when_something_changed()
    {
        var store = new SnapshotStore(FilePath);
        await store.AppendAsync(new ProgressSnapshot(Day0, 100, 50_000, "pass/a", 1_000));
        await store.AppendAsync(new ProgressSnapshot(Day0.AddHours(1), 100, 50_000, "pass/a", 1_000));   // same again
        await store.AppendAsync(new ProgressSnapshot(Day0.AddHours(2), 101, 50_900, "pass/a", 1_100));
        await store.AppendAsync(new ProgressSnapshot(Day0.AddHours(3), 101, 50_900, "pass/b", 0));        // new pass

        var all = await new SnapshotStore(FilePath).LoadAsync();
        Assert.Equal([100, 101, 101], all.Select(s => s.MatchesCompleted));
        Assert.Equal("pass/b", all[^1].ActivePass);
        Assert.Equal(Day0, all[0].TakenAt);
    }

    [Fact]
    public async Task Reads_snapshots_saved_before_pass_xp_was_recorded()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, """[{"takenAt":"2026-11-01T12:00:00+00:00","matchesCompleted":7,"careerXpEarned":900}]""");
        var s = Assert.Single(await new SnapshotStore(FilePath).LoadAsync());
        Assert.Null(s.ActivePass);
        Assert.Null(s.ActivePassXpEarned);
        Assert.Equal(900, s.CareerXpEarned);
    }

    [Fact]
    public void Display_path_hides_the_home_folder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var under = Path.Combine(home, "AppData", "Ancilla", "snapshots.json");
        Assert.Equal(Path.Combine("~", "AppData", "Ancilla", "snapshots.json"), new SnapshotStore(under).DisplayPath);
        Assert.Equal("~", SnapshotStore.HomeRelative(home));
        Assert.DoesNotContain(home, new SnapshotStore(under).DisplayPath);
    }

    [Fact]
    public void Paths_outside_home_are_unchanged()
    {
        var outside = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "reach", "snapshots.json");
        Assert.Equal(outside, SnapshotStore.HomeRelative(outside));
    }

    [Fact]
    public void A_folder_that_only_starts_like_home_is_not_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar);
        var sibling = home + "-reach" + Path.DirectorySeparatorChar + "snapshots.json";
        Assert.Equal(sibling, SnapshotStore.HomeRelative(sibling));
    }
}
