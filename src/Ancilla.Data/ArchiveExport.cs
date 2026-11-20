using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ancilla.Data;

/// <summary>One stored match as a flat row. Fields are blank when the scoreboard or skill result isn't stored.</summary>
public sealed record ArchiveRow(
    string MatchId, DateTimeOffset Started, DateTimeOffset Ended, string? Duration, string Outcome, int Rank,
    string? Playlist, string? Mode, string? Map, bool? AgainstAi,
    int? Kills, int? Deaths, int? Assists, double? Kda, long? Score, double? Accuracy,
    long? DamageDealt, long? DamageTaken, int? Medals,
    int? CsrBefore, int? CsrAfter, double? ExpectedKills, double? ExpectedDeaths);

public static class ArchiveExport
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string ToJson(IReadOnlyList<ArchiveRow> rows) => JsonSerializer.Serialize(rows, JsonOptions);

    public static string ToCsv(IReadOnlyList<ArchiveRow> rows)
    {
        var props = typeof(ArchiveRow).GetProperties();
        var sb = new StringBuilder();
        sb.Append(string.Join(',', props.Select(p => Cell(p.Name))));
        foreach (var row in rows)
        {
            sb.Append("\r\n");
            sb.Append(string.Join(',', props.Select(p => Cell(Format(p.GetValue(row))))));
        }
        sb.Append("\r\n");
        return sb.ToString();
    }

    private static string Format(object? v) => v switch
    {
        null => "",
        DateTimeOffset t => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>
    /// Quotes a cell when it needs it. Names come from user-made maps and modes, so a cell that a
    /// spreadsheet would read as a formula gets a leading apostrophe.
    /// </summary>
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '@' or '\t' or '\r') value = "'" + value;
        else if (value.Length > 1 && value[0] == '-' && !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
