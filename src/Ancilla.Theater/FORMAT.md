# Halo Infinite Theater film format: research notes

What Ancilla.Theater has worked out about Theater films, with the evidence and how sure we are. The
findings come from **six version-41 films**: one Firefight match (13 minutes, 4 players) and five
PvP Capture the Flag matches (3–11 minutes, 8 players, mode category 9). The PvP films include players who quit,
players who joined late, and bots that filled in for quitters. `Ancilla.Theater inspect` re-runs every
check marked *checked*, and all six films pass all of them.

Confidence levels:
- **Checked:** verified by an automated check that would fail if it were wrong.
- **Observed:** seen consistently in the data, with no check yet.
- **Guess:** a plausible reading that hasn't been tested.

## Getting a film

| Step | Detail | Confidence |
|---|---|---|
| Manifest | `GET discovery-infiniteugc.svc.halowaypoint.com/hi/films/matches/{matchId}/spectate`, needs the Spartan token | Checked |
| Chunks | `{BlobStoragePathPrefix}{FileRelativePath}` on `blobs-infiniteugc.svc.halowaypoint.com`. **Served without the token**, so anyone with the URL can download a film | Observed |
| Compression | Each chunk is a zlib stream (`78 5E` header) | Checked |
| Chunk types | 1 = header (one, at the start), 2 = replication (about 20 s each), 3 = summary (one, at the end) | Observed |
| Version | `CustomData.FilmMajorVersion` is 41, and the header's first u32 agrees | Checked |

## Packet layer (type 2 and 3 chunks)

Every decompressed type-2 and type-3 chunk is a run of packets with no gaps: walking them uses every
byte exactly (**checked**).

```
offset  size  field
0       2     packet type (u16, little-endian)
2       2     unknown (u16): often 0, sometimes small or large values
4       4     payload length (u32)
8       8     timestamp, microseconds (u64)
16      n     payload
```

- **Timestamps are 64-bit microseconds** (checked): they never go backwards across a whole film,
  each chunk's packets span its manifest `DurationMilliseconds` to within 25 ms, and each chunk
  starts at its manifest offset to within 12 ms.
- The clock doesn't start at zero: films start at 0.3–3.8 hours on it, perhaps server uptime
  (guess). *Correction:* bytes 12–15 were first read as a separate field that was constant per
  film (0, 1 or 3). They're the timestamp's high word: in one film it ticks from 0 to 1 at exactly
  the packet where the low word wraps.
- **Type 10 is a tick marker** (checked): always 7 bytes, at 60.0 per second, each followed by a
  type-0 packet. **Type 0 is presumably that tick's replication data**, from 1 byte (idle) to about
  36 KB (guess).
- Every replication chunk starts with types **1** (always 343,019 bytes, probably a full state
  snapshot), **2** (180–213 KB), **6** (4 bytes), **8** (always 12,544 bytes) and **12** (4 bytes),
  and ends with **7** (0 bytes, an end marker). Observed; their meanings are guesses.
- The summary chunk is one **type 9** packet (1.35 MB) followed by type 7 (observed).

## Header (type-1 chunk)

The header is **not** made of packets.

```
u32      film version (41)                                   checked
u32      used slots in block 0                                checked
blocks   50 × 16,640 bytes = 64 slots × 260 bytes             observed
           slot: char name[256] (ASCII, zero-padded), u32 value (1, 2, 4, ...; meaning unknown)
           some blocks are entirely empty
u32[123] small values (1–6), meaning unknown                  observed
char[32] build version   "6.10026.19225.0"                    observed
char[32] branch          "HI_1_13_0"
char[32] flavour         "release"
u32, u32 two build-related numbers
...      bit-packed stream, MSB-first, containing the roster  checked (roster)
```

### Component registry
The blocks hold **325 distinct component names** in 1,067 slots. Each block seems to list the
components of one kind of entity: game engine state, players, stats ("statborg"), Forge objects,
effects, sound, navpoints, objects, weapons, vehicles, projectiles. Useful names for later work:

- Movement: `object-position-component`, `object-position-dynamic-precision-component`,
  `object-translational-velocity-component`
- Aim: `unit-desired-aiming-vector-component`, `player-control-aiming-component`
- Loadout: `biped-desired-weapon-set`, `weapon-state-type-info`, `weapon-ammo-component`,
  `unit-grenade-counts-component`, `unit-equipment-component`

The registry **names** components but doesn't describe their fields. Field layouts have to be
worked out from the replication data.

### Roster
- **Players appear in scoreboard order** (checked), one record each, about 28,000 bits apart, or
  about 16,600 for a shorter record variant.
- **Each record starts with the player's XUID**, a 64-bit MSB-first value (checked: every player in
  all six films found by scanning for XUID-range values, `0x0009_xxxx_xxxx_xxxx`, without knowing them
  in advance). *Correction:* the scan's upper bound used to be 2,535,999,999,999,999, which missed a
  real XUID starting 2703…
- **The record then holds two identical copies of the gamertag**, each a 16-character UTF-16LE field,
  about 15,300 bits apart (checked: 44 players). The first copy is 11,896 to 12,342 bits after the
  XUID in long records, and 936 in short ones; Ancilla.Theater only accepts names in those windows.
- **The name ends at the first NUL.** Usually the rest of the field is zeros, but in two films one
  player's first copy had stray non-zero bytes after the NUL while the second copy was clean
  (observed). Ancilla.Theater requires at least one clean copy.
- **Players who join after the start aren't in the header** (checked: inspect skips scoreboard
  players with `joinedInProgress` that it can't find). Players who joined in the first seconds are
  there. Bots aren't in it either.
- *Correction:* an earlier note said the name sat 469 bits *before* the XUID. That was the previous
  player's second copy. The name follows its own XUID.
- **Open question:** with the old, narrow range there were 20 to 30 other XUID-range values in each
  stream, far more than chance would produce. Several sit about 6,435 bits into a player's record
  and differ from a real XUID by about 4×10⁷. With the full `0x0009` range there are 480–550, which
  is mostly chance. Ancilla.Theater tells real XUIDs apart by where the name copies fall.

## Summary (type-3 chunk): decoded

The summary is one **type-9 packet** (about 1.4 MB): a run of per-player records, bit-packed
MSB-first, **in time order** (checked). Each record holds its player's 16-character gamertag field
(the same encoding as the header), and the other fields sit at fixed offsets from it.

### Record layout (checked)

`SummaryRecords.Walk` reads the payload from bit 0 with no roster. On all six films it reproduces
the name-based decode record for record, and every check below holds:

```
u32                  record count (checked: equals the records walked, in all six films)
per record:
  body               the player's header record from just after its XUID: bit-identical to the
                     header for its first ~12,250 bits (long) or ~780 (short), then ~15,000 bits
                     that differ from the header but are the same in every record of that player.
                     Length = header XUID-to-name offset + 15,030 bits.
  char16[16]         gamertag, NUL-terminated, zero-padded
  224 bits           event data (offsets below are from the name)
  u32                always 12000 (checked; meaning unknown)
0–6 bits             padding to the byte
```

- **Records are mostly a fixed per-player blob.** Records are about 27,900 or 16,500 bits long,
  and the length depends only on the player, set by the header record layout. What looked like
  "most of the bits after the name change" was the next record's blob, which belongs to a
  different player.
- **Only the known fields change.** Across 1,470 PvP records, the only bits that ever differ
  between records of the same player are +376–383, +384–415 (the time), +447 and +472–479. Nothing in the
  blob varies.
- **Bit +383** is set in one record only: a Killtacular, event code 112. Either the event code is
  8 bits (reading 225), or this is a separate flag (observed, one sample).
- **Kill records hold no positions, weapons or damage**: every bit outside the known fields is the
  same for every event of that player, and the records fill the payload. Ruled out: float32 and
  quantised position fields, and weapon codes. Positions and weapons have to come from the
  replication stream (type-0 packets).
- **Weapons from medals:** 78 of 534 PvP kills (15%) have a same-player medal within 2 ms that
  hints at the weapon or method, such as Snipe, No Scope, Back Smack, Bomber or Grand Slam. Most
  kills carry no such hint.
- **Late joiners are decoded** (checked). Walking finds their records, with their gamertag,
  without the header roster. The body starts after the XUID, so it carries none. Instead, each
  gamertag not in the header is linked to a scoreboard player who joined in progress and has the
  same kills, deaths and medals (`FilmPlayers.Resolve`), and only when exactly one fits.
  `inspect` fails if any gamertag can't be linked. In one film this pairs the 2 kills that had
  no victim with the late joiner's 2 death records, 0–1 ms later, so every kill there now has a
  victim.

```
offset from name  bits  field
+376              7     event code: 10 = death, 25 = killed a player; for medals, a tier (below)
+384              32    time into the match, milliseconds
+447              1     medal flag
+472              8     medal code, the game's own numbering (see MedalCodes.cs)
```

*Correction:* the time was first read as 20 bits at +396, which would wrap at 17:28.6. The 12
bits above it (+384–395) are zero in all 1,923 records of six films, so the field is the whole u32
at +384. No film yet runs past 17.5 minutes, so the upper bits are unconfirmed in use.

*Correction:* the medal code was first read as 7 bits at +473. Codes of 128 and above (135, 152,
165) showed the extra bit; two table entries learned as 24 and 37 were really 152 and 165.

**Medal event codes** (checked, 537 medal records in six films): 25, 50 or 75 for normal, heroic
and legendary medals, as the metadata's `difficultyIndex` says. Mythic medals each have a fixed value
of 100 or more: Extermination and Perfection 100, Overkill 110, Killtacular 112, Killamanjaro 117.
The same medal always has the same value, in PvP and Firefight alike.

Checked against the scoreboard, for every player in all six films:
- **Deaths** (code 10) match exactly.
- **Player kills** (code 25, not a medal) match the scoreboard's PvP kills. AI kills in Firefight
  aren't recorded as events.
- **Every kill of a player in the roster is followed by the victim's death record**, 0–1 ms later,
  and every victim is on the other team. That gives **who killed whom, and when**.
- **Bots and late joiners** (checked):
  - **Bot deaths have no record at all.** A kill of a bot is a kill record with no death after it.
  - **Late joiners do have records** (often the short, 16,500-bit kind). Their name isn't in the
    header roster, but `SummaryRecords.Walk` finds them by layout and they're linked by their
    counts (see Record layout). A late joiner with no kills, deaths or medals has no records.
  - So the kills left without a victim equal the deaths of bots (and of any unlinked player),
    less their suicides, in every film.
  - The deaths left without a killer equal the scoreboard's suicides plus the kills made by bots
    (and by any unlinked player).
  - Bot kills and kills of bots count towards the scoreboard's PvP kills.
- **Summary records don't contain XUIDs** (checked by search), so identity comes only from the
  gamertag, matched against the header roster.
- **Medal counts** match, and so do the medals themselves for every code in the table.

**Medal codes.** They follow neither the order of the published medal metadata nor its sprite
indexes. Each one is learned by matching per-player code counts against the scoreboard
(`inspect --learn`), and accepted only when exactly one medal fits. There are **30 codes so far**. Codes
0–4 run Double Kill to Killtrocity and 9–10 run Killing Spree to Killing Frenzy, the game's own
order. The Firefight film has 13 codes its scoreboard alone can't resolve.

There are now **43 codes**. Some were accepted **by elimination**: every other fitting medal already
had a code, or two films' candidate lists overlapped in exactly one medal (78 = Sharpshooter). Still
open:
- **77 and 81** are Scattergunner and Bodyguard, but which is which isn't known yet.
- **6, 7 and 8** each fit Killtastrophe, Killpocalypse, Killionaire or Saboteur. Their event codes
  (120, 122, 125) rise like the mythic multikills above, so codes 0–8 probably run Double Kill to
  Killionaire (guess).
- **11, 12 and 27–30** each fit the six mythic spree medals, Running Riot to Demon. Their event
  codes are 102, 105, 110, 115, 120 and 125, so 11 and 12 are probably Running Riot and Rampage,
  following 9–10 (guess).

**Still unknown:**
- **Event code 5** is not a medal: 1–8 per player in PvP, and 9–65 in Firefight. Its per-player
  counts match none of the scoreboard's 43 numeric stats, assists included. Observed in 98 PvP
  records:
  - 21 share a millisecond with the same player's kill.
  - 18 come in same-team groups at one moment (up to three teammates).
  - 59 have no event within 5 ms.
  - They come in bursts a few minutes apart (for example 91–110 s, 327–368 s and 528–581 s in one
    film), and are often followed by the same player's death within seconds.

  A timed map event, such as power-weapon or power-up control, fits; that's a guess.
- **Linking late joiners when counts tie.** Two late joiners with the same kills, deaths and
  medals can't be told apart by counts. That hasn't happened yet; the profile service (gamertag
  to XUID) would settle it.
- **The u32 12000** after every record, and what makes the ~15,000-bit part of the blob differ
  from the header.

## Next steps
1. Run `inspect --learn` across more films (other modes, ranked) to fill in the medal table, and to
   check that the record layouts hold. All recent PvP matches so far are CTF (mode category 9).
2. ~~Decode more of each summary record.~~ Done: summary records hold no positions or weapons.
   For both, decode the replication stream, starting with `object-position-component` (step 4).
3. Work out code 5: compare its times with power-weapon spawn timers on a known map, or record a
   custom game where one player picks up a power weapon.
4. Start on type-0 replication packets with controlled recordings: short custom games where one
   thing changes (stand still, walk in a line, fire once).

## Prior art
Others who have worked on this format:

- [den.dev: Extracting match stats from Halo Infinite film files](https://den.dev/blog/extracting-stats-film-files-halo-infinite/) (Den Delimarsky)
- [SPNKr](https://github.com/acurtis166/SPNKr) (Andy Curtis)
- [halo_api](https://github.com/nuzzles/halo_api), and [halo-theater](https://github.com/games-directory/halo-theater), built on it
- [libfilmshell](https://github.com/OpenSpartan/libfilmshell) (OpenSpartan)
- [LevelUp](https://github.com/JGtm/LevelUp) (a fork of Guillaume Sitbon's LevelUp)
- [Spencer Imbleau: Reverse-engineering Halo: Infinite's Theater films](https://spencer.imbleau.com/blog/ai/rust/2026/09/14/halo-infinite-theater-format-astra.html)
