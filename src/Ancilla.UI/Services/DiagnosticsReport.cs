using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Ancilla.Core;
using Ancilla.Data;
using Ancilla.Theater;

namespace Ancilla.UI.Services;

/// <summary>What the Diagnostics page shows, and the text its "Copy diagnostics" button copies.</summary>
public sealed record DiagnosticsSnapshot(
    string Version, string Os, string Runtime,
    IReadOnlyList<EndpointHealth> Endpoints,
    int SharedEntries, int Images, long ImageBytes,
    int Snapshots, int ArchivedMatches, long ArchiveBytes,
    int Films, long FilmBytes,
    long LogBytes, string LogFolder,
    int Queued, int InFlight,
    IReadOnlyList<LoggedError> Errors);

public sealed class DiagnosticsReport(HaloDiagnostics diagnostics, HaloCache cache, AssetCache assets,
    ArchiveStore archive, FilmCache films)
{
    public const string BugTemplateUrl = "https://github.com/stuartmccoll/ancilla/issues/new?template=bug.yml";

    public static string AppVersion =>
        (typeof(DiagnosticsReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown")
            .Split('+')[0];

    public async Task<DiagnosticsSnapshot> TakeAsync(HaloSession session)
    {
        var (images, imageBytes) = assets.Size();
        int snapshotCount = 0, archived = 0;
        long archiveBytes = 0;
        try
        {
            if (session.IsSignedIn)
            {
                snapshotCount = (await session.Snapshots.LoadAsync()).Count;
                var summary = archive.Summary(session.Xuid);
                archived = summary.Matches;
                archiveBytes = summary.SizeBytes;
            }
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException) { }

        int filmCount = 0;
        long filmBytes = 0;
        try
        {
            filmCount = films.CachedMatches().Count();
            if (Directory.Exists(films.Root))
                filmBytes = Directory.EnumerateFiles(films.Root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return new(AppVersion, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
            diagnostics.Endpoints(), cache.Count, images, imageBytes, snapshotCount, archived, archiveBytes,
            filmCount, filmBytes, diagnostics.Log?.SizeBytes() ?? 0, diagnostics.Log?.DisplayFolder ?? "(logging is off)",
            session.QueueLength, session.InFlight,
            diagnostics.Log?.RecentErrors(ErrorsShown) ?? []);
    }

    /// <summary>How many recent errors the page lists, and how many of them (with their stack traces) a copy includes.</summary>
    public const int ErrorsShown = 10, ErrorsCopied = 2;

    // A stack trace is cut to this many lines in a copy: enough to see where, short enough to paste.
    private const int DetailLinesCopied = 40;

    /// <summary>A plain-text summary for a bug report, run through the redactor.</summary>
    public string ToText(DiagnosticsSnapshot d)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Ancilla {d.Version}");
        sb.AppendLine($"OS: {d.Os}");
        sb.AppendLine($"Runtime: {d.Runtime}");
        sb.AppendLine();
        sb.AppendLine("Halo services (calls, errors, last status, last call):");
        if (d.Endpoints.Count == 0) sb.AppendLine("  none yet");
        foreach (var e in d.Endpoints)
            sb.AppendLine($"  {e.Endpoint}: {e.Calls} calls, {e.Errors} errors, last {(e.LastStatus is { } s ? s.ToString() : "no response")}, {e.LastMilliseconds:0} ms, {e.LastCall:u}");
        sb.AppendLine();
        sb.AppendLine("Caches:");
        sb.AppendLine($"  Shared data: {d.SharedEntries} entries");
        sb.AppendLine($"  Images: {d.Images} ({Size(d.ImageBytes)})");
        sb.AppendLine($"  Match archive: {d.ArchivedMatches:N0} matches, {d.Snapshots} progress snapshots ({Size(d.ArchiveBytes)})");
        sb.AppendLine($"  Films: {d.Films} ({Size(d.FilmBytes)})");
        sb.AppendLine($"  Log files: {Size(d.LogBytes)}");
        sb.AppendLine();
        sb.AppendLine($"Requests: {d.InFlight} running, {d.Queued} waiting");
        sb.AppendLine();
        sb.AppendLine("Recent errors:");
        if (d.Errors.Count == 0) sb.AppendLine("  none in the log files");
        foreach (var e in d.Errors.Take(ErrorsCopied))
        {
            sb.AppendLine($"  {e.At:u} {e.Message}");
            foreach (var line in e.Detail.Take(DetailLinesCopied)) sb.AppendLine($"    {line}");
            if (e.Detail.Count > DetailLinesCopied) sb.AppendLine($"    ({e.Detail.Count - DetailLinesCopied} more lines in the log file)");
        }
        if (d.Errors.Count > ErrorsCopied) sb.AppendLine($"  and {d.Errors.Count - ErrorsCopied} earlier (see the log files)");
        return diagnostics.Redactor.Redact(sb.ToString());
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:0.#} GB",
    };
}
