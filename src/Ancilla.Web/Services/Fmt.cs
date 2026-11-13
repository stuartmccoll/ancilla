using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ancilla.Core;

namespace Ancilla.Web.Services;

/// <summary>Display formatting shared by the pages.</summary>
public static partial class Fmt
{
    public static string N(long v) => v.ToString("N0");
    public static string N(double v, int dp = 0) => v.ToString("N" + dp);
    public static string Pct(double v, int dp = 1) => v.ToString("F" + dp) + "%";

    /// <summary>For inline styles, which must use '.' regardless of the server's culture.</summary>
    public static string Css(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m"
        : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s"
        : $"{t.TotalSeconds:F1}s";

    public static string Duration(string? iso) => Duration(IsoDuration.Parse(iso));

    public static string Ago(DateTimeOffset when)
    {
        var d = DateTimeOffset.UtcNow - when;
        return d.TotalMinutes < 1 ? "just now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes}m ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours}h ago"
            : d.TotalDays < 7 ? $"{(int)d.TotalDays}d ago"
            : when.ToLocalTime().ToString("d MMM yyyy");
    }

    public static string When(DateTimeOffset when) => when.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm");

    public static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    public static string Outcome(MatchOutcome o) => o switch
    {
        MatchOutcome.Win => "Win",
        MatchOutcome.Loss => "Loss",
        MatchOutcome.Tie => "Tie",
        MatchOutcome.Left => "Left",
        _ => "—",
    };

    public static string OutcomeClass(MatchOutcome o) => o.ToString().ToLowerInvariant();

    public static string TeamName(int id) => id switch { 0 => "Eagle", 1 => "Cobra", _ => $"Team {id + 1}" };

    private static readonly Dictionary<string, string> GroupTitles = new()
    {
        ["CaptureTheFlagStats"] = "Capture the Flag",
        ["EliminationStats"] = "Elimination",
        ["ExtractionStats"] = "Extraction",
        ["InfectionStats"] = "Infection",
        ["OddballStats"] = "Oddball",
        ["ZonesStats"] = "Zones (KOTH, Strongholds)",
        ["StockpileStats"] = "Stockpile",
        ["PvpStats"] = "PvP",
        ["PveStats"] = "PvE (Firefight)",
    };

    public static string GroupTitle(string key) =>
        GroupTitles.TryGetValue(key, out var t) ? t : Humanize(key.EndsWith("Stats") ? key[..^5] : key);

    /// <summary>"FlagCaptureAssists" → "Flag capture assists"; acronyms like KDA are kept.</summary>
    public static string Humanize(string name)
    {
        var words = CamelBoundary().Split(name);
        return string.Join(' ', words.Select((w, i) =>
            i == 0 || w.All(char.IsUpper) ? w : w.ToLowerInvariant()));
    }

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBoundary();

    public static string Json(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number when e.TryGetInt64(out var l) => l.ToString("N0"),
        JsonValueKind.Number => e.GetDouble().ToString("N2"),
        JsonValueKind.String when IsoDuration.TryParse(e.GetString(), out var t) => Duration(t),
        JsonValueKind.String => e.GetString() ?? "",
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        _ => "—",
    };

    public static bool IsZero(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble() == 0,
        JsonValueKind.String => IsoDuration.TryParse(e.GetString(), out var t) && t == TimeSpan.Zero,
        JsonValueKind.Null or JsonValueKind.Undefined => true,
        _ => false,
    };
}
