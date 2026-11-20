namespace Ancilla.Core;

/// <summary>
/// A rolling log file in a folder of its own. One file a day (<c>ancilla-yyyyMMdd.log</c>), rolled
/// to a numbered file when it passes <paramref name="maxBytes"/>, and files older than
/// <paramref name="keepDays"/> are deleted. Every line goes through the <see cref="Redactor"/>
/// before it's written. Logging never throws.
/// </summary>
public sealed class DiagnosticLog(string folder, Redactor redactor, long maxBytes = 2_000_000, int keepDays = 7)
{
    private readonly object _gate = new();
    private DateOnly _pruned;

    public string Folder => folder;
    public string DisplayFolder => SnapshotStore.HomeRelative(folder);
    public Redactor Redactor => redactor;

    public static string DefaultFolder => Path.Combine(Path.GetDirectoryName(SnapshotStore.DefaultPath)!, "logs");

    public void Write(string message)
    {
        var now = DateTimeOffset.UtcNow;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff}Z {redactor.Redact(message).ReplaceLineEndings(" ")}{Environment.NewLine}";
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

    /// <summary>Total size of the log files, in bytes.</summary>
    public long SizeBytes() => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "ancilla-*.log").Sum(f => new FileInfo(f).Length)
        : 0;

    private void Prune()
    {
        var cutoff = DateTime.UtcNow.AddDays(-keepDays);
        foreach (var f in Directory.EnumerateFiles(folder, "ancilla-*.log"))
            if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f);
    }
}
