using Microsoft.Data.Sqlite;

namespace Ancilla.Data;

/// <summary>
/// The archive's schema, as an ordered list of migrations. The database records how many have run
/// in <c>PRAGMA user_version</c>. Migrations only ever add: a released migration is never edited,
/// and new changes go in a new entry at the end, so an update can't lose data.
/// </summary>
public static class ArchiveSchema
{
    public static IReadOnlyList<string> Migrations { get; } =
    [
        // 1: matches, per-player history and skill results.
        """
        CREATE TABLE matches (
            match_id   TEXT PRIMARY KEY,
            start_time TEXT NOT NULL,
            json       TEXT NOT NULL
        );
        CREATE TABLE history_entries (
            xuid       TEXT NOT NULL,
            match_id   TEXT NOT NULL,
            start_time TEXT NOT NULL,
            json       TEXT NOT NULL,
            PRIMARY KEY (xuid, match_id)
        );
        CREATE INDEX ix_history_entries_time ON history_entries (xuid, start_time DESC);
        CREATE TABLE history_types (
            xuid     TEXT NOT NULL,
            type     TEXT NOT NULL,
            match_id TEXT NOT NULL,
            PRIMARY KEY (xuid, type, match_id)
        ) WITHOUT ROWID;
        CREATE TABLE skills (
            xuid     TEXT NOT NULL,
            match_id TEXT NOT NULL,
            json     TEXT,
            PRIMARY KEY (xuid, match_id)
        );
        """,
        // 2: progress snapshots, versioned UGC names, how far back each history list has been loaded, and settings.
        """
        CREATE TABLE snapshots (
            xuid              TEXT NOT NULL,
            taken_at          TEXT NOT NULL,
            matches_completed INTEGER NOT NULL,
            career_xp         INTEGER NOT NULL,
            active_pass       TEXT,
            active_pass_xp    INTEGER,
            PRIMARY KEY (xuid, taken_at)
        );
        CREATE TABLE ugc (
            kind       TEXT NOT NULL,
            asset_id   TEXT NOT NULL,
            version_id TEXT NOT NULL,
            json       TEXT NOT NULL,
            PRIMARY KEY (kind, asset_id, version_id)
        );
        CREATE TABLE sync_state (
            xuid     TEXT NOT NULL,
            type     TEXT NOT NULL,
            complete INTEGER NOT NULL,
            PRIMARY KEY (xuid, type)
        );
        CREATE TABLE meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """,
    ];

    /// <summary>The version a database is at once every migration has run.</summary>
    public static int Latest => Migrations.Count;

    public static int VersionOf(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Runs the migrations after the database's current version, up to <paramref name="target"/>
    /// (default: all of them), each in its own transaction.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database is from a newer version of Ancilla.</exception>
    public static void Upgrade(SqliteConnection connection, int? target = null)
    {
        var goal = Math.Min(target ?? Latest, Latest);
        var current = VersionOf(connection);
        if (current > Latest)
            throw new InvalidOperationException(
                $"The archive is at schema version {current}, which is newer than this version of Ancilla understands ({Latest}). Update Ancilla rather than opening it with this one.");

        for (var version = current + 1; version <= goal; version++)
        {
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            // PRAGMA can't take parameters; version is our own integer.
            cmd.CommandText = Migrations[version - 1] + $"\nPRAGMA user_version = {version};";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
