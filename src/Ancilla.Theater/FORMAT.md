# Halo Infinite Theater film format: research notes

What Ancilla.Theater has worked out about Theater films, with the evidence and how sure we are. The
findings come from **six version-41 films**: one Firefight match (13 minutes, 4 players) and five
PvP Capture the Flag matches (3–11 minutes, 8 players, mode category 9). The PvP films include players who quit,
players who joined late, and bots that filled in for quitters. `Ancilla.Theater inspect` re-runs every
check marked *checked*, and all six films pass all of them.

**Update:** five more PvP CTF films (4–13 minutes, from further back in match history) also pass
every check, eleven films in all. The counts quoted below (records, medals, players) are still from
the first six unless they say otherwise.

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
- **Type 10 is a tick marker** (checked): 60.0 per second, each followed by a type-0 packet (100%
  in all eleven films). **Type 0 is that tick's replication data**, from 1 byte (idle) to about
  36 KB. See [Per-tick packets](#per-tick-packets-types-10-and-0).
- **Packet time is match time** (checked on ten PvP films): an event's `TimeMs` in the summary
  equals (packet timestamp − the film's first packet timestamp) / 1000, plus a constant of −0.5 to
  +2.9 ms per film, with no measurable drift (under 1 ms per 100 s). Evidence: type-0 packets are
  4.3× their mean size on the tick of each death, and the largest packet within ±5 ticks of a death
  is within 10 ms of it for 1,157 of 1,188 deaths. A death falls in the last tick at or before its
  time. Since chunks start at their manifest offsets to within 12 ms, match time is also chunk
  offset time.
- Every replication chunk starts with types **1**, **2**, **6**, **8** and **12** (see
  [Leading packets](#leading-packets-of-each-replication-chunk) below), and ends with **7** (0 bytes,
  an end marker, observed). The first replication chunk carries types 8 and 12 twice (observed).
- The summary chunk is one **type 9** packet (1.35 MB) followed by type 7 (observed).

## Leading packets of each replication chunk

Checked on all eleven films (293 chunks). `inspect` runs these checks (`Format/SlotTable.cs`).

### Type 1: entity slot table (343,019 bytes)

It isn't a state snapshot. It's the game's **entity slot table**: which of 8,191 slots are in use,
a reuse counter per slot, a 64-bit value per used slot, and the allocation cursors. It holds **no
positions**: every set bit falls in a decoded field. The fixed size is the table's capacity, so
about 97% of it is zero. MSB-first bits:

```
bits 0 .. 647,088            table A: 8,191 records × 79 bits                 checked
bits 647,089 .. 2,743,984    table B: 8,191 records × 256 bits                checked
bits 2,743,985 .. 2,744,151  trailer: 5 × u32 allocation cursors, 7 zero bits checked
```

8,191 × (79 + 256) + 167 = 2,744,152 bits = 343,019 bytes exactly. Slot *s* has record *s* in both
tables.

**Table A record (79 bits)**

| Bits | Field | Confidence |
|---|---|---|
| 0–2, 6–11, 55–77 | always 0 | checked |
| 3 | in use | checked |
| 4 | optional group present: only on in-use slots, mostly slots 1024–1279, never on two snapshots in a row | checked (flag), observed (rest) |
| 5 | copy of bit 3 | checked |
| 12–13 | (generation − 1) mod 4 | checked |
| 14–45 | generation (u32), ≥ 1 | checked |
| 46–54 | 9-bit value, only when bit 4 is set (510, 428 and 444 are common) | checked (presence), meaning unknown |
| 78 | flag, only when bit 4 is set | checked (presence) |

**The generation is a per-slot reuse counter** (checked): it never goes down, and always goes up
when a free slot becomes used. A never-used slot reads 1.

**Table B record (256 bits):** a 64-bit value, then 192 bits that are always zero (checked). The
value is nonzero only on in-use slots (checked). It's mostly constant for an entity but changes on
bipeds; all player bipeds share one base pattern and Firefight AI another (observed). It isn't a
component mask tied to the header registry (tested, nothing found).

**Trailer (167 bits):** five u32 allocation cursors, for pools 0–511, 512–767, 768–1023, 1024–1279
and 1280–8190, then 7 zero bits (checked). Until a pool wraps, its cursor is 1 + the highest slot
used in it (0 if none); after that, whenever it moves, slot (cursor − 1) was allocated since the
previous snapshot (checked, 871 moves across eleven films). The first cursor is 123 in each film's
first snapshot (checked). Pools 0–511, 512–767 and 1024–1279 wrap in some films; 768–1023 and
1280–8190 never have.

*Correction:* the trailer was first read as starting at bit 2,744,000 with four cursors and a
24-bit "highest used slot >> 8". It starts 15 bits earlier, and that 24-bit field is the top of the
fifth cursor, whose low 8 bits looked like trailing data.

**Slot pools**

| Slots | Contents | Confidence |
|---|---|---|
| 0–122 | 123 static entities, in use in every snapshot. The header's `u32[123]` table probably has one entry per slot | checked (in use), guess (header link) |
| 123–511 | 1–17 allocations per film, but about 389 in one | observed |
| 512–767 | **bipeds: one new slot per spawn** (players, and AI in Firefight) | checked |
| 768–1023 | used only in Firefight (109 allocations), perhaps AI | observed |
| 1024–1279 | short-lived entities: 600–2,700 allocations per film, few alive at once | observed |
| 1280–8190 | everything else | observed |

**Bipeds** (checked): by the last snapshot, the allocations in pool 512–767 are 0–2 fewer than the
scoreboard's total spawns in all ten PvP films; the shortfall is spawns after the last snapshot.
In Firefight the AI share the pool (505 allocations against 11 player spawns). Observed: at each
snapshot, biped allocations equal 8 plus the summary deaths at least *x* seconds before the chunk
start, with *x* = 7.9–8.4 s, in films without bots or quitters. Bots' deaths aren't in the summary,
so films with bots are 1–2 out. If CTF respawn is 8 s, this suggests the summary's event clock lines
up with the film's chunk offsets, which the per-tick death spikes confirm (see
[Packet layer](#packet-layer-type-2-and-3-chunks)). The first eight bipeds appear in the first 20 s,
during the intro; play starts later (see [The match start](#the-match-start)).

### Types 2, 6, 8 and 12

| Type | Finding | Confidence |
|---|---|---|
| 2 | 133–227 KB. Its size tracks type 1's in-use slot count (correlation 0.93–0.998 in ten of eleven films; 0.70 in one), about 100–140 bytes per entity on top of 104–132 KB, so it probably holds **per-entity state**. Every film's starts `a0 00 00 00 00 00 00 0b 7f ff ff ff 98` | observed / guess |
| 6 | always 4 zero bytes | checked |
| 8 | **Not fixed size:** 12,544 bytes in Firefight, 17,328–26,547 in PvP (25,000 or more with all eight humans present). Starts with a big-endian u32 count of human players, then about 25,000 bits per player. It shrinks after a human quits. Many 128-bit windows match the header's bit stream where per-player data lives, so it's probably per-player state like the roster | checked (count ≤ humans), observed (rest) |
| 12 | big-endian u32 bot count; 4 bytes when it's 0, otherwise about 2,074–2,086 bytes per bot | checked (size rule), observed (per bot) |

*Correction:* earlier notes gave type 2 as 180–213 KB and type 8 as always 12,544 bytes, and called
type 1 a probable full state snapshot. Those sizes came from the Firefight film. A byte-aligned
float32 scan of type 2 found nothing distinguishable from noise; a bit-aligned search hasn't been
tried.

## Per-tick packets (types 10 and 0)

Findings on all eleven films. `inspect` checks the type-10 layout (`Format/TickMarker.cs`), that
each type 10 is followed by a type 0, and the time mapping (see [Packet layer](#packet-layer-type-2-and-3-chunks));
the rest are research-script findings.

### Type 10: one 6-bit value per player slot (checked)

```
32 slots, MSB-first: [present:1] then, if present, [value:6]
zero padding; payload length = ceil(bits / 8)
```

- The layout and zero tail hold for all but 5 of 315,632 packets; those 5 sit between identical
  neighbours and look like noise. *Correction:* type 10 isn't always 7 bytes. It's 7 in the
  4-player Firefight film and 9–11 in PvP, following the number of slots present.
- **Present slots follow the humans in the match** (observed): one film goes from 8 to 9 when its
  late joiner arrives, others drop as players quit, and bots never appear.
- **Values** (observed): every slot reads 60 for about the first 100 ticks, and for long stretches
  on what look like quitters' slots. Otherwise they're 0–14, nearly constant per slot, and jitter by
  ±1. They don't change at the player's deaths. Per-player network latency in ticks fits (guess).
  Slots haven't been mapped to roster players.

### Type 0: header and tick counter (observed)

```
bits 0–28   10100000011110110100001000000   constant prefix (73–79% of packets)
bits 29–36  tick counter, u8, +1 per tick, wraps at 256
```

- After the first 10 s, the counter steps by exactly the number of packets in six films. The other
  five have 1–5 mismatches each (17 in all). 12 of them match gaps in the timestamps, so those
  ticks are missing from the film (up to 13 ticks, 213 ms). The rest are unexplained.
- For the first 2.5–7 s the counter steps by 4 per packet.
- Packet timestamps jitter by about 33 ms at chunk boundaries, so for tick-exact work, count ticks
  rather than using timestamps.
- A packet is a run of records, each starting with a 1 bit, ending with a 0 bit and zero padding.
  The 1-byte payload `0x80` is an idle tick. Record boundaries aren't decoded in general.
- **Per-player list at the end of each packet:** `[1][index:8][21 bits]` per player, index
  ascending, then `0`. The 21 bits take few distinct states, so they're status bits, not
  positions. **A player's entry is missing exactly while they're dead** (research-script check,
  nine of ten PvP films): the index equals the roster index (scoreboard order among humans) in
  seven films; in one with a late joiner the indexes are shifted, in another a late joiner reuses a
  quitter's index, and one film with a quitter, a late joiner and a bot is ambiguous for one
  player. Type-10 slots don't follow anyone's deaths and remain unmapped.

### Entity records and player positions

Type-0 records update one entity each, addressed by its **type-1 slot**, and list the components
they carry by their index in the header registry's block for that entity class
(`Format/EntityRecords.cs`; `inspect` runs the position checks below on every PvP film):

```
[1]        record start (1 in 99.7% of biped records)
[slot:13]  type-1 slot: bipeds 512–767, dropped objects 1280 and up
[0][1]     fixed
[n:5]      number of components in this update
n × [c:6]  component indexes, strictly ascending: slots in the registry block for the entity's
           class (block 35 for bipeds)
data       per component, in list order. Component 0, position:
             bipeds (object-position-dynamic-precision): [00] [F1:18] [F2:15] [F3:18]
             dropped objects (object-position):              [F1:18] [F2:15] [F3:18]
```

- **The slot is real** (observed): the 13 bits before a dropped object's component list name a
  slot in use in the surrounding type-1 snapshots in 86.5% of 22,611 records (3.1% by chance). For
  bipeds, an in-use biped slot is followed by coordinates at the expected distance 16× more often
  than an unused one.
- **Common biped component lists:** (0, 1, 19, 25), (0, 1, 25), (0, 1, 21, 25), (0, 1, 5, 21, 25),
  (0, 25). In block 35: 0 position, 1 velocity, 4/5 body/shield vitality, 19/20 AI actor control and
  state (Firefight only), 21 desired aiming vector, 25 command tick, 32/35 weapon state. All 41
  indexes seen are block-35 slots. Only position has been decoded.
- **The 2 bits before biped coordinates** are `00` in 18,447 of 18,463 records; perhaps a precision
  selector (guess, the other values are untested).
- **Field widths:** F1 = 18 bits is certain (a 17-bit split makes F2 jump in 43–45% of steps); F2 =
  15 bits is the only split whose steps match F1's; F3's lowest bit is always 0. Each biped updates
  every few ticks (about 2.7 position records per tick in an 8-player film).

**Player positions** (checked on ten PvP films by `inspect`, which prints the respawn timing without checking it):

| Check | Result |
|---|---|
| A summary death ends the victim's biped-slot track within 3 ticks | 1,154 of 1,188 deaths (35 with deaths shifted by 2 s); tracks end on the death tick or the one before |
| A new biped slot starts 7.95–8.25 s after the death | 70–98% per film, 5–30× the rate in an equal-width window at 4 s; 118 of 137 at 8,000–8,099 ms in one film. One film spreads from 7.5 to 9 s (perhaps a different respawn setting) and fails this check only |
| Objects dropped at a death land on the victim's last position | horizontal median 16–32 units, against 326–627 for random pairs. Height is +68 to +92 units, so the biped position is probably the feet (guess) |
| Tick-to-tick steps of 6 units or fewer | 93–96% |
| Bipeds with positions per second ≈ humans present minus the recently dead | within 25% |
| A new slot's first position is at the dying player's team base | 93–100% |

**Scale and axes are unknown.** F3 is vertical (small range; objects settle at a floor value). If a
Spartan runs about 7 world units a second, 1 unit ≈ 0.05 world units (guess). A custom game walking
a known line would settle it; so might decoding velocity (component 1) against the change in
position.

**Which player is which** (`Format/Lives.cs`, checked by `inspect`): a life that ends in a death is
the slot that death ends; the life after a player's last death is the new slot nearest 8 s after
it. No player ever gets more lives than their scoreboard spawns, and 64 of 84 PvP players get
exactly as many; the rest are short, mostly a life with no death at either end (a player who never
died, a quitter). Pairing every death with the new slot nearest 8 s later agrees with the slot the
player's next death ends in 85–100% of cases per film, so deaths are trusted first. Not yet used:
the end-of-packet per-player list (below), which could identify the remaining lives.

*Correction:* a "second record family" thought to carry coordinates after prefixes ending
`0101100100` was the tail of biped component lists. Tracking it without the slot ID failed.

### Coordinates of objects dropped at deaths

Dropped-object records are the generic record above with n = 4 and components 0–3 (position,
velocity, forward-and-up, angular velocity), giving the constant pattern
`0 1 00100 000000 000001 000010 000011` after the slot. They were found first, as a 30-bit pattern
after a 10-bit "even ID"; the ID is the low bits of the 13-bit slot. After the pattern:

```
+0    18 bits  F1  coordinate (values 9,600–26,500)
+18   15 bits  F2  coordinate (values 9,600–22,100)
+33   18 bits  F3  coordinate, probably vertical; lowest bit always 0 (1 exception in 789,000)
then  unknown, variable
```

- **Created at deaths:** in each PvP film, 186–417 new IDs first appear within 1 s after a death,
  against 37–125 at times shifted by 3.7 s. That's about 4 per death: dropped weapons, grenades and
  equipment fit.
- **Smooth motion:** about 99% of tick-to-tick steps of the same object are within 4 units (F1, F2)
  or 8 (F3). Falling objects settle at a floor value of F3.
- **Same map, same ranges:** two films on the same map have matching medians and floor values, and
  other maps differ.
- **Objects from one death sit together:** their spread, relative to the spread between deaths,
  is 0.02–0.36 per film and axis. The ratio resets at bit offsets 0, 18 and 33, which is how the
  field starts were found.
- They land on the victim's last biped position (see the table above).

**Ruled out:** float32 positions at any bit offset (MSB-first) or byte offset (little-endian) in
type 0. Packet sizes show no respawn signal 4–12 s after deaths.

### The match start

Bipeds exist, and players are listed in type 0, from 9.6–30 s into the film, but packets stay
small (about 2 KB/s): an intro. **Play starts 27–40 s in**, when the data rate jumps more than 4×.
In the original six films that's 2–6 s before the first event.

## Header (type-1 chunk)

The header is **not** made of packets.

```
u32      film version (41)                                   checked
u32      used slots in block 0                                checked
blocks   50 × 16,640 bytes = 64 slots × 260 bytes             observed
           slot: char name[256] (ASCII, zero-padded), u32 value (1, 2, 4, ...; meaning unknown)
           some blocks are entirely empty
u32[123] small values (1–6), identical in all eleven films;    observed
           probably one per static entity slot (see type 1)    guess
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
+376              7     event code: 10 = death, 25 = killed a player, 5 = objective; for medals, a tier (below)
+384              32    time into the match, milliseconds
+447              1     medal flag
+472              8     medals: medal code, the game's own numbering (see MedalCodes.cs)
                        otherwise: record type, 0 = kill, 1 = death, 2 = objective
```

**The +472 byte is a record type on non-medal records** (checked, eleven films): kills (code 25)
always have 0, deaths (10) 1 and objective events (5) 2, and no other code or pairing occurs.

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
(`inspect --learn`), and accepted only when exactly one medal fits. Codes 0–4 run Double Kill to
Killtrocity and 9–10 run Killing Spree to Killing Frenzy, the game's own order. The Firefight film
has 13 codes its scoreboard alone can't resolve.

There are **47 codes**. Some were accepted **by elimination**: every other fitting medal already
had a code, or two films' candidate lists overlapped in exactly one medal (78 = Sharpshooter). The
five newer films added 112 = Ballista (the only fit), and three by elimination: 118 = Interlinked
(also the only fit whose difficulty matches its event code, 75), 120 = Chain Reaction (Bodyguard is
77 or 81) and 131 = Last Shot (Breacher is 165). Still open:
- **77 and 81** are Scattergunner and Bodyguard, but which is which isn't known yet.
- **6, 7 and 8** each fit Killtastrophe, Killpocalypse, Killionaire or Saboteur. Their event codes
  (120, 122, 125) rise like the mythic multikills above, so codes 0–8 probably run Double Kill to
  Killionaire (guess).
- **11, 12 and 27–30** each fit the six mythic spree medals, Running Riot to Demon. Their event
  codes are 102, 105, 110, 115, 120 and 125, so 11 and 12 are probably Running Riot and Rampage,
  following 9–10 (guess).

### Objective events (code 5)

Code 5 (record type 2) is a **mode-objective event**. The record doesn't say which action it was;
nothing else in it varies.

- **CTF** (checked, 82 players in ten films): each player's code-5 count equals their
  `FlagSteals + FlagCaptures + FlagCaptureAssists + FlagReturns + FlagCarriersKilled`, or one
  more (12 players). Those 12 all have at least one `FlagGrabs` and one `FlagSecures`, so some flag
  pickups log one too; which ones isn't known.
- **Carrier kills** (checked): a code-5 event that shares a moment (5 ms) with a kill is always at
  the player's own kill, never another player's, and those never exceed the player's
  `FlagCarriersKilled` (42 against 43 in all). In 39 of 41 such kills the victim had a code-5 event
  earlier in that life (was carrying the flag), against 8% of other kills (observed).
- **Captures** (observed): same-team code-5 events at one moment fit a capture plus its assists;
  the extra group members equal `FlagCaptureAssists` in 7 of 10 films and are 1–2 above in three.
- **Firefight** (checked on its one film): code-5 events come in as many bursts as each player's
  `objectivesCompleted` (5), and within a burst each player's events are 5.003–5.009 s apart
  (95 of 102 gaps between 4.5 and 5.5 s). A 5-second award for holding a zone fits (guess). PvP has
  no such period.

So for a kill feed, a code-5 event at a player's own kill is a carrier kill, a same-team group at
one moment is a capture with assists, and the rest are steals, returns or pickups, which would need
a flag-state model to tell apart (untested).

*Correction:* code 5 was earlier compared only with the 43 core stats, which none matched, and
guessed to be a timed map event such as a power weapon.

**Still unknown:**
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
3. ~~Work out code 5.~~ Done: objective events (see above). Next: label CTF flag actions with a
   flag-state model per team, checked against per-player steals, returns and captures; and see
   what code 5 means in other modes (Oddball, Strongholds).
4. Replication stream: fill the remaining lives from the end-of-packet per-player list
   (`timeline` already shows distances and a death map from the lives found); decode velocity (component 1), aiming (21) and command tick
   (25). A controlled recording (walk a known line) would fix the coordinate scale and axes.

## Prior art
Others who have worked on this format:

- [den.dev: Extracting match stats from Halo Infinite film files](https://den.dev/blog/extracting-stats-film-files-halo-infinite/) (Den Delimarsky)
- [SPNKr](https://github.com/acurtis166/SPNKr) (Andy Curtis)
- [halo_api](https://github.com/nuzzles/halo_api), and [halo-theater](https://github.com/games-directory/halo-theater), built on it
- [libfilmshell](https://github.com/OpenSpartan/libfilmshell) (OpenSpartan)
- [LevelUp](https://github.com/JGtm/LevelUp) (a fork of Guillaume Sitbon's LevelUp)
- [Spencer Imbleau: Reverse-engineering Halo: Infinite's Theater films](https://spencer.imbleau.com/blog/ai/rust/2026/09/14/halo-infinite-theater-format-astra.html)
