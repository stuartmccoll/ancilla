using System.Text.Json;
using System.Xml;

namespace Ancilla.Core;

/// <summary>Which matches the Stats page counts.</summary>
public enum StatsScope { PvP, Firefight, All }

public enum RankedFilter { All, Ranked, Unranked }

public sealed record StatsFilter(StatsScope Scope = StatsScope.PvP, RankedFilter Ranked = RankedFilter.All)
{
    public static readonly StatsFilter Everything = new(StatsScope.All, RankedFilter.All);

    public bool? IsRanked => Ranked switch { RankedFilter.Ranked => true, RankedFilter.Unranked => false, _ => null };
}

/// <summary>
/// Game mode categories accepted by the service record's <c>gameVariantCategory</c> filter.
/// Undocumented: worked out by querying each number and matching it to the stat groups it
/// returns (Oddball stats for 18, PvE stats for 42, ...). Every matchmade match falls in exactly
/// one category.
/// </summary>
public static class GameVariantCategories
{
    public const int Firefight = 42;

    /// <summary>
    /// Names for the categories seen so far. 9, 24 and 41 are best guesses: their stat groups
    /// don't pin them down.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [6] = "Slayer", [7] = "Attrition", [8] = "Elimination", [9] = "Fiesta", [11] = "Strongholds",
        [12] = "King of the Hill", [14] = "Total Control", [15] = "Capture the Flag", [17] = "Extraction",
        [18] = "Oddball", [19] = "Stockpile", [22] = "Infection", [24] = "Gun Game", [39] = "Land Grab",
        [41] = "Minigame", [Firefight] = "Firefight",
    };

    /// <summary>Every known category except Firefight.</summary>
    public static readonly IReadOnlyList<int> PvP = Names.Keys.Where(k => k != Firefight).ToList();

    public static string Name(int category) => Names.TryGetValue(category, out var n) ? n : $"Mode {category}";

    /// <summary>Fields that hold a best or longest value, so they can't be added up or subtracted.</summary>
    public static bool IsMaximum(string field) =>
        field.StartsWith("Longest", StringComparison.Ordinal) || field.StartsWith("Max", StringComparison.Ordinal);
}

public static class ServiceRecordMath
{
    /// <summary>
    /// <paramref name="total"/> minus <paramref name="part"/>, where <paramref name="part"/> is a
    /// subset of the same matches (e.g. Firefight out of everything). Counts, damage, shots, time
    /// and medals subtract exactly; accuracy and average KDA are recalculated from what's left.
    /// Best killing spree can't be subtracted, so the total's is kept. Groups named in
    /// <paramref name="dropGroups"/> are removed.
    /// </summary>
    public static ServiceRecord Subtract(ServiceRecord total, ServiceRecord part, params string[] dropGroups)
    {
        var t = total.CoreStats;
        var p = part.CoreStats;
        int I(int a, int b) => Math.Max(0, a - b);
        long L(long a, long b) => Math.Max(0, a - b);

        var matches = I(total.MatchesCompleted, part.MatchesCompleted);
        var kills = I(t.Kills, p.Kills);
        var deaths = I(t.Deaths, p.Deaths);
        var assists = I(t.Assists, p.Assists);
        var fired = L(t.ShotsFired, p.ShotsFired);
        var hit = L(t.ShotsHit, p.ShotsHit);

        var partMedals = p.Medals.ToDictionary(m => m.NameId);
        var medals = t.Medals
            .Select(m => partMedals.TryGetValue(m.NameId, out var pm)
                ? m with { Count = I(m.Count, pm.Count), TotalPersonalScoreAwarded = I(m.TotalPersonalScoreAwarded, pm.TotalPersonalScoreAwarded) }
                : m)
            .Where(m => m.Count > 0)
            .ToList();

        var core = new CoreStats
        {
            Score = L(t.Score, p.Score),
            PersonalScore = L(t.PersonalScore, p.PersonalScore),
            RoundsWon = I(t.RoundsWon, p.RoundsWon),
            RoundsLost = I(t.RoundsLost, p.RoundsLost),
            RoundsTied = I(t.RoundsTied, p.RoundsTied),
            Kills = kills,
            Deaths = deaths,
            Assists = assists,
            // Halo's KDA is kills + assists/3 - deaths per match, so the average is exact from the sums.
            AverageKDA = matches == 0 ? 0 : (kills + assists / 3.0 - deaths) / matches,
            Suicides = I(t.Suicides, p.Suicides),
            Betrayals = I(t.Betrayals, p.Betrayals),
            GrenadeKills = I(t.GrenadeKills, p.GrenadeKills),
            HeadshotKills = I(t.HeadshotKills, p.HeadshotKills),
            MeleeKills = I(t.MeleeKills, p.MeleeKills),
            PowerWeaponKills = I(t.PowerWeaponKills, p.PowerWeaponKills),
            ShotsFired = fired,
            ShotsHit = hit,
            Accuracy = fired == 0 ? 0 : 100.0 * hit / fired,
            DamageDealt = L(t.DamageDealt, p.DamageDealt),
            DamageTaken = L(t.DamageTaken, p.DamageTaken),
            CalloutAssists = I(t.CalloutAssists, p.CalloutAssists),
            VehicleDestroys = I(t.VehicleDestroys, p.VehicleDestroys),
            DriverAssists = I(t.DriverAssists, p.DriverAssists),
            Hijacks = I(t.Hijacks, p.Hijacks),
            EmpAssists = I(t.EmpAssists, p.EmpAssists),
            MaxKillingSpree = t.MaxKillingSpree,
            Spawns = I(t.Spawns, p.Spawns),
            ObjectivesCompleted = I(t.ObjectivesCompleted, p.ObjectivesCompleted),
            Medals = medals,
        };

        var groups = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in total.ModeStats)
        {
            if (dropGroups.Contains(key)) continue;
            groups[key] = part.ModeStats.TryGetValue(key, out var pv) ? SubtractGroup(value, pv) : value;
        }

        var time = IsoDuration.Parse(total.TimePlayed) - IsoDuration.Parse(part.TimePlayed);
        return new ServiceRecord
        {
            TimePlayed = XmlConvert.ToString(time < TimeSpan.Zero ? TimeSpan.Zero : time),
            MatchesCompleted = matches,
            Wins = I(total.Wins, part.Wins),
            Losses = I(total.Losses, part.Losses),
            Ties = I(total.Ties, part.Ties),
            CoreStats = core,
            Subqueries = total.Subqueries,
            ModeStats = groups,
        };
    }

    /// <summary>Field-by-field subtraction of one per-mode stat group (numbers and durations).</summary>
    private static JsonElement SubtractGroup(JsonElement total, JsonElement part)
    {
        if (total.ValueKind != JsonValueKind.Object || part.ValueKind != JsonValueKind.Object) return total;
        var result = new Dictionary<string, object?>();
        foreach (var f in total.EnumerateObject())
        {
            part.TryGetProperty(f.Name, out var pv);
            if (GameVariantCategories.IsMaximum(f.Name)) { result[f.Name] = f.Value.Clone(); continue; }
            result[f.Name] = f.Value.ValueKind switch
            {
                JsonValueKind.Number when f.Value.TryGetInt64(out var a) && pv.ValueKind == JsonValueKind.Number && pv.TryGetInt64(out var b) =>
                    Math.Max(0, a - b),
                JsonValueKind.Number when pv.ValueKind == JsonValueKind.Number =>
                    Math.Max(0, f.Value.GetDouble() - pv.GetDouble()),
                JsonValueKind.String when pv.ValueKind == JsonValueKind.String
                    && IsoDuration.TryParse(f.Value.GetString(), out var ta) && IsoDuration.TryParse(pv.GetString(), out var tb) =>
                    XmlConvert.ToString(ta > tb ? ta - tb : TimeSpan.Zero),
                _ => f.Value.Clone(),
            };
        }
        return JsonSerializer.SerializeToElement(result);
    }
}
