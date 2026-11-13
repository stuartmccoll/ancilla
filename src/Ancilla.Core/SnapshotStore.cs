using System.Text.Json;

namespace Ancilla.Core;

/// <summary>
/// Stores progress snapshots in a JSON file. The API has no per-match XP figure, so the
/// change in career XP between runs, divided by matches played, gives the real rate.
/// </summary>
public sealed class SnapshotStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Where this store keeps its snapshots.</summary>
    public string FilePath => path;

    /// <summary>
    /// <see cref="FilePath"/> with the user's home folder shown as <c>~</c>, so pages don't display
    /// the account name.
    /// </summary>
    public string DisplayPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return path;
            var full = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (full.Equals(home, comparison)) return "~";
            var prefix = Path.EndsInDirectorySeparator(home) ? home : home + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, comparison)
                ? "~" + Path.DirectorySeparatorChar + full[prefix.Length..]
                : path;
        }
    }

    public static string DefaultPath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "Ancilla");

            // Carry over snapshots saved before the app was renamed from "HaloAncilla".
            var legacyDir = Path.Combine(appData, "HaloAncilla");
            if (!Directory.Exists(dir) && Directory.Exists(legacyDir))
            {
                try { Directory.Move(legacyDir, dir); }
                catch (IOException) { return Path.Combine(legacyDir, "snapshots.json"); }
                catch (UnauthorizedAccessException) { return Path.Combine(legacyDir, "snapshots.json"); }
            }

            return Path.Combine(dir, "snapshots.json");
        }
    }

    public async Task<IReadOnlyList<ProgressSnapshot>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return [];
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<ProgressSnapshot>>(fs, Json, ct) ?? [];
    }

    /// <summary>Appends the snapshot if anything changed, and returns the full history.</summary>
    public async Task<IReadOnlyList<ProgressSnapshot>> AppendAsync(ProgressSnapshot snapshot, CancellationToken ct = default)
    {
        var all = (await LoadAsync(ct)).ToList();
        var last = all.LastOrDefault();
        if (last is null || last.MatchesCompleted != snapshot.MatchesCompleted || last.CareerXpEarned != snapshot.CareerXpEarned)
        {
            all.Add(snapshot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(all, Json), ct);
        }
        return all;
    }
}
