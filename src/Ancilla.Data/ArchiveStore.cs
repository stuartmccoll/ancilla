using System.Globalization;
using System.Text.Json;
using Ancilla.Core;
using Microsoft.Data.Sqlite;

namespace Ancilla.Data;

/// <summary>What the archive holds for one player.</summary>
public sealed record ArchiveSummary(
    int Matches, int WithScoreboard, int WithSkill, DateTimeOffset? Oldest, DateTimeOffset? Newest,
    long SizeBytes, int SchemaVersion);

/// <summary>
/// The local archive: every match you've loaded, kept in a SQLite file so it survives restarts and
/// isn't capped at what the API lists. History entries, skill results and snapshots are keyed by
/// XUID, so one player's data never mixes with another's; scoreboards and UGC are the same for
/// everyone, so they're stored once. The file holds other players' XUIDs (from scoreboards), so it
/// stays on this machine and out of the repo.
/// </summary>
public sealed class ArchiveStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly string _path;

    /// <summary>Opens the archive at <paramref name="path"/>, creating it and running any migrations it needs.</summary>
    public ArchiveStore(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode = WAL");
        ArchiveSchema.Upgrade(c);
    }

    public string FilePath => _path;
    public string DisplayPath => SnapshotStore.HomeRelative(_path);

    /// <summary>Next to the snapshots file, in the same folder.</summary>
    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(SnapshotStore.DefaultPath)!, "archive.db");

    // ---- Scoreboards (the same for everyone) -------------------------------------------

    public MatchStats? GetMatch(string matchId)
    {
        using var c = Open();
        var json = Scalar<string>(c, "SELECT json FROM matches WHERE match_id = $id", ("$id", Key(matchId)));
        return json is null ? null : JsonSerializer.Deserialize<MatchStats>(json, Json);
    }

    public bool HasMatch(string matchId)
    {
        using var c = Open();
        return Scalar<long?>(c, "SELECT 1 FROM matches WHERE match_id = $id", ("$id", Key(matchId))) is not null;
    }

    public void PutMatch(MatchStats match)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO matches (match_id, start_time, json) VALUES ($id, $t, $j)",
            ("$id", Key(match.MatchId)), ("$t", Time(match.MatchInfo.StartTime)), ("$j", JsonSerializer.Serialize(match, Json)));
    }

    // ---- Match history (per player) ----------------------------------------------------

    /// <summary>How many matches of <paramref name="type"/> are stored for the player.</summary>
    public int CountHistory(string xuid, MatchHistoryType type)
    {
        using var c = Open();
        return (int)Scalar<long>(c, "SELECT COUNT(*) FROM history_types WHERE xuid = $x AND type = $t",
            ("$x", xuid), ("$t", type.ToString()));
    }

    /// <summary>The newest <paramref name="limit"/> entries of <paramref name="type"/>, newest first.</summary>
    public IReadOnlyList<MatchHistoryEntry> GetHistory(string xuid, MatchHistoryType type, int limit = int.MaxValue)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT h.json FROM history_types t
            JOIN history_entries h ON h.xuid = t.xuid AND h.match_id = t.match_id
            WHERE t.xuid = $x AND t.type = $t
            ORDER BY h.start_time DESC, h.match_id
            LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$x", xuid);
        cmd.Parameters.AddWithValue("$t", type.ToString());
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<MatchHistoryEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (JsonSerializer.Deserialize<MatchHistoryEntry>(r.GetString(0), Json) is { } e) list.Add(e);
        return list;
    }

    public bool HasHistory(string xuid, MatchHistoryType type, string matchId)
    {
        using var c = Open();
        return Scalar<long?>(c, "SELECT 1 FROM history_types WHERE xuid = $x AND type = $t AND match_id = $m",
            ("$x", xuid), ("$t", type.ToString()), ("$m", Key(matchId))) is not null;
    }

    /// <summary>Stores history entries; returns how many weren't in the <paramref name="type"/> list already.</summary>
    public int PutHistory(string xuid, MatchHistoryType type, IEnumerable<MatchHistoryEntry> entries)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var added = 0;
        foreach (var e in entries)
        {
            var id = Key(e.MatchId);
            Exec(c, "INSERT OR REPLACE INTO history_entries (xuid, match_id, start_time, json) VALUES ($x, $m, $s, $j)",
                tx, ("$x", xuid), ("$m", id), ("$s", Time(e.MatchInfo.StartTime)), ("$j", JsonSerializer.Serialize(e, Json)));
            added += Exec(c, "INSERT OR IGNORE INTO history_types (xuid, type, match_id) VALUES ($x, $t, $m)",
                tx, ("$x", xuid), ("$t", type.ToString()), ("$m", id));
        }
        tx.Commit();
        return added;
    }

    /// <summary>Whether the API has been paged to the end of this player's history of <paramref name="type"/>.</summary>
    public bool IsComplete(string xuid, MatchHistoryType type)
    {
        using var c = Open();
        return Scalar<long?>(c, "SELECT complete FROM sync_state WHERE xuid = $x AND type = $t",
            ("$x", xuid), ("$t", type.ToString())) == 1;
    }

    public void SetComplete(string xuid, MatchHistoryType type, bool complete)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO sync_state (xuid, type, complete) VALUES ($x, $t, $c)",
            ("$x", xuid), ("$t", type.ToString()), ("$c", complete ? 1 : 0));
    }

    // ---- Skill results (per player) ----------------------------------------------------

    /// <summary>
    /// Whether the player's skill result for the match is stored. <paramref name="skill"/> is null
    /// for matches the service has no skill data for (such as custom games), which are remembered
    /// so they aren't asked about again.
    /// </summary>
    public bool TryGetSkill(string xuid, string matchId, out MatchSkill? skill)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM skills WHERE xuid = $x AND match_id = $m";
        cmd.Parameters.AddWithValue("$x", xuid);
        cmd.Parameters.AddWithValue("$m", Key(matchId));
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { skill = null; return false; }
        skill = r.IsDBNull(0) ? null : JsonSerializer.Deserialize<MatchSkill>(r.GetString(0), Json);
        return true;
    }

    public void PutSkill(string xuid, string matchId, MatchSkill? skill)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO skills (xuid, match_id, json) VALUES ($x, $m, $j)",
            ("$x", xuid), ("$m", Key(matchId)), ("$j", skill is null ? null : JsonSerializer.Serialize(skill, Json)));
    }

    // ---- Versioned UGC (maps, modes, playlists) -----------------------------------------

    public UgcAsset? GetUgc(string kind, AssetRef a)
    {
        using var c = Open();
        var json = Scalar<string>(c, "SELECT json FROM ugc WHERE kind = $k AND asset_id = $a AND version_id = $v",
            ("$k", kind), ("$a", Key(a.AssetId)), ("$v", Key(a.VersionId)));
        return json is null ? null : JsonSerializer.Deserialize<UgcAsset>(json, Json);
    }

    public void PutUgc(string kind, UgcAsset asset)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO ugc (kind, asset_id, version_id, json) VALUES ($k, $a, $v, $j)",
            ("$k", kind), ("$a", Key(asset.AssetId)), ("$v", Key(asset.VersionId)), ("$j", JsonSerializer.Serialize(asset, Json)));
    }

    // ---- Progress snapshots (per player) ------------------------------------------------

    /// <summary>The player's progress snapshots, as a store <c>ProgressService</c> can use.</summary>
    public ISnapshotStore SnapshotsFor(string xuid) => new ArchiveSnapshots(this, xuid);

    /// <summary>
    /// Copies the snapshots from the old JSON file into the archive for <paramref name="xuid"/>, once
    /// only: later calls (and other players) do nothing, so the file's snapshots go to the first
    /// player to sign in and aren't duplicated. The file is left where it is.
    /// </summary>
    public async Task<int> ImportSnapshotsAsync(SnapshotStore file, string xuid, CancellationToken ct = default)
    {
        const string flag = "snapshots-json-imported";
        using (var c = Open())
            if (Scalar<string>(c, "SELECT value FROM meta WHERE key = $k", ("$k", flag)) is not null) return 0;

        var snapshots = await file.LoadAsync(ct);
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var s in snapshots) InsertSnapshot(conn, tx, xuid, s);
        Exec(conn, "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)", tx, ("$k", flag), ("$v", Time(DateTimeOffset.UtcNow)));
        tx.Commit();
        return snapshots.Count;
    }

    internal IReadOnlyList<ProgressSnapshot> LoadSnapshots(string xuid)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT taken_at, matches_completed, career_xp, active_pass, active_pass_xp
            FROM snapshots WHERE xuid = $x ORDER BY taken_at
            """;
        cmd.Parameters.AddWithValue("$x", xuid);
        var list = new List<ProgressSnapshot>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ProgressSnapshot(
                DateTimeOffset.Parse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                r.GetInt32(1), r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetInt64(4)));
        return list;
    }

    internal void AddSnapshot(string xuid, ProgressSnapshot s)
    {
        using var c = Open();
        InsertSnapshot(c, null, xuid, s);
    }

    private static void InsertSnapshot(SqliteConnection c, SqliteTransaction? tx, string xuid, ProgressSnapshot s) =>
        Exec(c, """
            INSERT OR IGNORE INTO snapshots (xuid, taken_at, matches_completed, career_xp, active_pass, active_pass_xp)
            VALUES ($x, $t, $m, $c, $p, $px)
            """, tx, ("$x", xuid), ("$t", Time(s.TakenAt)), ("$m", s.MatchesCompleted), ("$c", s.CareerXpEarned),
            ("$p", s.ActivePass), ("$px", s.ActivePassXpEarned));

    // ---- Summary and export -------------------------------------------------------------

    public ArchiveSummary Summary(string xuid)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*), MIN(h.start_time), MAX(h.start_time),
                   SUM(CASE WHEN m.match_id IS NOT NULL THEN 1 ELSE 0 END),
                   SUM(CASE WHEN s.json IS NOT NULL THEN 1 ELSE 0 END)
            FROM history_entries h
            LEFT JOIN matches m ON m.match_id = h.match_id
            LEFT JOIN skills s ON s.xuid = h.xuid AND s.match_id = h.match_id
            WHERE h.xuid = $x
            """;
        cmd.Parameters.AddWithValue("$x", xuid);
        using var r = cmd.ExecuteReader();
        r.Read();
        var size = new[] { _path, _path + "-wal" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        return new ArchiveSummary(
            (int)r.GetInt64(0), r.IsDBNull(3) ? 0 : (int)r.GetInt64(3), r.IsDBNull(4) ? 0 : (int)r.GetInt64(4),
            r.IsDBNull(1) ? null : ParseTime(r.GetString(1)), r.IsDBNull(2) ? null : ParseTime(r.GetString(2)),
            size, ArchiveSchema.VersionOf(c));
    }

    /// <summary>One row per stored match for the player, oldest first, ready to write out as CSV or JSON.</summary>
    public IReadOnlyList<ArchiveRow> Rows(string xuid)
    {
        var names = new Dictionary<(string, string, string), string?>();
        string? Name(string kind, AssetRef? a)
        {
            if (a is null) return null;
            var key = (kind, a.AssetId, a.VersionId);
            if (!names.TryGetValue(key, out var n)) names[key] = n = GetUgc(kind, a)?.PublicName;
            return n;
        }

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT h.json, m.json, s.json
            FROM history_entries h
            LEFT JOIN matches m ON m.match_id = h.match_id
            LEFT JOIN skills s ON s.xuid = h.xuid AND s.match_id = h.match_id
            WHERE h.xuid = $x ORDER BY h.start_time, h.match_id
            """;
        cmd.Parameters.AddWithValue("$x", xuid);
        var entries = new List<(MatchHistoryEntry, MatchStats?, MatchSkill?)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                entries.Add((JsonSerializer.Deserialize<MatchHistoryEntry>(r.GetString(0), Json)!,
                    r.IsDBNull(1) ? null : JsonSerializer.Deserialize<MatchStats>(r.GetString(1), Json),
                    r.IsDBNull(2) ? null : JsonSerializer.Deserialize<MatchSkill>(r.GetString(2), Json)));

        return entries.Select(x =>
        {
            var (e, match, skill) = x;
            var core = match?.Players.FirstOrDefault(p => p.Xuid == xuid)?.Stats?.CoreStats;
            return new ArchiveRow(
                e.MatchId, e.MatchInfo.StartTime, e.MatchInfo.EndTime, e.MatchInfo.Duration, e.Outcome.ToString(), e.Rank,
                Name("playlist", e.MatchInfo.Playlist), Name("mode", e.MatchInfo.UgcGameVariant), Name("map", e.MatchInfo.MapVariant),
                match?.IsPve(xuid), core?.Kills, core?.Deaths, core?.Assists, core?.KDA, core?.Score, core?.Accuracy,
                core?.DamageDealt, core?.DamageTaken, core?.Medals.Sum(m => m.Count),
                skill?.RankRecap?.PreMatchCsr is { IsRanked: true } pre ? pre.Value : null,
                skill?.RankRecap?.PostMatchCsr is { IsRanked: true } post ? post.Value : null,
                skill?.StatPerformances?.GetValueOrDefault("Kills")?.Expected,
                skill?.StatPerformances?.GetValueOrDefault("Deaths")?.Expected);
        }).ToList();
    }

    public void Dispose() => SqliteConnection.ClearPool(new SqliteConnection(_connectionString));

    // ---- Plumbing -----------------------------------------------------------------------

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    // IDs are GUIDs, which the API sends in lower case.
    private static string Key(string id) => id.ToLowerInvariant();
    private static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static int Exec(SqliteConnection c, string sql, params (string, object?)[] args) => Exec(c, sql, null, args);

    private static int Exec(SqliteConnection c, string sql, SqliteTransaction? tx, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    private static T? Scalar<T>(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var v = cmd.ExecuteScalar();
        if (v is null or DBNull) return default;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(v, target, CultureInfo.InvariantCulture);
    }

    private sealed class ArchiveSnapshots(ArchiveStore store, string xuid) : ISnapshotStore
    {
        public Task<IReadOnlyList<ProgressSnapshot>> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(store.LoadSnapshots(xuid));

        public Task<IReadOnlyList<ProgressSnapshot>> AppendAsync(ProgressSnapshot snapshot, CancellationToken ct = default)
        {
            var all = store.LoadSnapshots(xuid);
            if (!ISnapshotStore.IsNew(all.LastOrDefault(), snapshot)) return Task.FromResult(all);
            store.AddSnapshot(xuid, snapshot);
            return Task.FromResult(store.LoadSnapshots(xuid));
        }
    }
}
