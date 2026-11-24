using System.Text.Json;
using Ancilla.Core;
using Ancilla.Data;
using Ancilla.Theater;

namespace Ancilla.UI.Services;

/// <summary>One kind of thing Ancilla keeps on this machine, for the Settings page.</summary>
/// <param name="Location">Where it is, with the home folder shown as ~.</param>
/// <param name="Bytes">Its size on disk, 0 when there's nothing there yet.</param>
public sealed record StoredItem(string Name, string Location, long Bytes, string Description);

/// <summary>
/// Everything Ancilla keeps in files on this machine: the archive, cached films, the snapshots file
/// from earlier versions, log files and <see cref="LocalSettings"/>. Lists them, exports what's
/// yours, and deletes the lot. The desktop app's sign-in is kept by <see cref="IWaypointSignIn"/>,
/// which <see cref="EraseAsync"/> clears too; the theme is forgotten by <see cref="ThemeChoice"/>.
/// </summary>
/// <param name="folder">
/// Ancilla's own folder. It's removed at the end of <see cref="EraseAsync"/> if nothing else is
/// left in it; no other folder is, so a file kept somewhere of your choosing never takes its folder with it.
/// </param>
public sealed class LocalData(ArchiveStore archive, SnapshotStore snapshots, FilmCache films, DiagnosticLog log,
    LocalSettings settings, IWaypointSignIn waypoint, string folder)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultFolder => Path.GetDirectoryName(SnapshotStore.DefaultPath)!;

    public string Folder => folder;

    /// <summary>What's stored and where. The old snapshots file is only listed while it exists.</summary>
    public IReadOnlyList<StoredItem> Items()
    {
        var items = new List<StoredItem>
        {
            new("Match archive", archive.DisplayPath, Size(archive.Files),
                "Your matches, their scoreboards, skill results, names of maps and modes, and progress snapshots."),
            new("Theater films", films.DisplayRoot, films.TotalBytes(),
                "Films downloaded for match timelines. Each holds everyone in the match's gamertag."),
            new("Log files", log.DisplayFolder, log.SizeBytes(),
                "What the app did, with tokens, IDs, gamertags and paths taken out. Kept for a week."),
            new("Settings", settings.DisplayPath, Size([settings.FilePath]),
                "Choices made on this page, such as the film cache limit."),
        };
        if (File.Exists(snapshots.FilePath))
            items.Add(new("Old snapshots file", snapshots.DisplayPath, Size([snapshots.FilePath]),
                "Progress snapshots from an earlier version, already copied into the archive."));
        return items;
    }

    /// <summary>
    /// Everything stored about you, as one JSON file: your matches (one row each, without other
    /// players' names or IDs), your progress snapshots and your settings. Films and logs aren't
    /// included: films hold other players' data, and logs are only for bug reports.
    /// </summary>
    public async Task<string> ExportAsync(HaloSession session, string themeId)
    {
        var export = new
        {
            App = $"Ancilla {DiagnosticsReport.AppVersion}",
            ExportedAt = DateTimeOffset.UtcNow,
            Settings = new
            {
                Theme = themeId,
                FilmCacheLimitMb = (int)(session.FilmLimitBytes >> 20),
                StaySignedIn = waypoint.IsAvailable ? waypoint.StaySignedIn : (bool?)null,
            },
            ProgressSnapshots = await session.Snapshots.LoadAsync(),
            Matches = session.ExportRows(),
        };
        return JsonSerializer.Serialize(export, Json);
    }

    /// <summary>
    /// Signs out and deletes everything Ancilla keeps on this machine: the sign-in browser's data and
    /// "Stay signed in" (desktop app), then every file <see cref="Items"/> lists,
    /// and finally <see cref="Folder"/> if nothing else is in it. Logs go last, so the sign-out's
    /// own log lines go with them.
    /// </summary>
    /// <returns>The names of anything that couldn't be deleted (a file in use, say). Empty when it all went.</returns>
    public async Task<IReadOnlyList<string>> EraseAsync(HaloSession session)
    {
        var sync = session.BackgroundSync;
        await session.SignOutAsync();
        waypoint.StaySignedIn = false;
        // A check for new matches that was running finishes before the archive goes, so it can't make it again.
        if (sync is not null)
        {
            try { await sync; }
            catch (Exception) { /* it only matters that it has stopped */ }
        }

        var failed = new List<string>();
        void Try(string name, Action delete)
        {
            try { delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(name); }
        }
        Try("Match archive", archive.Erase);
        Try("Theater films", films.DeleteAll);
        Try("Old snapshots file", () => { if (File.Exists(snapshots.FilePath)) File.Delete(snapshots.FilePath); });
        Try("Settings", settings.Erase);
        Try("Log files", log.Clear);
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* something else is in it now */ }
        session.ShowNotice(failed.Count == 0
            ? "Everything Ancilla kept on this device has been deleted."
            : $"Everything else Ancilla kept on this device has been deleted, but not: {string.Join(", ", failed)}. " +
              "It may be in use. Quit Ancilla and delete the Ancilla folder in your local app data folder.");
        return failed;
    }

    private static long Size(IEnumerable<string> files) =>
        files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
}
