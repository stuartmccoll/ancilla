# Ancilla.Theater

Decodes Halo Infinite Theater films (the recordings behind Theater Mode) into match analysis: who
killed whom and when, medals, lives and score flow. It's a library: the web app's match timeline
page uses `MatchTimeline`, and the research console in `src/Ancilla.Theater.Cli` uses the rest.

- **[FORMAT.md](FORMAT.md)** is the source of truth for what's been decoded, with the evidence and a
  confidence level for each finding. Read it first.
- This README covers how to run it, how the code is organised, the techniques that have worked,
  and where to pick up.

## Status (November 2026)

| Part | State |
|---|---|
| Download and cache (manifest, chunks, scoreboard) | Done |
| Packet layer (16-byte headers, 64-bit µs timestamps, 60 Hz ticks) | Decoded and checked |
| Header: version, component registry, build identity | Decoded and checked |
| Header: roster (XUIDs and gamertags, in scoreboard order) | Decoded and checked |
| Summary: deaths, player kills with victims, medals, event times | Decoded and checked against the scoreboard |
| Summary: medal code table | 47 codes confirmed (8-bit field); more come from more films |
| Summary: bots and late joiners | Late joiners decoded and linked to the scoreboard; bot deaths aren't recorded, and the checks account for them |
| Summary: full record layout (count, per-player body, name, event bits, 12000) | Decoded and checked; no positions or weapons in it |
| Summary: event code 5 | Objective events (CTF flag actions, Firefight objective awards); checked against CTF stats |
| Replication chunk leading packets: type 1 entity slot table, types 6, 8, 12 | Type 1 decoded and checked; 8 and 12 partly |
| Type 10 tick markers, and packet time = summary event time | Decoded and checked |
| Replication stream: type 0 entity records and player positions | Positions decoded and checked against deaths; lives chained per player (most, not all); scale, aim and weapons not yet |

**Verified on eleven version-41 films:** one Firefight (4 players) and ten PvP Capture the Flag
(8 players, mode category 9, with quitters, late joiners and bots). `inspect` passes every check on all eleven. Run
`list` to see what's cached.

## Running it

`dotnet test` from the repo root runs the unit tests in `tests/Ancilla.Theater.Tests`. They build
films from scratch (`Synthetic.cs`), so they need no token and no cached films.

```bash
export HALO_SPARTAN_TOKEN='v4=...'      # raw or URL-encoded (v4%3D...) both work
dotnet run --project src/Ancilla.Theater.Cli -- fetch                 # your latest matchmade match
dotnet run --project src/Ancilla.Theater.Cli -- fetch --pvp           # latest one that isn't Firefight
dotnet run --project src/Ancilla.Theater.Cli -- fetch --pvp --count=5 # latest 5 of your last 25, one per mode first
dotnet run --project src/Ancilla.Theater.Cli -- fetch --pvp --count=5 --start=25  # the same, from matches 26–50
dotnet run --project src/Ancilla.Theater.Cli -- fetch --custom        # your latest custom game
dotnet run --project src/Ancilla.Theater.Cli -- fetch <matchId>
dotnet run --project src/Ancilla.Theater.Cli -- list
dotnet run --project src/Ancilla.Theater.Cli -- inspect               # newest cached film
dotnet run --project src/Ancilla.Theater.Cli -- inspect <matchId> --learn --roster
dotnet run --project src/Ancilla.Theater.Cli -- timeline <matchId>   # kill feed, score flow, head-to-head, lives
dotnet run --project src/Ancilla.Theater.Cli -- track <matchId> > track.csv   # raw biped positions, for controlled tests
dotnet run --project src/Ancilla.Theater.Cli -- dump <matchId> <chunkIndex>
```

| Option | Effect |
|---|---|
| `--show-names` | Prints gamertags instead of name lengths (`inspect`) or `T0 P1` labels (`timeline`). Keep it off when output might be shared. |
| `--no-feed` | `timeline` only: leaves out the kill feed. |
| `--learn` | For medal codes not yet in `MedalCodes.cs`, lists which scoreboard medals fit, with names and difficulty from the cached medal metadata. Add a code when exactly one fits, or when every other fit already has a code (say so in the comment). The candidate's difficulty must match the event code (25/50/75, or 100+ for mythic). |
| `--roster` | Lists every XUID-range value that has a name, with its XUID-to-name distance and whether it's on the scoreboard. Use it when roster checks fail. |

**Exit codes:** `inspect` returns 0 when every check passes and 2 when any fails, so it can be run
across all cached films to find where a finding stops holding:

```bash
for m in $(dotnet run --project src/Ancilla.Theater.Cli --no-build -- list | cut -d' ' -f1); do
  dotnet run --project src/Ancilla.Theater.Cli --no-build -- inspect "$m" | grep -E "FAIL|failed|passed"
done
```

### The timeline command (prototype)

`timeline` turns the decoded summary into the analysis a match-timeline page would show. Players
are `T{team} P{roster index}`; the index is scoreboard order among humans.

- **Kill feed:** each kill with its victim and the medals it earned, deaths with no killer, and
  medals that aren't tied to a kill (Odin's Raven, Steaktacular, Wingman, Harpoon). Kill medals are
  recorded from 2 ms before their kill to about 280 ms after it (Bulltrue), so the window is
  −5 to +500 ms.
- **Score flow:** team kills per minute, lead changes, and the largest lead for each team. Film
  kills plus bots' kills are checked against the scoreboard's team kills. All
  PvP films so far are **CTF** (mode category 9), so team score is flag captures, which the
  summary doesn't time.
- **Head-to-head:** kills by each player on each opponent (late joiners are marked `*`), kills of bots, and each
  player's nemesis (the opponent who killed them most).
- **Players:** kills, deaths, deaths with no killer, longest streak, first kill and first death,
  traded kills (died within 3 s after a kill) and revenge kills (killed their last killer). The
  longest streak equals the scoreboard's `maxKillingSpree` for all 40 roster players in the five PvP
  films.
- **Positions** (PvP only): per player, the lives found against scoreboard spawns, the distance
  moved, and the median kill distance (killer's position against the victim's last position).
  Then a text **death map**: where players went (`·`) and where each team died. Distances are in
  film units, whose scale and axes are unknown, so compare them only within a film, or maps
  across films on the same map. Lives come from `Lives` (see FORMAT.md, "Which player is which").
- **Lives:** death-to-death times, and **estimated** time alive (death-to-death minus the respawn
  estimate), next to the scoreboard's average life.

**Calibration (estimates):**
- **Respawn ≈ 8 s in CTF.** For full-match players, the time the scoreboard doesn't count as alive
  (`timePlayed − averageLifeDuration × spawns`) fits 8.0 s × deaths + 38 s across 35 players in five
  films. Each film's own fit is 7.8–8.2 s, and `timeline` prints it.
- **The event clock is match time.** The last event is 1.8–2.0 s before the scoreboard's match
  duration in every PvP film, and the winning team's Steaktacular medals sit on that last event.
  Nobody has an event before 35 s, and the fit's 38 s is time before the first spawn. The
  replication stream confirms both: event times equal packet time from the film's first packet
  to within a few ms, and play starts 27–40 s in, after an intro in which bipeds already exist.
  Each film is 11.6–11.8 s longer than its match.
- **Firefight is different:** revives and lives make the fit meaningless there (14 s × deaths).

**Speed:** `inspect` takes about 1 second for a 13-minute film. `fetch` downloads 9–30 MB, 6 chunks
at a time, and skips chunks already cached.

### In a sandboxed or Claude Code shell
- **Network:** calls to the Halo services need network access. In a sandboxed shell they fail with
  a connection error until sandboxing is lifted for that command.
- **Python on this Mac:** it rejects the HTTPS certificate chain ("self-signed certificate in
  chain"), probably because something inspects HTTPS traffic. Don't switch verification off. Export
  the keychain roots once and point Python at them:
  ```bash
  security find-certificate -a -p /Library/Keychains/System.keychain \
    /System/Library/Keychains/SystemRootCertificates.keychain > /tmp/keychain-roots.pem
  SSL_CERT_FILE=/tmp/keychain-roots.pem python3 -I script.py
  ```
  .NET uses the keychain directly, so Ancilla.Theater itself is unaffected.

## Code layout

| File | Role |
|---|---|
| `../Ancilla.Theater.Cli/Program.cs` | Commands. `Inspect` is a list of `Check(bool, description)` calls grouped by chunk type; add a check for every new finding. |
| `MatchTimeline.cs` | The web app's match timeline: kill feed, per-player figures, team kills per minute, head-to-head and death positions, as data. |
| `Timeline.cs` | The `timeline` command: a prototype of the match-timeline feature, built on the decoded summary. `AssignMedals` links each medal to the same player's nearest kill. `WritePositions` adds distances and the death map. |
| `FilmPositions.cs` | Loads a cached film's biped and dropped-object positions, with each tick's time on the event clock. |
| `FilmSource.cs` | Manifest parsing and chunk download (public, so no token; host and path are validated). The manifest itself comes from `HaloWaypointClient.GetFilmManifestJsonAsync`. |
| `FilmCache.cs` | Cache folder for each match: `manifest.json`, `scoreboard.json`, `chunkNNN-typeT.zlib`. `LoadChunk` decompresses. `medals.json` (medal metadata, saved once by `fetch`) sits in the cache root. |
| `Format/Packets.cs` | `PacketReader.Read`: the 16-byte packet layer of type-2 and type-3 chunks. |
| `Format/FilmHeader.cs` | Header chunk: version, registry blocks, value table, build strings, start of the bit stream. |
| `Format/Roster.cs` | Finds players in the header bit stream: XUID scan plus a two-copy name check, with ownership chosen by record layout. |
| `Format/SummaryRecords.cs` | Walks the summary record by record from its stored count, with no roster needed. |
| `Format/Summary.cs` | Turns walked records into events for a list of players, and pairs kills with victims. Keeps the old name-search decoder for `inspect`'s cross-check. |
| `Format/FilmPlayers.cs` | Whose records are whose: roster players by XUID, then late joiners linked to the scoreboard by kills, deaths and medals. |
| `Format/MedalCodes.cs` | Learned medal code → medal NameId table. |
| `Format/SlotTable.cs` | The type-1 packet: the entity slot table (in use, generation, value per slot; pool cursors). |
| `Format/TickMarker.cs` | The type-10 packet: one 6-bit value per present player slot. |
| `Format/EntityRecords.cs` | Type-0 entity records: biped and dropped-object positions, found by their headers. |
| `Format/Lives.cs` | Biped tracks per slot, matched to deaths and chained into each player's lives. |
| `Format/BitReader.cs` | MSB-first bit reader. |
| `Format/ShiftedBits.cs` | The data shifted by 0–7 bits, so a byte pattern at any bit offset can be found with `IndexOf`. Much faster than reading bit by bit. |

## Techniques that worked

These are how each finding was made. Reuse them for the next ones.

1. **Search at every bit offset.** Data is bit-packed MSB-first, so values rarely sit on byte
   boundaries. To find a known value (a XUID, or a gamertag as a 32-byte UTF-16LE field), shift the
   whole buffer by 0–7 bits and byte-search each copy (`ShiftedBits.FindAll`). Searching bit by bit
   in Python with big integers is far too slow; don't.
2. **Anchor on something known, then read fixed offsets.** Summary fields were found relative to
   the player's gamertag field. When records vary in length, an anchor inside the record is more
   reliable than the record's start.
3. **Compare snapshots of the same player.** For one player's records in order, list the bit
   positions that change. Fields that never change are identity or layout; fields that change often
   are counters, codes or times. That's how the event code (+376) and time were found (first
   read at +396; the whole field starts at +384).
4. **Test a field against the scoreboard for every player at once.** For a candidate field and
   value, count matching records per player and compare the result with a scoreboard stat across all
   players. An exact match for 8 players can't be chance. This is how deaths (code 10), kills
   (code 25 with the medal flag clear) and the medal flag were confirmed.
5. **Match count vectors to identify codes.** Per-player counts of a code against per-player counts
   of each scoreboard medal. A unique match identifies the code; that's how `--learn` works. Bigger
   lobbies give more distinctive vectors, so PvP films resolve codes better than 4-player Firefight.
6. **Pair events by time.** A kill and its victim's death are consecutive records 0–1 ms apart.
   Deaths left over are suicides.
7. **Get exact answers from the APIs.** The saved scoreboard has per-player kills, deaths, medals
   and suicides, plus `PvpStats.Kills`. Gamertags for XUIDs come from
   `GET profile.svc.halowaypoint.com/users?xuids=a,b,c`, and medal names and NameIds from
   `gamecms-hacs.../hi/Waypoint/file/medals/metadata.json` (both need the token).

8. **Check that a field is wide enough.** A 7-bit medal code passed every check until codes over
   127 turned up. When a field's values crowd its top end, read the neighbouring bits for the same
   records and see whether they vary.
9. **Account for every mismatch with the scoreboard.** Unpaired kills and killerless deaths looked
   like decoding errors. They were exactly the bots' and late joiners' deaths and kills, and that
   became a stronger check.

**Mistakes to avoid repeating:**
- Reading a field once and assuming it belongs to the nearest record. The name 469 bits before a
  XUID turned out to be the *previous* player's.
- Loose text checks. Short strings and zero-padded fields recur by chance in zero-heavy data. Keep
  the gamertag character set, a minimum of 3 characters, and the two-copy rule.
- Assuming the medal code is an index into the metadata. It isn't; it's the game's own numbering.
- Ranges and fields that are too tight. The XUID range missed a real player, and the medal code
  was a bit short. Both passed every check until a film showed otherwise.
- Calling a field constant after two films. The "always 0 or 3" packet field was the high word of
  the timestamp.

## Where to pick up

In order of value:

1. **Fetch more films and run `inspect --learn` across them.** Prefer different modes (Slayer,
   Oddball, Strongholds) and ranked: the last 25 PvP matches were all CTF (mode category 9). Each new film either confirms the layouts or shows where they
   break, and fills in the medal table.
2. ~~Decode more of the kill record.~~ Done: apart from the four known fields, every bit in a
   summary record is constant for its player, so there are no positions, weapons or damage there.
   Those have to come from the replication stream (step 4).
3. ~~Work out event code 5.~~ Done: it's an objective event (FORMAT.md). Next: tell CTF flag
   actions apart (steal, capture, return, carrier kill) for the kill feed, and check code 5 in
   other modes.
4. **Replication stream.** Type 1 (entity slots), type 10 and the match-time mapping are decoded,
   and `inspect` checks them. Player positions are found: type 0 records are addressed by type 1
   slot and carry a component list (see FORMAT.md), and `inspect` checks them; `Lives` chains them
   into each player's lives, and `timeline` shows distances and a death map. **To do:**
   - [x] **Per-player list decoded** (FORMAT.md, "Per-player list"): who is alive at every tick.
     It showed the lives chaining isn't the gap: chained tracks already cover 91–99.6% of alive
     time in most films. Not in the code yet; the animation can use it for alive and dead states.
   - [ ] **Find the missing biped tracks.** Lives the chaining misses mostly have no decoded biped
     records (two films cover only 65% and 77% of alive time). Look at what `EntityRecords.Find`
     skips during those lives.
   - [x] **Aim (component 21), the command tick (25) and ammo counts (30/33/36/39)** are read by
     `EntityRecords.FindBipedUpdates`, and `inspect` checks them (FORMAT.md, "Biped component
     widths").
   - [ ] **Decode velocity (component 1).** It's 31 bits after a position and another width
     without one. Velocity against the change in position may give the coordinate scale.
   - [ ] **Count rounds fired** from the ammo counts and compare them with the scoreboard's shots
     fired. It matched exactly in a controlled custom game; matchmade films need the ammo
     component on its own (no command tick to check it) and the `[1]` kind handled.
   - [x] Controlled recordings fixed the axes: Z is vertical, X and Y share one scale, and yaw is
     measured from +X. The scale is still unknown.
5. **The web app's match timeline.** Started: the decoder is a library, the console is
   `Ancilla.Theater.Cli`, and `/matches/{id}/timeline` shows `MatchTimeline`. To do:
   - [ ] Make the console's `timeline` command render `MatchTimeline` too, so the analysis lives
     in one place (it still has its own copy of the streak, traded and revenge logic).
   - [ ] Label CTF objective events (steal, capture, return, carrier kill) once a flag-state model
     can tell them apart.
   - [x] Map layers: deaths with facing, kills from killer to victim, time-spent heatmap, objective
     events.
   - [x] Floor plan from the Forge map's navmesh (`NavMesh` in Core, `FloorPlan` here), fitted per
     film; 343-built maps (Bazaar) have none.
   - [ ] Animated movement on the map; it needs the lives chaining misses filled first.
   - [ ] Weapon pickups: dropped objects that vanish next to a biped (untested), then which weapon
     (`weapon-state-type-info`, 43–46).

**Open questions from FORMAT.md:** the extra XUID-range values in each header stream; whether the
123-value table after the registry describes type 1's 123 static slots; the layouts of packet types
2, 8 and 12; the event clock's offset from film time; and packet header field 2.

## Privacy
- **Where films live:** cached in `~/Library/Application Support/Ancilla/films` on macOS (the local
  application data folder elsewhere), never in the repo. They contain **every player's gamertag and
  XUID**, and a scoreboard is saved beside each one.
- **Never commit** film data, match IDs tied to people, or other players' gamertags or XUIDs. That
  covers code, docs, test fixtures and commit messages. Describe findings with counts and offsets
  instead.
- **Output:** `inspect` hides gamertags unless you pass `--show-names`.
- **The token** is read only from `HALO_SPARTAN_TOKEN` and held only by `HaloWaypointClient`, which
  fetches the manifest and scoreboard. Film chunks download from blob storage without it, and
  automatic redirects are off, so no request carries it to another host. It is never printed or
  saved.
  Treat film chunk URLs as personal data, since anyone with one can download the film.
