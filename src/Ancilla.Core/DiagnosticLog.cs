namespace Ancilla.Core;

/// <summary>An error from the log: when, its one-line message, and the exception's lines (type, message, stack trace), if any.</summary>
public sealed record LoggedError(DateTimeOffset At, string Message, IReadOnlyList<string> Detail);

/// <summary>
/// A rolling log file in a folder of its own. One file a day (<c>ancilla-yyyyMMdd.log</c>), rolled
/// to a numbered file when it passes <paramref name="maxBytes"/>, and files older than
/// <paramref name="keepDays"/> are deleted. Every line goes through the <see cref="Redactor"/>
/// before it's written. Logging never throws.
/// <see cref="CatchUnhandled"/> also writes exceptions nothing else caught.
/// </summary>
public sealed class DiagnosticLog(string folder, Redactor redactor, long maxBytes = 2_000_000, int keepDays = 7)
{
    private readonly object _gate = new();
    private DateOnly _pruned;

    public string Folder => folder;
    public string DisplayFolder => SnapshotStore.HomeRelative(folder);
    public Redactor Redactor => redactor;

    public static string DefaultFolder => Path.Combine(Path.GetDirectoryName(SnapshotStore.DefaultPath)!, "logs");

    public void Write(string message) => Write(message, null);

    private const string ErrorMark = "ERROR ";

    /// <summary>
    /// Writes an error: marked so <see cref="RecentErrors"/> can find it, with the exception (if any)
    /// on the lines after it, as <see cref="Write(string, Exception?)"/> does.
    /// </summary>
    public void Error(string message, Exception? exception = null) => Write(ErrorMark + message, exception);

    /// <summary>
    /// The newest <paramref name="max"/> errors in the log files, newest first, redacted again in case
    /// a secret (a gamertag, say) was registered after they were written.
    /// </summary>
    public IReadOnlyList<LoggedError> RecentErrors(int max = 10)
    {
        var errors = new List<LoggedError>();
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(folder)) return errors;
                var files = Directory.EnumerateFiles(folder, "ancilla-*.log")
                    .OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(f => f, StringComparer.Ordinal);
                foreach (var file in files)
                {
                    var inFile = new List<LoggedError>();
                    LoggedError? current = null;
                    foreach (var line in File.ReadLines(file))
                    {
                        if (line.StartsWith("    ", StringComparison.Ordinal))
                        {
                            if (current is not null) ((List<string>)current.Detail).Add(redactor.Redact(line[4..]));
                            continue;
                        }
                        current = null;
                        // "2026-11-24 21:19:14.903Z ERROR Unhandled exception in the page"
                        var space = line.IndexOf("Z ", StringComparison.Ordinal);
                        if (space < 0 || !line.AsSpan(space + 2).StartsWith(ErrorMark, StringComparison.Ordinal)) continue;
                        if (!DateTimeOffset.TryParseExact(line[..space], "yyyy-MM-dd HH:mm:ss.fff",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) continue;
                        current = new LoggedError(at, redactor.Redact(line[(space + 2 + ErrorMark.Length)..]), new List<string>());
                        inFile.Add(current);
                    }
                    inFile.Reverse();
                    errors.AddRange(inFile);
                    if (errors.Count >= max) break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return errors.Take(max).ToList();
    }

    /// <summary>
    /// Writes <paramref name="message"/> on one line, then, if there is one, the exception (its type,
    /// message, inner exceptions and stack trace) on the lines after it, indented. All of it is redacted.
    /// </summary>
    public void Write(string message, Exception? exception)
    {
        var now = DateTimeOffset.UtcNow;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff}Z {redactor.Redact(message).ReplaceLineEndings(" ")}{Environment.NewLine}";
        if (exception is not null)
            line += string.Concat(redactor.Redact(exception.ToString()).ReplaceLineEndings("\n").Split('\n')
                .Where(l => l.Length > 0).Select(l => $"    {l}{Environment.NewLine}"));
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var day = DateOnly.FromDateTime(now.UtcDateTime);
                var path = Path.Combine(folder, $"ancilla-{day:yyyyMMdd}.log");
                if (File.Exists(path) && new FileInfo(path).Length + line.Length > maxBytes)
                {
                    for (var n = 1; ; n++)
                    {
                        var rolled = Path.Combine(folder, $"ancilla-{day:yyyyMMdd}.{n}.log");
                        if (File.Exists(rolled)) continue;
                        File.Move(path, rolled);
                        break;
                    }
                }
                File.AppendAllText(path, line);
                if (_pruned != day) { _pruned = day; Prune(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Writes exceptions that would otherwise go unrecorded: ones that end the process, and failed
    /// tasks nobody awaited. Call it once, from the app's start-up.
    /// </summary>
    public void CatchUnhandled()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Error($"Unhandled exception{(e.IsTerminating ? ", the app is closing" : "")}", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Error("Unobserved task exception", e.Exception);
    }

    /// <summary>Total size of the log files, in bytes.</summary>
    public long SizeBytes() => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "ancilla-*.log").Sum(f => new FileInfo(f).Length)
        : 0;

    /// <summary>
    /// Deletes every log file, then the folder if nothing else is in it. The next line written
    /// starts a new file.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var f in Directory.EnumerateFiles(folder, "ancilla-*.log").ToList()) File.Delete(f);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            _pruned = default;
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow.AddDays(-keepDays);
        foreach (var f in Directory.EnumerateFiles(folder, "ancilla-*.log"))
            if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
    }
}
