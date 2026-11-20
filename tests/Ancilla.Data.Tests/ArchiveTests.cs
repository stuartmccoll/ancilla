using Ancilla.Core;
using Ancilla.Data;
using Ancilla.Testing;
using Microsoft.Data.Sqlite;

namespace Ancilla.Data.Tests;

public sealed class ArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ancilla-data-" + Guid.NewGuid().ToString("N"));
    private readonly List<ArchiveStore> _open = [];
    private static readonly HaloWorld World = new();
    private static readonly string Me = Spartans.NobleSix.Xuid;

    private string DbPath => Path.Combine(_root, "archive.db");

    private ArchiveStore Open()
    {
        var store = new ArchiveStore(DbPath);
        _open.Add(store);
        return store;
    }

    public void Dispose()
    {
        foreach (var s in _open) s.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- Migrations ---------------------------------------------------------------------

    [Fact]
    public void An_empty_database_is_brought_up_to_date()
    {
        using var store = Open();
        Assert.Equal(ArchiveSchema.Latest, store.Summary(Me).SchemaVersion);
        Assert.Equal(0, store.Summary(Me).Matches);
    }

    [Fact]
    public void Opening_it_again_changes_nothing()
    {
        Open().PutMatch(World.Games[0].Stats);
        var again = Open();
        Assert.Equal(ArchiveSchema.Latest, again.Summary(Me).SchemaVersion);
        Assert.NotNull(again.GetMatch(World.Games[0].Id));
    }

    [Theory]
    [MemberData(nameof(EarlierVersions))]
    public void Every_earlier_schema_upgrades_and_keeps_its_data(int version)
    {
        Directory.CreateDirectory(_root);
        var game = World.Games[0];
        using (var c = new SqliteConnection($"Data Source={DbPath}"))
        {
            c.Open();
            ArchiveSchema.Upgrade(c, version);
            Assert.Equal(version, ArchiveSchema.VersionOf(c));
            if (version >= 1)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO matches (match_id, start_time, json) VALUES ($id, $t, $j)";
                cmd.Parameters.AddWithValue("$id", game.Id);
                cmd.Parameters.AddWithValue("$t", game.Stats.MatchInfo.StartTime.UtcDateTime.ToString("O"));
                cmd.Parameters.AddWithValue("$j", System.Text.Json.JsonSerializer.Serialize(game.Stats, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
                cmd.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearAllPools();

        var store = Open();
        Assert.Equal(ArchiveSchema.Latest, store.Summary(Me).SchemaVersion);
        if (version >= 1) Assert.Equal(game.Id, store.GetMatch(game.Id)?.MatchId);
        // The tables added by later migrations work.
        store.PutUgc("map", HaloWorld.LiveFire.Ugc);
        Assert.Equal("Live Fire", store.GetUgc("map", HaloWorld.LiveFire.Ref(2))?.PublicName);
    }

    public static TheoryData<int> EarlierVersions() => new(Enumerable.Range(0, ArchiveSchema.Latest));

    [Fact]
    public void A_database_from_a_newer_version_is_left_alone()
    {
        Directory.CreateDirectory(_root);
        using (var c = new SqliteConnection($"Data Source={DbPath}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {ArchiveSchema.Latest + 1}";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var ex = Assert.Throws<InvalidOperationException>(() => new ArchiveStore(DbPath));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void A_failed_migration_rolls_back()
    {
        Directory.CreateDirectory(_root);
        using var c = new SqliteConnection($"Data Source={DbPath}");
        c.Open();
        ArchiveSchema.Upgrade(c, 1);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE snapshots (x INTEGER)";   // makes migration 2 fail halfway
        cmd.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => ArchiveSchema.Upgrade(c));
        Assert.Equal(1, ArchiveSchema.VersionOf(c));
    }

    // ---- Storage ------------------------------------------------------------------------

    [Fact]
    public void A_scoreboard_survives_a_round_trip_and_reopening()
    {
        var game = World.Games.First(g => g.Category == 18);   // Oddball, with mode stats
        Open().PutMatch(game.Stats);
        SqliteConnection.ClearAllPools();

        var back = Open().GetMatch(game.Id)!;
        Assert.Equal(game.Stats.Players.Count, back.Players.Count);
        var mine = back.Players.First(p => p.Xuid == Me);
        Assert.Equal(game.Me.Stats!.CoreStats.Kills, mine.Stats!.CoreStats.Kills);
        Assert.Equal(game.Me.Stats.CoreStats.Medals.Count, mine.Stats.CoreStats.Medals.Count);
        Assert.True(mine.Stats.ModeStats.ContainsKey("OddballStats"));   // mode groups come back
        Assert.False(back.IsPve(Me));
        Assert.Null(Open().GetMatch(World.Games[2].Id));
    }

    [Fact]
    public void Firefight_is_still_recognised_once_stored()
    {
        var pve = World.Games.First(g => g.IsPve);
        var store = Open();
        store.PutMatch(pve.Stats);
        Assert.True(store.GetMatch(pve.Id)!.IsPve(Me));
    }

    [Fact]
    public void History_is_listed_newest_first_per_type()
    {
        var store = Open();
        var matchmade = World.Matchmade().Select(g => g.Entry).ToList();
        Assert.Equal(matchmade.Count, store.PutHistory(Me, MatchHistoryType.Matchmaking, matchmade));
        Assert.Equal(0, store.PutHistory(Me, MatchHistoryType.Matchmaking, matchmade));   // nothing new the second time
        store.PutHistory(Me, MatchHistoryType.All, World.Games.Select(g => g.Entry));

        Assert.Equal(matchmade.Count, store.CountHistory(Me, MatchHistoryType.Matchmaking));
        Assert.Equal(World.Games.Count, store.CountHistory(Me, MatchHistoryType.All));
        var listed = store.GetHistory(Me, MatchHistoryType.Matchmaking);
        Assert.Equal(matchmade.OrderByDescending(e => e.MatchInfo.StartTime).Select(e => e.MatchId), listed.Select(e => e.MatchId));
        Assert.Equal(5, store.GetHistory(Me, MatchHistoryType.All, 5).Count);
        Assert.True(store.HasHistory(Me, MatchHistoryType.All, World.Games[0].Id));
        Assert.False(store.HasHistory(Me, MatchHistoryType.Custom, World.Games[0].Id));
    }

    [Fact]
    public void One_players_data_never_shows_up_for_another()
    {
        var store = Open();
        var other = Spartans.Kat.Xuid;
        store.PutHistory(Me, MatchHistoryType.All, World.Games.Take(5).Select(g => g.Entry));
        store.PutSkill(Me, World.Games[0].Id, new MatchSkill(1200, null, null, 0, null));
        store.SnapshotsFor(Me).AppendAsync(new ProgressSnapshot(DateTimeOffset.UtcNow, 10, 1000)).Wait();

        Assert.Equal(0, store.CountHistory(other, MatchHistoryType.All));
        Assert.Empty(store.GetHistory(other, MatchHistoryType.All));
        Assert.False(store.TryGetSkill(other, World.Games[0].Id, out _));
        Assert.Empty(store.SnapshotsFor(other).LoadAsync().Result);
        Assert.Equal(0, store.Summary(other).Matches);
        Assert.Equal(5, store.Summary(Me).Matches);
    }

    [Fact]
    public void Skills_ugc_and_completeness_are_remembered()
    {
        var store = Open();
        var id = World.Games[0].Id;
        Assert.False(store.TryGetSkill(Me, id, out _));
        store.PutSkill(Me, id, new MatchSkill(1234.5, null, new() { ["Kills"] = new(10, 8, 2) }, 1, null));
        Assert.True(store.TryGetSkill(Me, id, out var skill));
        Assert.Equal(1234.5, skill!.TeamMmr);
        Assert.Equal(8, skill.StatPerformances!["Kills"].Expected);
        store.PutSkill(Me, World.Games[1].Id, null);
        Assert.True(store.TryGetSkill(Me, World.Games[1].Id, out var none));
        Assert.Null(none);

        Assert.False(store.IsComplete(Me, MatchHistoryType.All));
        store.SetComplete(Me, MatchHistoryType.All, true);
        Assert.True(store.IsComplete(Me, MatchHistoryType.All));
        Assert.False(store.IsComplete(Me, MatchHistoryType.Matchmaking));

        // Names are kept per version: a renamed map is a new row.
        var v1 = new UgcAsset(HaloWorld.Streets.AssetId, HaloWorld.Streets.VersionId, "Streets", null, null);
        var v2 = v1 with { VersionId = HaloWorld.Ids.Version(99), PublicName = "Streets (remastered)" };
        store.PutUgc("map", v1);
        store.PutUgc("map", v2);
        Assert.Equal("Streets", store.GetUgc("map", new(2, v1.AssetId, v1.VersionId))!.PublicName);
        Assert.Equal("Streets (remastered)", store.GetUgc("map", new(2, v2.AssetId, v2.VersionId))!.PublicName);
        Assert.Null(store.GetUgc("mode", new(2, v1.AssetId, v1.VersionId)));
    }

    // ---- Snapshots ----------------------------------------------------------------------

    [Fact]
    public async Task Snapshots_are_appended_only_when_something_changed()
    {
        var snaps = Open().SnapshotsFor(Me);
        var t = DateTimeOffset.UtcNow;
        await snaps.AppendAsync(new ProgressSnapshot(t, 10, 1000, "pass", 500));
        await snaps.AppendAsync(new ProgressSnapshot(t.AddMinutes(1), 10, 1000, "pass", 500));
        var all = await snaps.AppendAsync(new ProgressSnapshot(t.AddMinutes(2), 11, 1100, "pass", 560));
        Assert.Equal([10, 11], all.Select(s => s.MatchesCompleted));
        Assert.Equal("pass", all[0].ActivePass);
        Assert.Equal(560, all[1].ActivePassXpEarned);
    }

    [Fact]
    public async Task Old_json_snapshots_are_imported_once()
    {
        var file = new SnapshotStore(Path.Combine(_root, "snapshots.json"));
        var t = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        await file.AppendAsync(new ProgressSnapshot(t, 100, 5_000));
        await file.AppendAsync(new ProgressSnapshot(t.AddDays(1), 110, 6_000, "S1", 300));

        var store = Open();
        Assert.Equal(2, await store.ImportSnapshotsAsync(file, Me));
        Assert.Equal(0, await store.ImportSnapshotsAsync(file, Me));                    // not again
        Assert.Equal(0, await store.ImportSnapshotsAsync(file, Spartans.Kat.Xuid));     // not for someone else either
        SqliteConnection.ClearAllPools();
        Assert.Equal(0, await Open().ImportSnapshotsAsync(file, Me));                   // not after a restart

        var loaded = await store.SnapshotsFor(Me).LoadAsync();
        Assert.Equal([100, 110], loaded.Select(s => s.MatchesCompleted));
        Assert.Equal(t, loaded[0].TakenAt);
        Assert.Equal(300, loaded[1].ActivePassXpEarned);
        Assert.Empty(await store.SnapshotsFor(Spartans.Kat.Xuid).LoadAsync());
        Assert.Equal(2, (await file.LoadAsync()).Count);   // the file is left alone
    }

    [Fact]
    public async Task A_missing_snapshots_file_imports_nothing()
    {
        Assert.Equal(0, await Open().ImportSnapshotsAsync(new SnapshotStore(Path.Combine(_root, "nope.json")), Me));
    }

    // ---- Summary and export -------------------------------------------------------------

    private ArchiveStore Filled()
    {
        var store = Open();
        store.PutHistory(Me, MatchHistoryType.All, World.Games.Select(g => g.Entry));
        foreach (var g in World.Games.Take(6)) store.PutMatch(g.Stats);
        foreach (var a in HaloWorld.Maps.Concat(HaloWorld.Modes)) store.PutUgc(HaloWorld.Maps.Contains(a) ? "map" : "mode", a.Ugc);
        store.PutSkill(Me, World.Games[0].Id, new MatchSkill(1200, new RankRecap(
            new Csr(1500, 0, "Gold", 1500, 2, "Gold", 1550, 3, 10),
            new Csr(1510, 0, "Gold", 1500, 2, "Gold", 1550, 3, 10)), new() { ["Kills"] = new(12, 9.5, 3), ["Deaths"] = new(8, 9, 3) }, 0, null));
        return store;
    }

    [Fact]
    public void The_summary_counts_what_is_stored()
    {
        var s = Filled().Summary(Me);
        Assert.Equal(World.Games.Count, s.Matches);
        Assert.Equal(6, s.WithScoreboard);
        Assert.Equal(1, s.WithSkill);
        Assert.Equal(World.Games.Min(g => g.Stats.MatchInfo.StartTime).UtcDateTime, s.Oldest!.Value.UtcDateTime);
        Assert.True(s.SizeBytes > 0);
    }

    [Fact]
    public void Rows_join_the_scoreboard_skill_and_names()
    {
        var rows = Filled().Rows(Me);
        Assert.Equal(World.Games.Count, rows.Count);
        Assert.Equal(rows.OrderBy(r => r.Started).Select(r => r.MatchId), rows.Select(r => r.MatchId));

        var g = World.Games[0];
        var row = rows.Single(r => r.MatchId == g.Id);
        Assert.Equal(g.Me.Stats!.CoreStats.Kills, row.Kills);
        Assert.Equal("Live Fire", row.Map);
        Assert.Equal("Slayer", row.Mode);
        Assert.Equal(1500, row.CsrBefore);
        Assert.Equal(1510, row.CsrAfter);
        Assert.Equal(9.5, row.ExpectedKills);
        Assert.False(row.AgainstAi);

        var noBoard = rows.First(r => r.Kills is null);   // stored history without a scoreboard still exports
        Assert.Null(noBoard.AgainstAi);
        Assert.Contains(rows, r => r.AgainstAi == true);
    }

    [Fact]
    public void Csv_has_a_header_one_row_each_and_safe_cells()
    {
        var rows = Filled().Rows(Me);
        var csv = ArchiveExport.ToCsv(rows);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(rows.Count + 1, lines.Length);
        Assert.StartsWith("MatchId,Started,Ended,", lines[0]);
        Assert.Contains("Live Fire", csv);
        Assert.DoesNotContain(Me, csv);   // the player's own ID isn't in a row

        var nasty = rows[0] with { Map = "=HYPERLINK(\"x\"),a", Mode = "-2+3", Playlist = "Plain, with comma", Outcome = "Win", Kills = -3 };
        var line = ArchiveExport.ToCsv([nasty]).Split("\r\n")[1];
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\"),a\"", line);   // formula defanged and quoted
        Assert.Contains("'-2+3", line);
        Assert.Contains("\"Plain, with comma\"", line);
        Assert.Contains(",-3,", line);   // real negative numbers are left alone
    }

    [Fact]
    public void Json_is_a_list_of_the_same_rows()
    {
        var rows = Filled().Rows(Me);
        var doc = System.Text.Json.JsonDocument.Parse(ArchiveExport.ToJson(rows));
        Assert.Equal(rows.Count, doc.RootElement.GetArrayLength());
        Assert.Equal(rows[0].MatchId, doc.RootElement[0].GetProperty("matchId").GetString());
    }
}
