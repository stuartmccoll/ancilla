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
| Chunks | `{BlobStoragePathPrefix}{FileRelativePath}` on `blobs-infiniteugc.svc.halowaypoint.com` (matchmade) or `s3infiniteugcsessions.blob.core.windows.net` (custom games). **Served without the token**, so anyone with the URL can download a film. Custom game paths carry a read-only Azure SAS query string valid for about a day (`/filmChunk1?sv=…&sig=…`) | Observed |
| Compression | Each chunk is a zlib stream (`78 5E` header) | Checked |
| Chunk types | 1 = header (one, at the start), 2 = replication (about 20 s each), 3 = summary (one, at the end). A custom game whose manifest says `HasGameEnded: false` had no summary chunk | Observed |
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
- **Per-player list at the end of each packet** (`Format/PlayerList.cs`; `inspect` checks it on
  every PvP film): one entry per player who is alive, index ascending, then a `0` bit and fewer
  than 8 zero bits of padding:

  ```
  [1] [index:8] [status:21]          status starts 11; its other bits aren't decoded, and are nearly constant per player
                [5 more bits]        when status bit 18 is 1 (bits counted from 0, MSB first)
                  [16 more bits]     when the last of those 5 bits is 1
                [16 more bits]       when status bit 19 is 1
  ```

  The list's start isn't marked. Taking the earliest start that reads exactly to the padding finds
  junk chains running back into the records before the list (indexes up to 255) in 99.9% of
  packets, so the parse also requires every status to start `11`, which no junk chain met in the
  packets dumped. With that rule the list parses in 88–97% of type-0 packets per film (2026-11-18).
  *Correction:* the nested 16 bits were missing from the earlier reading. Without them, an entry
  whose 5-bit extension ended in 1 broke the parse for several seconds at a time, and every lower
  index dropped out with it (several players "dying" together mid-life). Adding them cut the
  positions of chained lives that fall where the list says the player is dead by a third to a half
  (F12 12,586 → 8,216, F10 3,816 → 2,005 in a test of two films). Treating bit 1 = 0 entries as 3 bits
  shorter, or the extension's other bits as the flag, did less or worse. Packets that don't parse
  keep the previous packet's list, and presence changes shorter than 60 ticks (1 s) are ignored.
  **A player's entry is missing exactly while they're dead:** a summary death ends one of that
  player's alive spans within 0.5 s for 84–93% of deaths per film (`inspect`). An index appears
  a few ticks (up to about 20) after the life's first biped record and disappears 1 tick after its last. Players are
  also listed during the intro (see [The match start](#the-match-start)), before any biped moves.
- **Index to player** (`PlayerList.Spans`): each alive span that ends within 0.5 s of a death is
  that player's, nearest first. A span with no death at its end (a life running to the end of the
  film, or a quitter's last) goes to whoever owns the last claimed span before it on the same
  index. So a late joiner who takes over a quitter's index gets their own lives, not the
  quitter's. Indexes are the scoreboard order among humans in eight of eleven films; in one
  they're shifted by one (a late joiner), and in two a late joiner reuses a quitter's index. One
  human (a player with one death, in a 9-human film) can't be matched, so their state stays unknown.
- **Where the list and the biped tracks disagree:** 1.4–7.4% of chained lives' positions fall
  where the list says that player is dead (`inspect`, 11 PvP films). Most are inside lives, not at
  their ends, so the list still loses players now and then (another entry variant, probably).
  The timeline's animation therefore counts any frame with a position as alive, and uses the list
  only where there's no position (`MatchTimeline.BuildPlayback`).
- **Coverage: the "missing biped tracks" mostly weren't missing** (2026-11-18, research scripts).
  Measured against the list, chained tracks covered 77% and 69% of alive time in two films
  (65% in an earlier measure). But that counted the intro, the first 10–50 s of each film, when players are
  listed and no biped has moved. Counted from play start (when half the players' first tracks
  have begun), the two films are at 85% and 88%, and the other nine at 91–99.8%. What's left:
  - *An idle player* (0 kills, 0 shots, 0 damage dealt, 9 deaths, 22% covered). Their biped's
    records are almost all shield-only (component 5): 532 of 635 records in one life. There are
    hardly any positions, because positions are only sent when the biped moves. Below 30 positions
    a slot is dropped as a chance match (`FilterBipeds`).
  - *A player whose biped all but stops updating* (52% covered, active earlier in the match):
    their slot has 2–22 records of any kind across three lives (24 s to 144 s), against hundreds
    a life for everyone else. A scan of every header naming the slot, without the start-bit or
    component-0 rules, finds nothing more, and no other slot (vehicle-like or otherwise) shows up
    beyond those in a fully covered film. The data isn't in the film. Why isn't known.
  - *Tracks that exist but aren't linked*: in one film only 0.8% of player-time has no track at
    all, so the rest of its gap is linking (`Format/Lives.cs`), not finding.
  So `EntityRecords.Find` doesn't skip records during these lives: every biped slot active in the
  windows checked had its positions read, and position-plus-tick records land the command tick
  in 99% of cases in both films, as in the others.
- **Linking lives from the list (2026-11-18; `Lives.FillFromSpans`, reported by `inspect`):** across
  15 PvP films the chaining finds 1,677 of 1,713 scoreboard spawns; 36 are missing. For each missing
  life, the list's alive span says when it was. Only 5 have exactly one unclaimed biped track
  starting within 1 s of the span's start and ending with it; `FillFromSpans` links those, and 3 of
  the 4 that end in a death end within 1 s of it (the fourth, 4 s early). The rest have no unclaimed
  biped records during the span at all (one player alone has 6 such lives: the slot-goes-silent case
  above), or are first lives at the start of play, when several players' tracks begin together.
  So the remaining gap is missing data, not linking.

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
  indexes seen are block-35 slots. Position, aim, command tick and ammo are decoded (below).
- **The 2 bits before biped coordinates** are `00` in 18,447 of 18,463 records; perhaps a precision
  selector (guess, the other values are untested).
- **Field widths:** F1 = 18 bits is certain (a 17-bit split makes F2 jump in 43–45% of steps); F2 =
  15 bits is the only split whose steps match F1's; F3's lowest bit is always 0. Each biped updates
  every few ticks (about 2.7 position records per tick in an 8-player film).

**Biped component widths** (`EntityRecords.FindBipedUpdates` reads them; `inspect` checks the tick, aim and ammo). The command
tick works as a marker: its value is predictable, so finding it shows where the components before it
end. Every common component list lands on these sums (one CTF film, 10 lists checked):

| Component | Bits | Notes |
|---|---|---|
| 0 position | 54 | `[00]` + 51 coordinate bits + 1 bit, always 0 when nothing follows but the tick. The custom game's is 57 |
| 1 velocity | 31 or 2 | `[00] [sign:1] [direction:18] [speed:10]` (below), or `[01]` alone: standing still |
| 5 shield vitality | 29 | not decoded |
| 21 aim | 25 | `[1] [yaw:12] [pitch:12]`, 4,096 units to a full turn (controlled test below) |
| 25 command tick | 9 | `[1] [tick:8]`, the game tick mod 256, then a `0` when more components follow |
| 30/33/36/39 ammo | 9, 14 or 2 | `[0] [rounds:8]`; `[10] [value:12]`, a share of a battery (probably); or `[11]` |
| 31/34/37/40 reserve | 11 | `weapon-state-rounds-inventory`, not decoded; for weapons with a round count it's 1,024 plus a small number |
| 42 desired weapon set | 7 | `[?:3] [inventory slot wanted:3] [?:1]` (see "The flag slot"); first read as 8 bits, which took in type-info's presence bit |
| 43–46 type-info | varies | `[1] [item ID:32] [variant:32] …` or, empty, `[0] [stamped:1] …` (see "Which weapon", "The flag slot"); reading stops after the variant |

- **Command tick (checked on all 12 matchmade films):** in every (0, 25) record the 8 bits equal
  the type-0 packet count plus a per-film offset. The offset steps up by 1–4 a few times in most
  films. Those are probably ticks missing from the film, so the command tick can repair its clock.
  With the widths above, the tick lands where expected in 99% of records carrying an ammo
  component (14 misses in 4,236 in one film).
- **A `0` follows the command tick when another component comes after it** (checked): in
  198,300 of 198,876 such records across the matchmade films. That's why an ammo count reads as
  `[00] [count:8]` after the tick but `[0] [count:8]` in a record with the ammo component alone.
- **`inspect` checks** (every film with a summary, 2026-11-17): the command tick is the packet
  count plus one of the film's four most common offsets in 98.8–99.9% of records whose tick lands;
  pitch is within 90° of level in 99.9%; yaw moves less than 22.5° between a slot's updates up to
  3 ticks apart in 99.3–99.9%; 96–98% of drops in a slot's checked ammo counts are one round.
- **Velocity (component 1, checked on 13 films by `inspect`; `BipedVelocity`):** 31 bits when it
  starts `00`, with or without a position before it:

  ```
  [00] [sign:1] [direction:18] [speed:10]
  direction = face × 87,381 + u × 294 + v    face 0/1/2: X, Y or Z is the largest component
  sign: that component's sign; u, v (0–293, 146.5 = 0): the other two components ÷ it, in X, Y, Z order
  speed: exponent 4 bits, mantissa 6 bits; steps = mantissa if the exponent is 0, else (64 + mantissa) << (exponent − 1)
  ```

  The direction is a point on a cube's surface: three faces of 87,381 (2^18 ÷ 3) values, each a
  294 × 294 grid. Found by tabulating the top bits against the direction of movement (each face
  covers one axis's ±45°), then fitting the grid size: 294 is the only one where the second
  component appears on all three faces. The speed is a small float: read as an integer, speed ÷
  value drifts by 70% across the range; read as a float, it's constant. **One step is half a film
  unit per second** on all three axes: in windows where the velocity holds steady, the chord
  between their ends matches it with a median horizontal direction error of 0.8–0.9° and a
  horizontal speed ratio of 0.97–0.99 on every film (the 11 PvP films, Firefight and a custom game;
  4,780–26,883 runs each).
  *Correction:* "velocity without a position has another width" was the other form. A velocity
  starting `01` (most of the rare velocity-only records, about 1,000 a film against about 190,000
  position-and-velocity records) is longer: the command tick lands 99 or 136 bits later in some,
  nowhere consistent in the rest. Velocity starting `00` is 31 bits with or without a position.
  *Correction (2026-11-18):* **`01` is the whole velocity: 2 bits, meaning standing still.** The
  earlier search allowed only the tick's marker bit; matching all 8 tick bits against the packet
  count (a 1-in-256 chance) puts the tick straight after the `01` in 18,302 of 18,615 records
  (16 films; the next most common spot, 2,925). After a `01` record the biped moves a median 0 film
  units a tick by its next record (90th percentile 1), against 3.0 for moving records. `inspect`
  checks it: 90–98% per film stay within 1 film unit a tick.
- **What velocity says about the scale:** it doesn't give the world scale, because it's stored in
  film units too: half an X/Y film unit per second per step, a power of two. Vertically, film Z
  moves about 2.8 times as fast as the velocity says (median 2.76–2.84 in three films), so **film Z
  has its own scale, about 2.8 times X and Y's**. That matches the navmesh fit (film Z ≈ 200 ×
  world, X and Y ≈ 72: a ratio of 2.8) independently. If velocity were in world units with a
  power-of-two step, X and Y would be 64 film units to a world unit, which the navmesh fit (70.8–73)
  rules out.
- **Aim (component 21, checked in a controlled custom game, 1,572 records):** `[1] [yaw:12]
  [pitch:12]`. Four turns to the right show yaw falling by 1,071, 956, 1,065 and 1,082 (about
  94°, 84°, 94° and 95° at 4,096 to a turn), ending about 7° from the start, which is how
  accurately the player turned. Turning right lowers yaw: pitch stayed at exactly 2,032 in all
  358 aim records during the turns (any vertical stick drift didn't register) and yaw only fell
  (304 steps). Pitch is 2,048 at spawn and 2,032 after the player re-levelled by eye, so 2,048
  is probably level (it's the midpoint). **Higher pitch is up** (the player looked up only):
  looking up reached 3,020, 85° above 2,048; looking down at a ledge before stepping off it reached
  1,172, 77° below. While walking straight, the direction of
  movement in F1–F2 matches yaw, measured from +F1 towards +F2, within about 1° (60 samples,
  3 walks; the player steered round obstacles between walks, where the two don't match, as
  expected when strafing or turning). So yaw is an angle from the +F1 axis, and **F1 and F2 share one scale**.
- **The custom game's position is 3 bits longer**: its command tick sits 3 bits later. The extra
  bits are 2 that vary and a 0, so its split is probably about 20/17/17, not 20/17/14.

**Player positions** (checked on ten PvP films by `inspect`, which prints the respawn timing without checking it):

| Check | Result |
|---|---|
| A summary death ends the victim's biped-slot track within 3 ticks | 1,154 of 1,188 deaths (35 with deaths shifted by 2 s); tracks end on the death tick or the one before |
| A new biped slot starts 7.95–8.25 s after the death | 70–98% per film, 5–30× the rate in an equal-width window at 4 s; 118 of 137 at 8,000–8,099 ms in one film. One film spreads from 7.5 to 9 s (perhaps a different respawn setting) and fails this check only |
| Objects dropped at a death land on the victim's last position | horizontal median 16–32 units, against 326–627 for random pairs. Height is +68 to +92 units, so the biped position is probably the feet (guess) |
| Tick-to-tick steps of 6 units or fewer | 93–96% |
| Bipeds with positions per second ≈ humans present minus the recently dead | within 25% |
| A new slot's first position is at the dying player's team base | 93–100% |

**Field widths depend on the map or the game type (observed, one custom game).** In a one-player
custom game on Bazaar, a map none of the cached films use, the 51 bits split as about **F1 20, F2 17, F3 14**, not 18/15/18:
read as 18/15/18, F2 jumps by multiples of 2^13 and F3 by multiples of 2^14 in 80% of steps,
while F1's top 18 bits stay smooth. The 2-bit prefix was still `00`. The 12 cached matchmade
films (9 maps, CTF and Firefight) all need F1 = 18 exactly, so the widths vary, by map or by
custom versus matchmade (the component is `object-position-dynamic-precision`). Bazaar is a small
map, so "bigger map, more bits" doesn't fit; a matchmade film on Bazaar, or a custom game on a
map already cached, would tell the two apart. Where they come from
isn't known yet; the header is the place to look.

*Correction and a third layout (2026-11-18; `PositionLayout`, checked by `inspect` on every film):*
the widths are per map, and Bazaar's are **20/17/17**, not 20/17/14. A matchmade Slayer film on
another map (`2b6d2baf`, not in any other cached film) uses **21/18/16**. Two measurements find them,
and `PositionLayout.Detect` runs both on every film:

1. **The length.** In biped records holding only a position and the command tick, try every
   length: with the right one the tick lands after it and equals the packet count plus the film's
   offset (a 1-in-256 chance otherwise). The position is 54 bits on standard maps, 57 on Bazaar,
   58 on the Slayer map (17,363 of 17,367 records there; the next best length, 357).
2. **The split.** Between ticks a coordinate's low bits flip often and its high bits hardly ever,
   so a field ends where a bit flipping in 5% or more of steps is followed by one flipping in under
   1%. Z's lowest bit is always 0 on all three layouts, so the drop into it isn't a field end.

| Layout | Position bits | Seen on |
|---|---|---|
| 18/15/18 | 54 | 10 maps (all CTF, Firefight and the second custom game) |
| 20/17/17 | 57 | Bazaar (custom game) |
| 21/18/16 | 58 | one Slayer map |

On the Slayer map the X values sit near 65,536 instead of 16,384, and positions move **1.33–1.39
times** as far as velocity says (horizontal direction still within 0.8°).

**The scale differs per map and per axis too (2026-11-18, research script; checked against
velocity).** Velocity reads the same Spartan running speed on every map (90th percentile of
horizontal speed 208–244 units a second, Slayer map 217), so velocity is in fixed units and
positions are what change. Median position change ÷ velocity, for a slot's records a tick apart:

| Layout | Horizontal | Vertical |
|---|---|---|
| 18/15/18 (3 films) | 0.99–1.01 | 2.68–2.71 |
| 21/18/16 (Slayer map) | 1.39 | 1.61 |
| 20/17/17 (Bazaar) | 1.55 | 2.85 (short walk test; noisy) |

So on the Slayer map horizontal film units are about 1.35 times finer than standard (about 96–100
to a world unit), and vertical ones coarser. Both maps are 343-built and have no navmesh (the
console's `floor` command confirms it for the Slayer map), so that can't be checked against map
geometry. Bit count and precision both varying per axis fits a quantisation from each map's
bounds, which is where to look for the widths. Distances the timeline shows (kill distance,
distance moved) are in film units, so they compare within a match, not across maps.

**The layout follows the base level, which the header names (2026-11-18, research scripts; level
paths re-checked independently).** The film doesn't store the widths, any bounds or a
quantisation step: a search of the header and of chunk 1's leading packets (types 2, 6, 8, 12) for
the width triples in u8/u16/u32 and 5–8-bit fields, and for float bounds in both byte orders, found
nothing that tracks the layout. What it does hold, 3 bits into a byte (bit 6,763,827 in every film),
is a Bond CompactBinary struct whose first field is the base level's path, after the map's asset
and version GUIDs (bit 6,763,361, .NET byte order; they match the scoreboard). The level predicts
the layout in all 18 cached films:

| Level | Films | Layout |
|---|---|---|
| `levels\multi\fo03_space`, `fo08_wetland`, `fo09_academy`, `fo11_blank` (Forge canvases) | 16, including Firefight and both custom games on them | 18/15/18 |
| `levels\multi\ctf_bazaar` | 1 | 20/17/17 |
| `levels\multi\sgh_blueprint` (Recharge) | 1 | 21/18/16 |

So Forge maps share a layout because they share a few canvases, and 343-built maps each have
their own, taken from the level's data in the game files (not in the film). Detecting the layout
from the records (`PositionLayout.Detect`) stays the way to read an unseen map. Also: biped X never
uses the top 3 bits of its field (largest X needs 15, 17 and 17 bits on the three layouts), while Y
reaches its full width on two of them. X being 3 bits wider than Y in all three layouts suggests
those bits are something else (a flag, or extra precision); untested. Scale from velocity doesn't
fit "bits = enough for the map's extent at one fixed precision" either. With its layout, every positional `inspect` check passes on that film,
including dropped objects landing on the victim (91 units against 1,142), so dropped objects use
the bipeds' layout. The "lives start at one base" check runs only in CTF: in Slayer, 66 of 92
lives started at the nearer of two clusters, as expected with no team bases.

*Second custom game (a version of a map the cached films use, but a different map asset ID):* exactly **18/15/18**, with F3's
lowest bit always 0, as in matchmade films; the command tick lands after a 54-bit position in
2,659 of 2,713 records. So custom games don't use other widths as such: Bazaar, or that game's
settings, does. Smoothness pins F1 (20, possibly 21) but not the
F2/F3 boundary within a bit or two, because the spare high bits barely change over a short walk.
Read as 20/17/14, the recording shows (`track` exports it):

| Time | What the track does |
|---|---|
| 16.8–20.8 s | walks along −F1 at about 295 units/s; F3 drops 65 units at 18.9–19.6 s: the player dropped from a ledge |
| 22.7–24.2 s | walks back along +F1 |
| 26.1–28.7 s | walks along +F2 at about 244 units/s |
| 30.0–32.7 s | walks back along −F2 |
| 33.4–39.8 s | walks along −F1 at about 275 units/s while jumping 4 times: F3 rises 24–26 units over takeoff and lands about 1.1–1.2 s later, on ground falling about 23 units |
| 39.8–41.0 s | reverses along +F1 |
| 41–51.4 s | stands still (only a position record every 0.3–0.5 s, unchanged) and fires the Assault Rifle in 8 bursts of 3, 44.6–50.3 s |
| 51.5–55.3 s | swings ±100 units along F1 |
| 55.3–57.4 s | walks along +F1 at about 245 units/s |
| 62–65 s | walks along −F1, then curves round and stops |

So F3 is vertical (jump arcs) and F1/F2 horizontal. If the walks along F1 and F2 were at the same
speed, F1 and F2 units differ by about 1.2×, as they would if each axis is quantised over its own
extent. The player confirmed Bazaar, outdoors throughout, and the ledge; they don't remember the
moves in order, so the walking directions can't be checked against their account.

**Ammo (component 30, `weapon-state-ammo`, observed).** Firing shows as 24 biped records carrying
component 30, one per round. The 8 bits ending 4 bits before a constant 26-bit tail
(`10000000011010111110111110`) count down by one per record, 35 to 12: a full 36-round magazine
after its first shot. Components 21 (aim) and 25 (command tick) come before it in most of these
records (34 bits together: aim 25, command tick 9, see "Biped component widths").

*In matchmade films (12 films, 72 players whose lives all chain):* skipping the components
before it with the widths above, ammo data starting `00` holds `[00] [count:8]`. The count goes
down by one per round (e.g. 29, 28 … 9, four ticks apart). Data starting `01` holds a value that
rises slowly by about 0x333 at a time, perhaps an energy weapon's charge (guess). Summing each
player's drops gives a median 0.74× the scoreboard's shots fired. That includes shots hidden by
reloads and before a life's first update, with each magazine taken as the largest count seen.
Only 18 of 72 players are within 20%, so the count is real but not yet a reliable total. The
gaps: magazines are guessed, energy weapons are skipped, components 30/33/36/39 seem to be
inventory slots whose weapon can change, and it isn't known how the scoreboard counts burst or
multi-pellet weapons. The value 36 (Assault Rifle and Battle Rifle magazine) never shows as a refill,
so updates are sparse after reloads.

*Controlled check (second custom game, checked against the scoreboard):* every round fired has
its own record, and the drops add up to the scoreboard's shots fired exactly. The Assault Rifle
(component 30) counts 35 down to 1, then one record starting `01` at empty, then 36 after the
reload: 36 rounds. The Sidekick (component 33) counts 11 down to 7, then 12 after its reload:
5 rounds. 36 + 5 = 41, the scoreboard's shots fired. The player fired 10 taps, then held the
trigger, and the taps fired 15 rounds (four taps fired two, 83 ms apart, the Assault Rifle's
time between rounds). So **shots fired counts rounds, not trigger pulls**. In records with only
the ammo component the count comes after one 0 bit, not two (`[0] [count:8]`). Component 42
appears at each weapon switch (not decoded).

*Matchmade films, after decoding the tick (2026-11-17, research; `rounds` in the console):* the
guess that unread records explained the shortfall was wrong. Reading stops before the ammo
component in only about 170 of about 5,950 ammo records in two films. Counting checked one-round
drops gives a median 0.45× the scoreboard's shots fired (7 complete players, one film). The
causes found so far:

- **Weapon slots** (header registry, block 35): each of four inventory slots has three
  components: `weapon-state-ammo` (30, 33, 36, 39), `weapon-state-rounds-inventory` (31, 34,
  37, 40; reserve rounds, probably) and `weapon-state-overheated` (32, 35, 38, 41). Then 42
  `biped-desired-weapon-set` and 43–46 `weapon-state-type-info`, one per slot, which may say
  which weapon is in it. `inspect --registry` lists every block's names.
- **Ammo starting `1` is most ammo records** (1,868 of 2,831 checked in one film). Starting
  `10`, the next 15 bits are a value from 0 to 32,767 that only goes up. It jumps by a fixed
  step per shot, with no records between shots: steps of 913 (about 32,768 ÷ 36) and 3,280
  (about 32,768 ÷ 10) seen, 20–38 ticks apart. So it's probably the share of a battery or
  magazine used, for weapons that don't keep a round count (guess: energy weapons). Other runs
  rise by 8 or 24 every tick for hundreds of ticks, perhaps continuous fire or a charge. Starting
  `11` is rarer; the controlled game's "empty" record above is one, and its layout isn't known.
  *Correction (2026-11-18):* the value is **12 bits**, not 15 (`[10] [value:12]`, 14 bits in all),
  and `[11]` is 2 bits on its own, found by where the item ID lands after it (see "Which weapon").
  The steps above, read 3 bits too long, are 8 times too big: 410 (about 4,096 ÷ 10) and 114
  (about 4,096 ÷ 36). The player with the most such records had 0.18× their shots counted, and the best,
  with very few, 0.81×.
- **It isn't the whole story:** two players with few such records still had only 0.23× and 0.34×,
  with far fewer round-count records than shots. Burst weapons (one record per burst?) and
  records with only an ammo component (no tick to check) are the next suspects.

*Across every PvP film (research scripts, 11 films, 72 players whose lives all chain):*
- A least-squares fit of shots fired on per-player record counts does best as about
  1.1 × one-round drops + 176 × battery used (in whole batteries) + 0.4 × ammo-only count records.
  The median error is 18%, and 25 of 72 players are within 10%. That's better than one-round drops
  alone (median 0.45×), but not reliable: the weights don't match any single rule.
- Players with almost no battery records still range from 0.21× to 0.88×. Drops of more than one
  round are rare, so it isn't records missing within a magazine.
- **The `[0] [count:8]` layout is probably right only for some weapons.** Counts after a rise
  (which should be magazine sizes) are mostly 2, 14, 30, 4 and 5, and some slots start at 79. In
  the controlled game the same layout gave exactly 36 and 12. So the ammo component's layout
  likely depends on the weapon, and the next step is knowing which weapon a slot holds:
  `weapon-state-type-info` (43–46), or a controlled game with one weapon at a time.

**Weapon pickups (checked on the PvP films by `inspect`; `Format/Inventory.cs`).** A picked-up
weapon doesn't vanish. Dropped objects stop sending records once they come to rest (652 of 732 in
one film end on a record that repeats the one before), and nearly all their slots are freed later,
at a median age of 23–28 s, which looks like the despawn time. The slot table only comes every
20 s and this pool never reuses slots, so it can't tell a pickup from a despawn. Objects that go
quiet and then move again don't have players near them more than chance (62 against 64 within 80
units); they're nudged by explosions. What does show is **the picker's inventory**: a checked biped
record lists `weapon-state-type-info` for the slot that changed (43–46, after the command tick, so
the record can be checked).
- **43 and 44 are the two weapon slots.** 89 of 148 changes in five films have a new dropped object
  appear within 80 units and 1 s of the player, away from any death: the weapon given up (about 5%
  four seconds earlier). 57 have an object that was dropped in the last 25 s within 20 units: the
  weapon picked up. The rest are probably weapons from the map's own spawns, which aren't dropped
  objects. Per film, 43–67% of weapon-slot changes show a swap drop, against 0–7% four seconds
  earlier. Since picking up a weapon always gives one up, some swapped-out weapons are missed (or
  some changes aren't pickups; untested). They usually come with that slot's ammo (30/31 or 33/34)
  and 42 (`biped-desired-weapon-set`).
- **45 is the CTF flag.** 76 of 94 changes in five CTF films have an objective event within 1 s
  (9 four seconds earlier), and almost none drop anything. So a flag-slot change at an objective
  event is a flag pickup; the steals, returns and captures still need telling apart.
- `MatchTimeline.Pickups` lists the weapon pickups during linked lives, with the weapon's ID.

**Which weapon (2026-11-18; `InventoryItem`, `Inventory.KnownWeapons`, checked by `inspect`).**
With the widths of the components between the command tick and the type-info (ammo 9, 14 or 2
bits; reserve 11; desired weapon set 7, then type-info's presence bit), **type-info holds a 32-bit item ID** that's the same
in every film, probably a tag ID. Of 400 records listing a weapon slot's type-info in 16 films,
297 have a known ID exactly where those widths put it. The main exception is 29 records shaped
`[11]` ammo, reserve, 42, all starting with the same odd bits: all in the Firefight film, one per
biped, 5–42 ticks after its last position (at death), with 42 reading `1101101`; their layout isn't
settled (research). `inspect`
reads an ID for 85–100% of each film's weapon-slot changes, and 85–100% of those are IDs seen in
another film. A misread would be a random 32-bit value.

What follows the ID isn't decoded, so reading stops there. Another 32-bit value comes next. It's
shared by weapons of one kind: `2164b3cf` or `a164b3cf` for weapons with a round count, and
`42c9679f` for most weapons whose ammo is `[10]`. Then come a 12-bit value that varies, a few
constant bytes, and 8 bits that depend on the ID. The two most-picked IDs each come with one of two
second values. The length isn't known.

Which weapon each ID is hasn't been worked out: the names aren't in the film. What the ammo shows at
pickup, from the same record (picks / films; counts are rounds at pickup, the largest is a lower
bound for the magazine):

| ID | Picks | Ammo at pickup |
|---|---|---|
| `841ac5e5` | 118 / 12 | battery, 410 a shot (about 10 shots) |
| `4ff3937e` | 32 / 10 | battery, 594 a shot (about 7) |
| `850cc95e` | 18 / 9 | 3–10 rounds |
| `b8d58516` | 15 / 8 | 1–2 rounds |
| `9564e17f` | 13 / 5 | 4–8 rounds |
| `da99cabf` | 13 / 9 | 5–30 rounds |
| `daf193c7` | 11 / 5 | battery, rising 63 at a time (continuous fire?) |
| `c30d87c7` | 10 / 8 | battery, 114 a shot (about 36) |
| `fa040c87` | 10 / 5 | 2–14 rounds |
| `d04aaf4f` | 10 / 7 | 42–80 rounds |
| `958c126a` | 8 / 6 | 3–36 rounds |
| `c9c3d45c`, `db0cec25`, `918223d8`, `fecc2aa6`, `86906234`, `c04bbdd2`, `a460ce96`, `c25e94f6`, `bb3edcb6` | 2–5 each | at most 15, 12, 6, 40, 1 (always), 8, 25, 12, 6 rounds |
| `30484ea6`, `c3542946` | 5, 2 | battery, 33 and 36 a step |
| `71ab0a2c`, `230447b1`, `a0955e9e`, `0a1992bc` | 2–5 each | no ammo field; reserve 0–2, 4–6, 80, 10 |

Magazine sizes suggest some names, but these are guesses, not checked: always 1 round is a Skewer,
2 a rocket launcher, 14 a Bandit, 12 a Sidekick, 36 an Assault Rifle or Battle Rifle. A controlled
custom game picking up known weapons one at a time would settle them.

**The flag slot and flag actions (2026-11-18; research scripts on 13 CTF films, re-checked
independently; `InventoryItem`, `FlagEvents`, checked by `inspect` against the scoreboard).**
*Correction:* component 42 is **7 bits**, not 8. Its "8th bit" is type-info's first bit, a
**presence bit**: in records ending in 42 the next record starts 7 bits after it (1,346 cases, every
film), and where it was 1 a known ID followed (451 of 454), where 0 none did (105 of 105). The IDs
read before are unchanged (they sit at the same place); the `0x1188…` "other layout" values in the
flag slot were empty slots read as IDs. Type-info:

```
[1] [item ID:32] [variant:32] [more state] [1] [stamp:8] [set:7]    something in the slot
[0] [stamped:1] ([stamp:8] if stamped) [set:7]                      empty
```

*set* repeats 42's value; *stamp* is the command tick plus a per-film constant. Battery weapons end
with that tail; round-count weapons don't, and their layout after the variant isn't decoded. In the
flag slot (45):

- **Flag picked up** (165 bits): item `2a392328`, variant **which team's flag**: `6565cd0e` when a
  team-0 player carries it, `0299afaf` for team 1 (122 of 123; the other is probably a slot linked
  to the wrong player). Nothing in it tells a steal from a grab.
- **Emptied, stamped** (17 bits): the flag left a living carrier. 42 of 54 come with the carrier's
  own code-5 event at the same moment, against 47 scoreboard captures: **captures**; the rest are
  **drops**. A carrier's death sends no flag-slot record.
- **Empty, unstamped** (9 bits): the slot at the start of play, not a change.
- Component 42's middle 3 bits name the inventory slot wanted: 000 for 43, 001 for 44, 010 for the
  flag (131 of 131), 100 a second form at weapon changes (research; not used).

`FlagEvents` follows each team's flag (home, carried, dropped) through these, deaths, kills and the
code-5 objective events: a pickup is a steal from home or a grab of a dropped flag; an emptying with
the carrier's code-5 is a capture, without one a drop; code-5 at the player's own kill of the
carrier is a carrier kill, by a capturer's teammate at a capture an assist, and by a player whose
team's flag is dropped a return; a dropped flag goes home after 15 s untouched (inferred: counts
fit best between 13 and 18 s). Records can be missing (the winning capture ends play before its
record; some pickups have none), so a carrier's code-5 with no record counts as a capture and an
unexplained one while nobody carries the flag as a pickup. Across 13 CTF films, against the
scoreboard:

| Action | Found | Scoreboard | Players exact |
|---|---|---|---|
| Captures | 48 | 47 | 109 of 110 |
| Capture assists | 21 | 21 | 110 of 110 |
| Returns | 39 | 39 | 104 of 110 |
| Steals | 104 | 100 | 100 of 110 |
| Grabs | 41 | 43 | 106 of 110 |
| Carrier kills | 78 | 82 | 106 of 110 |

Plus 11 drops, which the scoreboard doesn't count. Not settled: the flag's state fields after the
variant, round-count weapons' tails, and 42's other bits.

**Killers aim at their victims (checked, 11 PvP films, 1,201 kills with a killer aim):** at each kill whose killer's
life is linked (`MatchTimeline`), the killer's last checked aim in the second before points along
the line from killer to victim with a median error of 2–6° per film, and 85–97% of kills are within
20°. That checks aim in matchmade films (not only the custom game), the yaw convention, that the
F1–F2 plane isn't mirrored (a mirror would put the median near the reflection, not 2°), and the
killer placement. Victims were facing within 45° of their killer in 64–94% of kills per film.

**The scale comes from the navmesh, not the film.** F1 and F2 share one scale, and yaw is measured
from +F1 (see aim, above). In the second custom game, on an 18/15/18 map, walking straight ran at
about 372 units/s, a faster run (probably a sprint) at about 414, and crouch-walking at
about 194. F3 is vertical, on its own scale about 2.8 times F1 and F2's (velocity, above). The
navmesh fit gives about 72 film units to a world unit horizontally ([Map geometry](#map-geometry-the-map-assets-navmesh-observed));
velocity can't, because it's stored in film units too.

**Which player is which** (`Format/Lives.cs`, checked by `inspect`): a life that ends in a death is
the slot that death ends; the life after a player's last death is the new slot nearest 8 s after
it. No player ever gets more lives than their scoreboard spawns, and 64 of 84 PvP players get
exactly as many; the rest are short, mostly a life with no death at either end (a player who never
died, a quitter). Pairing every death with the new slot nearest 8 s later agrees with the slot the
player's next death ends in 85–100% of cases per film, so deaths are trusted first. Not yet used:
the end-of-packet per-player list (above), which could identify the remaining lives.

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

## Map geometry: the map asset's navmesh (observed)

Films hold no geometry, but Forge maps carry it. The map asset
(`GET discovery-infiniteugc.svc.halowaypoint.com/hi/maps/{asset}/versions/{version}`, token needed)
lists its files under a `blobs-infiniteugc` prefix (token needed too). Forge maps (11 of the 12 maps
in the cached PvP films) have `map.mvar` (0.3–1.6 MB, the Forge objects; `CustomData.NumOfObjectsOnMap`
2,331–5,636), a small canvas `.mvar`, `navmesh.blob`, `lightprobes.blob` and `audioocclusion.blob`.
Bazaar, a 343-built map (`IsBaked: true`, 0 objects), has only its `.mvar` and images.

**`navmesh.blob`:** a 12-byte big-endian header (2, the length that follows, 0x001FFFFF), 12 more
bytes, then raw deflate. Inside: big-endian u32s (2, 1 section, then sizes) and four Havok 2022.1
tagfiles (`TAG0`, `SDKV 20220100`, `DATA`, `TYPE`, `INDX`/`ITEM`): `hkaiNavMesh`,
`hkaiClusterGraph`, `hkaiTraversalAnnotationLibrary` (jumps and drops, probably) and
`hkcdStaticAabbTree`, then about 190 KB not yet looked at. In the `hkaiNavMesh`, `ITEM` entries
2–4 give the offsets in `DATA` and counts of faces (12 bytes: start edge i32, start user edge i32,
edge count i16, user edge count i16), edges (20 bytes: vertex a i32, vertex b i32, opposite edge,
opposite face, flags, padding, cost) and vertices (16-byte float4, little-endian). Each face's
edges chain into a closed polygon (2,075 of 2,075 in one map). Z is up. Meshes have 66–7,761 faces.

**Film coordinates against the navmesh (observed, 13 films on 10 maps):** with film X and Y mapped
to world as (value − 16,384) ÷ k, the share of sampled biped positions over a navmesh face peaks at
83–98% per film, at k = 68.5–81.5 (most 70–75). So X and Y share one scale, there's no axis swap
or mirror, and one world unit is about 72 film units. Fitting k and both origins together
(`FloorPlan.Fit`, checked on 11 PvP films) puts 94–99.8% of positions on the mesh at k = 70.8–73.0;
the origins are near (16,360, 16,410) on maps near the world origin and move by up to about 100
film units on maps far from it (centred 90–140 world units out), so the quantisation range
probably depends on the map (guess), as Bazaar's different field widths also suggest. Z fits roughly
200 × world Z + 210,000 on most maps (noisier: the biped position may not be the feet). A finer fit
(k and offsets per axis at once) is still to do. At k = 72 the walking speeds above are about
5.2 world units a second.

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

- **CTF** (checked, 92 players in twelve films): each player's code-5 count equals their
  `FlagSteals + FlagCaptures + FlagCaptureAssists + FlagReturns + FlagCarriersKilled`, plus
  between 0 and their `FlagGrabs`. Some flag grabs log one too: 18 of 30 grabs in all, and 82 of
  92 players have exactly as many extra events as grabs. Which grabs log one isn't known.
  *Correction:* this was "or one more, for a player with grabs and secures". One film has a
  player with 3 grabs and 2 extra events, which failed that rule.
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
4. Replication stream: the per-player list is in the code (`Format/PlayerList.cs`) and drives the
   animation's alive and dead states. Next: find the entry variant still behind the 1–7%
   disagreement with biped tracks, and use the list to link the lives chaining misses (in one film
   the gap is linking, not records). Velocity (including the 2-bit standing-still form), aim, the
   command tick and the weapon's ID are decoded; next, name the weapon IDs (a controlled game) and
   the flag slot's other layout. Two controlled recordings exist (see
   "Field widths depend on the map"); `PositionLayout.Detect` now reads each film's widths from its
   records. Next, find where they come from (the header?) and the Slayer map's world scale (its
   navmesh), and match the track to the player's account.

## Prior art
Others who have worked on this format:

- [den.dev: Extracting match stats from Halo Infinite film files](https://den.dev/blog/extracting-stats-film-files-halo-infinite/) (Den Delimarsky)
- [SPNKr](https://github.com/acurtis166/SPNKr) (Andy Curtis)
- [halo_api](https://github.com/nuzzles/halo_api), and [halo-theater](https://github.com/games-directory/halo-theater), built on it
- [libfilmshell](https://github.com/OpenSpartan/libfilmshell) (OpenSpartan)
- [LevelUp](https://github.com/JGtm/LevelUp) (a fork of Guillaume Sitbon's LevelUp)
- [Spencer Imbleau: Reverse-engineering Halo: Infinite's Theater films](https://spencer.imbleau.com/blog/ai/rust/2026/09/14/halo-infinite-theater-format-astra.html)
