namespace Ancilla.Theater.Format;

/// <summary>
/// The summary's 8-bit medal codes, mapped to medal NameIds. The codes follow the game's own
/// medal numbering, not any ordering of the published medal metadata, so they're learned: each
/// was confirmed by matching per-player code counts against a scoreboard where only one medal
/// fitted, or where every other fitting medal already had a code ("by elimination").
/// `Ancilla.Theater inspect --learn` proposes more from new films. Codes 0–5 (Double Kill to Killamanjaro)
/// and 9–10 (Killing Spree, Killing Frenzy) run in the game's own order, which supports reading the
/// code as the game's medal index.
/// </summary>
public static class MedalCodes
{
    public static readonly IReadOnlyDictionary<int, long> Known = new Dictionary<int, long>
    {
        [0] = 622331684L,    // Double Kill
        [1] = 2063152177L,   // Triple Kill
        [2] = 835814121L,    // Overkill (learned from a Firefight film)
        [3] = 2137071619L,   // Killtacular (Firefight)
        [4] = 1430343434L,   // Killtrocity (Firefight)
        [5] = 3835606176L,   // Killamanjaro (Firefight; by elimination, with 78 = Sharpshooter)
        [9] = 2780740615L,   // Killing Spree
        [10] = 4261842076L,  // Killing Frenzy (Firefight)
        [13] = 865763896L,   // Perfection (Firefight; by elimination, Wingman is 65)
        [26] = 3233952928L,  // Killjoy
        [31] = 1680000231L,  // Flawless Victory (Firefight)
        [32] = 1169390319L,  // Steaktacular (3 films)
        [36] = 3488248720L,  // Stopped Short (by elimination, Breacher is 165)
        [44] = 4100966367L,  // Extermination (by elimination and difficulty)
        [65] = 1284032216L,  // Wingman
        [71] = 2852571933L,  // Rifleman (Firefight)
        [72] = 1146876011L,  // Bomber
        [73] = 2648272972L,  // Grenadier (Firefight)
        [75] = 1210678802L,  // Warrior
        [76] = 1172766553L,  // Gunslinger
        [78] = 4277328263L,  // Sharpshooter (the only medal that fits in both a PvP and a Firefight film)
        [79] = 2758320809L,  // Marksman (Firefight)
        [82] = 548533137L,   // Back Smack (2 films)
        [85] = 3114137341L,  // Bulltrue
        [86] = 3905838030L,  // Cluster Luck (Firefight)
        [88] = 2418616582L,  // Harpoon
        [91] = 87172902L,    // Odin's Raven
        [99] = 1841872491L,  // Tag & Bag (3 films)
        [100] = 1734214473L, // Whiplash
        [105] = 2123530881L, // Reversal
        [106] = 3934547153L, // Hail Mary
        [108] = 4229934157L, // Snipe
        [109] = 1512363953L, // Perfect (count matched two medals; difficulty code chose this one)
        [110] = 2414983178L, // Bank Shot
        [111] = 988255960L,  // Fire & Forget
        [114] = 2602963073L, // No Scope
        [116] = 1646928910L, // Grand Slam
        [117] = 3334154676L, // Guardian Angel
        [121] = 1427176344L, // 360 (count matched 360 and Reversal; Reversal is 105, and difficulty agrees)
        [127] = 2625820422L, // From the Grave (as for code 109)
        [135] = 2861418269L, // Quick Draw (by elimination, Sharpshooter is 78)
        [152] = 580478179L,  // Hill Guardian (Firefight; was misread as 24 when the field was 7 bits)
        [165] = 2750622016L, // Breacher (Firefight and PvP; was misread as 37)
    };
}
