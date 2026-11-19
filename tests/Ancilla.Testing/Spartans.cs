namespace Ancilla.Testing;

/// <summary>
/// Synthetic players, all Halo characters. Nothing here is a real account: XUIDs are made up in the
/// Xbox Live range (0x0009_xxxx_xxxx_xxxx) and written in hex, as in the Theater tests, so the
/// privacy check never sees a decimal XUID in the source.
/// </summary>
public static class Spartans
{
    public sealed record Spartan(string Gamertag, string Xuid, string? ServiceTag = null);

    public static string Xuid(int n) => (0x0009_0000_0000_0100UL + (ulong)n).ToString();

    // Noble Team, by service number. Noble Six is the signed-in player.
    public static readonly Spartan NobleSix = new("Noble Six", Xuid(312), "B312");
    public static readonly Spartan Kat = new("Kat B320", Xuid(320));
    public static readonly Spartan Jorge = new("Jorge 052", Xuid(52));
    public static readonly Spartan Emile = new("Emile A239", Xuid(239));
    public static readonly Spartan Carter = new("Carter A259", Xuid(259));
    public static readonly Spartan Jun = new("Jun A266", Xuid(266));

    // Blue Team.
    public static readonly Spartan Fred = new("Fred 104", Xuid(104));
    public static readonly Spartan Kelly = new("Kelly 087", Xuid(87));
    public static readonly Spartan Linda = new("Linda 058", Xuid(58));

    // The other side.
    public static readonly Spartan Thel = new("Thel Vadam", Xuid(1001));
    public static readonly Spartan Rtas = new("Rtas Vadum", Xuid(1002));
    public static readonly Spartan HalfJaw = new("Half Jaw", Xuid(1003));

    public static readonly IReadOnlyList<Spartan> All =
        [NobleSix, Kat, Jorge, Emile, Carter, Jun, Fred, Kelly, Linda, Thel, Rtas, HalfJaw];

    /// <summary>A Spartan token the fake backend accepts. Not a real token shape (no JWE), by design.</summary>
    public const string Token = "v4=cortana-synthetic-token";
}
