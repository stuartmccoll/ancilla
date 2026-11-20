using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Ancilla.Core;

/// <summary>How one Halo endpoint has been doing since the app started.</summary>
public sealed record EndpointHealth(string Endpoint, int Calls, int Errors, int? LastStatus, DateTimeOffset LastCall, double LastMilliseconds);

/// <summary>
/// Counts and logs Halo calls by endpoint name (never by URL, which holds IDs). Shared by every
/// session. Messages go to the <see cref="DiagnosticLog"/> if there is one, already redacted.
/// </summary>
public sealed partial class HaloDiagnostics(Redactor? redactor = null, DiagnosticLog? log = null)
{
    private sealed class Counter
    {
        public int Calls, Errors;
        public int? LastStatus;
        public DateTimeOffset LastCall;
        public double LastMs;
    }

    private readonly ConcurrentDictionary<string, Counter> _endpoints = new();

    public Redactor Redactor { get; } = redactor ?? log?.Redactor ?? new Redactor();
    public DiagnosticLog? Log => log;

    /// <summary>
    /// A name for a URL that holds no IDs: the service, then the path with GUIDs and XUIDs
    /// swapped for placeholders, e.g. <c>halostats/hi/players/xuid({xuid})/matches</c>.
    /// </summary>
    public static string EndpointName(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "unknown";
        var host = uri.Host.Split('.')[0];
        var path = XuidSegment().Replace(uri.AbsolutePath, "xuid({xuid})");
        path = GuidSegment().Replace(path, "{id}");
        return $"{host}{path}";
    }

    public void RecordCall(string endpoint, int? status, TimeSpan elapsed, bool ok)
    {
        var c = _endpoints.GetOrAdd(endpoint, _ => new Counter());
        lock (c)
        {
            c.Calls++;
            if (!ok) c.Errors++;
            c.LastStatus = status;
            c.LastCall = DateTimeOffset.UtcNow;
            c.LastMs = elapsed.TotalMilliseconds;
        }
        Info($"{endpoint} {(status is { } s ? s.ToString() : "no response")} {elapsed.TotalMilliseconds:0} ms");
    }

    public void Info(string message) => log?.Write(message);

    public IReadOnlyList<EndpointHealth> Endpoints()
    {
        var list = new List<EndpointHealth>();
        foreach (var (name, c) in _endpoints)
            lock (c) list.Add(new(name, c.Calls, c.Errors, c.LastStatus, c.LastCall, c.LastMs));
        return [.. list.OrderBy(e => e.Endpoint, StringComparer.Ordinal)];
    }

    [GeneratedRegex(@"xuid\([^)/]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex XuidSegment();

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidSegment();
}
