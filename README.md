<p align="center">
  <img src="branding/favicon-suite/ancilla-symbol-transparent.svg" alt="The Ancilla symbol" width="160">
</p>

# Ancilla

> **ancilla** (plural *ancillas*): in the Halo universe, what the Forerunners called their artificial
> intelligences. The term comes from Greg Bear's Forerunner Saga novels. Ancillas range from the
> monitors that look after the Halo rings, such as 343 Guilty Spark, to vast minds like Mendicant
> Bias. Humans would just say "AI".

An unofficial Halo Infinite stats viewer, built with Blazor. It shows how many more matchmade games
you need to finish your battle passes and reach Hero, plus your service record, medals, match
history, trends, insights and the players you meet. It uses the services behind halowaypoint.com.

```
src/Ancilla.Core     API client, models, calculations, caching, snapshot store (no NuGet deps)
src/Ancilla.Web      Blazor Web App (interactive server rendering)
src/Ancilla.Theater  Theater film decoding and match analysis, used by the web app (see src/Ancilla.Theater/README.md)
src/Ancilla.Theater.Cli  research console for Theater films (fetch, inspect, timeline)
tests/               unit, component and browser tests on synthetic data (dotnet test; see Tests)
```

## Run

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). The projects target `net10.0`.
Change `<TargetFramework>` in both `.csproj` files if you're on an older SDK.

1. Sign in at https://www.halowaypoint.com.
2. In your browser's DevTools, go to Application > Cookies and copy the value of `343-spartan-token`.
3. Start the app and open http://localhost:5260:

   ```powershell
   dotnet run --project src/Ancilla.Web --launch-profile http
   ```

4. Paste the token into the sign-in page.

To skip step 4, set `HALO_SPARTAN_TOKEN` before starting and the app signs in straight away:

```powershell
$env:HALO_SPARTAN_TOKEN = 'v4=...'
dotnet run --project src/Ancilla.Web --launch-profile http
```

API calls run on the server, so there's no CORS and the token is never sent to the browser. The token
is held in memory for that browser session only. Tokens expire after a few hours.

### Hot reload while developing

Use `dotnet watch` instead of `dotnet run` and saved changes are applied to the running app:

```powershell
$env:DOTNET_WATCH_RESTART_ON_RUDE_EDIT = 'true'   # restart without asking when a change can't be hot reloaded
dotnet watch --project src/Ancilla.Web --launch-profile http
```

In bash or zsh, set the variable with `export DOTNET_WATCH_RESTART_ON_RUDE_EDIT=true`.

Markup, scoped CSS, `app.css` and most method-body edits apply in place, and you stay signed in.
Some changes, such as adding a type, changing a record's parameters or editing `Program.cs`, need a
restart. A restart clears a pasted token, so set `HALO_SPARTAN_TOKEN` to be signed back in
automatically.

### Keep it local

Ancilla is built to run on your own machine and listens on `localhost` only. Don't expose it to a
network (`--urls http://0.0.0.0:...`, a tunnel, a hosting service) without adding proper sign-in.
With `HALO_SPARTAN_TOKEN` set, **everyone** who opens the app is signed in as you.

Never commit a token. `.gitignore` already leaves out `.env` files and `appsettings.*.local.json`.

## Tests

```powershell
dotnet test                                    # every test project, browser tests included
dotnet test tests/Ancilla.Web.Tests            # or one project at a time
```

Coverage (CI writes the same summary to each build-and-test job's page):

```powershell
dotnet test --filter "FullyQualifiedName!~Ancilla.Web.E2E" --collect:"XPlat Code Coverage" --results-directory TestResults
dotnet tool restore
dotnet reportgenerator -reports:"TestResults/**/coverage.cobertura.xml" -targetdir:coverage -reporttypes:"Html;TextSummary" -assemblyfilters:"-Ancilla.Testing" -classfilters:"-System.Text.RegularExpressions.Generated*"
```

Open `coverage/index.html` for line-by-line detail. Coverage is reported, not enforced. The browser
tests aren't counted, because the app they drive runs in its own process.

Nothing needs a Spartan token or a network connection to Halo. The tests come in five layers:

| Project | What it covers | Tools |
|---|---|---|
| `tests/Ancilla.Core.Tests` | The API client (URLs, headers, ID checks, error messages), progress and service record maths, caching, snapshots | xUnit |
| `tests/Ancilla.Data.Tests` | The SQLite archive: schema migrations (an empty database and every earlier schema), storage per player, the one-off snapshot import, CSV and JSON export | xUnit |
| `tests/Ancilla.Theater.Tests` | Theater film decoding, on films built bit by bit | xUnit |
| `tests/Ancilla.Web.Tests` | Formatting and chart helpers, `HaloSession` (sign-in, filters, caching, throttling, quiet failures), and every page and shared component rendered without a browser | xUnit, bUnit |
| `tests/Ancilla.Web.E2E` | The real app in Chromium: every route, sign-in, navigation, themes, the token staying on the server, and axe accessibility checks (WCAG 2.1 A and AA, colour contrast included) on every main page in every theme | xUnit, Playwright, axe-core |

`tests/Ancilla.Testing` holds a fake of the Halo Waypoint services (`FakeHalo`) that answers the
same URLs as the real ones, from a made-up world (`HaloWorld`): Noble Six and the rest of Noble Team,
Blue Team and a few Sangheili, playing Slayer, Oddball, CTF, free-for-all and Firefight. Service
records are added up from its matches, so filtered and unfiltered figures agree. Its two custom games
have Theater films, built bit by bit (`Synthetic.cs`, which the Theater tests share): one with a short
kill feed and one that ended without a summary. The component and
browser tests use it in place of halowaypoint.com, and the browser tests sign in with its token
through `HALO_SPARTAN_TOKEN`.

The first browser test run downloads Chromium (about 150 MB) through Playwright. CI runs the browser
tests in their own Ubuntu job.

## Pages

Home · Progress · Stats › Service record, Medals · Matches › History, Trends, Insights, Players · About · Settings, Diagnostics (from the user menu and the footer)

- **Home** (where you land): profile header plus an overview card per section: career and
  active-pass progress, headline stats and top medals, your last 10 results with a K/D sparkline
  and latest games, and the players you've seen most recently.
- **Progress**: battle passes and career rank (with rank icon), and games to go for each (career
  XP and pass XP per match are measured separately; pass XP comes in much more slowly), plus a
  **Road to Hero** forecast: XP pace over the chosen window, from the saved snapshots, projected to a Hero date.
  The forecast needs snapshots at least a day apart.
- **Service record**: lifetime stats, ranked CSR, top medals, combat stats with per-match figures,
  and a card per game mode (matches, win rate, K/D and that mode's own stats).
  Shows PvP only by default (Firefight left out); filter to Firefight or everything, and to ranked
  or unranked.
- **Medals**: every medal with its icon and count. Its own PvP / Firefight / All and ranked filter,
  search, earned / not earned yet / all, type pills showing how many of each you have, and a
  collection strip by difficulty that filters the grid.
- **History**: recent games (matchmaking, custom or all) with map, mode, score and your K/D/A.
  Select one for the full scoreboard, your medals, expected-vs-actual kills/deaths, MMR and CSR change.
- **Match timeline** (from a match's page): read from the match's Theater film, which is downloaded
  when you ask for it. Team kills by minute; a map (PvP) of where players died and which way they
  faced, kills drawn from killer to victim, a heatmap of where players spent time, objective
  events, and an animation of everyone's movement (play, scrub, speed, kill flashes), drawn on the
  map's own floor plan for Forge maps (from the map's navmesh). Hover over a death or kill for its
  details. The map starts on you; a table per player (best streak, traded and revenge kills, nemesis, objectives, kill
  distance) and the kill feed with each kill's medals, weapon pickups and, in CTF, flag actions (taken,
  picked up, captured, dropped, returned, carrier stopped). Filter the map by player
  or team, and the feed by player.
- **Trends**: your last 25/50/100 PvP games (or everything in the archive) charted: K/D per match with a 10-game rolling K/D,
  kills above/below what the matchmaker expected, and CSR across ranked games.
- **Insights**: over your last 50/100/200 PvP games, or everything in the archive:
  - win rate and K/D by map, mode or playlist (dot plot; hollow dots mean fewer than 3 games)
  - actual vs expected kills per game (scatter around the y = x line)
  - win rate by team MMR gap, showing how much lobby balance decided your results
- **Players**: everyone who's been in at least two of your last 50/100/200 games (or all stored ones), with your record
  with them as teammates and against them as opponents. Select a player (here or on any scoreboard)
  for a head-to-head: per-game averages side by side and the list of shared matches.
- **About**: what the app is, where the data comes from, what happens to your token and data,
  how each derived number is worked out, and the disclaimer.
- **Settings**: the match archive (what's stored, where, how big), **Load older matches** to
  backfill your whole history, **Export as CSV / JSON** (one row per match, with no other players'
  names or IDs), and the Theater film cache with its size limit and **Clear films**.
- **Diagnostics**: app version, operating system, how each Halo service has been answering (calls,
  errors, last status and time), cache sizes (shared data, images, snapshots, films, logs) and how many
  requests are running or waiting. **Copy diagnostics** puts a redacted text summary on the clipboard
  for a [bug report](.github/ISSUE_TEMPLATE/bug.yml).

Firefight (PvE) games are left out of Trends and Insights, because kills against AI would swamp the
scale. Every chart has a "Show as table" view. Every page has a footer stating that this is an
unofficial fan project, not affiliated with Microsoft, Xbox or Halo Studios.

## Themes

Pick a theme from the menu under your gamertag: **Basic** (follows the system light/dark setting),
**Halo** or **XBOX** (original Xbox). The choice is saved in a cookie, so the server renders the page
in that theme from the first paint. Halo and XBOX headings use the Rajdhani and Exo 2 web fonts from
Google Fonts. Without a connection they fall back to the system font.

To add a theme (e.g. Xbox 360):

1. Add a `:root[data-theme="x360"] { ... }` block to `wwwroot/app.css`. Set every colour variable,
   plus shape and type (`--radius-*`, `--font-display`, `--page-bg`), and any extra touches below it.
2. Keep the chart colours (`--viz-1`, `--viz-2`, `--viz-neg`) as the validated blue / orange / red.
   Only re-step them if they drop below 3:1 contrast on the new surface. Body text, muted text and
   links need 4.5:1, and so does the text on buttons and win/loss badges.
3. Add it to `Services/Themes.cs` (ID, name, description, three preview swatches).

## Data, caching and snapshots

- Medal sprite sheets and rank icons need the token to download, so the server fetches them once and
  serves them from memory at `/assets/...`.
- Finished matches, maps, modes and medal metadata never change, so they're cached in memory too
  (`HaloCache`).
- Theater films (for match timelines, 9–30 MB each) are downloaded on request and cached in
  `films` in the same `Ancilla` folder as snapshots, shared with `Ancilla.Theater.Cli`. Set the
  `Films` setting to a folder to store them elsewhere. A film holds every player's gamertag and XUID,
  so keep the folder out of anything you share.
- **The match archive** (`Ancilla.Data`) is a SQLite database, `archive.db`, in an `Ancilla`
  folder under the local application data folder (on macOS `~/Library/Application Support/Ancilla`,
  on Windows `%LOCALAPPDATA%\Ancilla`). It keeps, per player (by XUID): match history entries,
  skill results and progress snapshots, plus scoreboards and versioned map, mode and playlist names,
  which are the same for everyone. Trends, Insights and Players can read all of it
  (**Everything stored**), and after a restart what you've loaded shows without asking Halo again.
  While the app is open, new matches are looked for in the background; **Settings > Load older
  matches** pages back through your whole history. Requests share the app's 8-at-a-time limit and
  back off when the service answers 429 (too many requests). The schema is a numbered list of
  migrations (`ArchiveSchema`), so updates add to the database and never drop data; a database from a
  newer version is refused rather than changed. To store it elsewhere, set the `Archive` setting to a file path:

  ```powershell
  dotnet run --project src/Ancilla.Web -- --Archive="$HOME\Documents\Ancilla\archive.db"
  ```
- Progress snapshots (matches played, total career XP, and the active pass with its XP) are saved
  in the archive each time progress loads. Earlier versions kept them in `snapshots.json` in the
  same folder: that file is copied into the archive once, for the first player to sign in, and left
  where it is. The `Snapshots` setting still points at it.
- The film cache is trimmed to 2 GB, oldest films first, whenever a film is downloaded. Set
  `FilmCacheLimitMb` to change the limit. Settings can clear it.

### Logs

The app writes a log to a `logs` folder next to the snapshots: one `ancilla-yyyyMMdd.log` a day, rolled
at 2 MB and kept for a week. It records each call to Halo by service name, result and time taken (never
the URL, which holds IDs), plus start-up and sign-in. Before any line is written, `Redactor` removes
Spartan tokens, clearance IDs, XUIDs (decimal, hex and `xuid(...)`), gamertags, film download URLs and
absolute paths. To store logs elsewhere, set the `Logs` setting to a folder.

To check the logs the browser tests produce, set `ANCILLA_E2E_LOGS` to a folder when running them, then run
`.github/scripts/privacy-check.sh --log <folder>`. CI does this in the browser tests job.

### How XP per match is worked out

The API gives no per-match XP figure, so the rate is measured from the difference between an earlier
snapshot and now. The Progress page lets you pick the window: the last 10 or 50 matches, the last 7, 30
or 60 days (the default), the last 12 months, all recorded history, the lifetime average, or a figure
you enter. Snapshots are only saved when progress loads, so the app uses the snapshot that best fits:
for "last N matches", the latest one at least N matches back; for "last N days", the earliest one in
those days. A baseline needs at least 5 matches since it. If none fits, the lifetime average is used,
which is only a rough guide. It includes challenge XP, which is what you want: it's the real XP you
get per game played.

## API notes (reverse engineered November 2026)

Every request sends `x-343-authorization-spartan: v4=...`, the value of the `343-spartan-token`
cookie. Economy endpoints also need `343-clearance: <flight id>`; without it they return
`400 Clearance not specified`. Error details come back URL-encoded in the `x-343-exception`
response header.

| Purpose | Endpoint |
|---|---|
| Who am I (xuid, gamertag) | `GET https://profile.svc.halowaypoint.com/users/me` |
| Clearance / flight ID | `GET https://settings.svc.halowaypoint.com/oban/flight-configurations/titles/hi/audiences/RETAIL/players/xuid({xuid})/active?sandbox=UNUSED&build=222249.22.06.08.1730-0` |
| Career rank progress* | `GET https://economy.svc.halowaypoint.com/hi/players/xuid({xuid})/rewardtracks/careerranks/careerrank1` |
| All pass progress + active pass* | `GET https://economy.svc.halowaypoint.com/hi/players/xuid({xuid})/rewardtracks/operations` |
| Track definition (XP per rank) | `GET https://gamecms-hacs.svc.halowaypoint.com/hi/Progression/file/{RewardTrackPath}` |
| Service record (matches played; optional `?gameVariantCategory=` and `isRanked=`) | `GET https://halostats.svc.halowaypoint.com/hi/players/xuid({xuid})/Matchmade/servicerecord` |
| Match history (max 25 per page; `type` = All, Matchmaking, Custom, Local) | `GET https://halostats.svc.halowaypoint.com/hi/players/xuid({xuid})/matches?start=0&count=25&type=All` |
| Match scoreboard (all players, teams, medals, mode stats) | `GET https://halostats.svc.halowaypoint.com/hi/matches/{matchId}/stats` |
| Match skill (MMR, CSR before/after, expected kills/deaths) | `GET https://skill.svc.halowaypoint.com/hi/matches/{matchId}/skill?players=xuid({xuid})` |
| Playlist CSR (current, season best, all-time best; 400/404 if unranked) | `GET https://skill.svc.halowaypoint.com/hi/playlist/{playlistId}/csrs?players=xuid({xuid})` |
| Map / mode / playlist name and images | `GET https://discovery-infiniteugc.svc.halowaypoint.com/hi/{maps\|ugcGameVariants\|playlists}/{assetId}/versions/{versionId}` |
| Medal names, descriptions, sprite layout | `GET https://gamecms-hacs.svc.halowaypoint.com/hi/Waypoint/file/medals/metadata.json` |
| Medal sprite sheet (16 columns) | `GET https://gamecms-hacs.svc.halowaypoint.com/hi/Waypoint/file/medals/images/medal_sheet_med.png` |
| Career rank icons (`RankIcon` paths in the career track) | `GET https://gamecms-hacs.svc.halowaypoint.com/hi/images/file/{path}` |
| Gamertags and gamerpics (batch) | `GET https://profile.svc.halowaypoint.com/users?xuids={xuid},{xuid},...` |
| Service tag* | `GET https://economy.svc.halowaypoint.com/hi/players/xuid({xuid})/customization/appearance` |
| Theater film manifest (chunk list, blob prefix; used by Ancilla.Theater) | `GET https://discovery-infiniteugc.svc.halowaypoint.com/hi/films/matches/{matchId}/spectate` |
| Map navmesh, for the timeline's floor plan (Forge maps only; deflate + Havok tagfile; public, sent without the token) | `GET https://blobs-infiniteugc.svc.halowaypoint.com/ugcstorage/map/{assetId}/{versionId}/navmesh.blob` (listed in the map's `Files`) |
| Theater film chunks (zlib; public on `blobs-infiniteugc.svc.halowaypoint.com`, sent without the token) | `GET {BlobStoragePathPrefix}filmChunk{n}` (the prefix is a full URL from the manifest) |

\* needs `343-clearance`.

Map/mode/playlist thumbnails (`Files.Prefix` + `images/thumbnail.jpg` or `images/hero.jpg`) are public.
Everything on gamecms-hacs needs the token, images included. Display strings there are localised
objects (`{"value": "...", "translations": {...}}`), read by `LocalizedStringConverter`.

Service record filters: `gameVariantCategory={n}` narrows to one mode category and `isRanked=true|false`
to ranked or unranked; the two combine. Categories partition every matchmade match (they sum to the
total). Firefight is **42**. Others, matched to the stat groups they return: 6 Slayer, 7 Attrition,
8 Elimination, 9 Fiesta, 11 Strongholds, 12 King of the Hill, 14 Total Control, 15 CTF, 17 Extraction,
18 Oddball, 19 Stockpile, 22 Infection, 24 Gun Game, 39 Land Grab, 41 Minigame (9, 24 and 41 are
best guesses: their stat groups don't pin them down). Repeating
`gameVariantCategory` gives meaningless totals, so PvP is worked out as everything minus Firefight.
`seasonId` is accepted but only accounts for some matches, and `playlistAssetId` returns 400.

Match quirks: `Outcome` is 1 Tie, 2 Win, 3 Loss, 4 Left. In team modes a player's `Rank` is their
team's placement, not their own. CSR `SubTier` is zero-based (1050 = Platinum 4). Medal `NameId`s
can exceed `int.MaxValue`.

Progress semantics: `CurrentProgress.Rank` = ranks **completed**, and `PartialProgress` = XP into
the next one. Operations have a flat `XpPerRank` (1,000). Career rank has 272 ranks, each with its
own `XpRequiredForRank`; reaching Hero takes 9,319,351 XP in total.

## Ideas for next steps

- Proper sign-in rather than pasting the cookie: Microsoft account OAuth, then an Xbox Live user
  token, then an XSTS token for the Halo relying party, then a Spartan token from
  `settings.svc.halowaypoint.com/spartan-token`. This needs an Azure AD app registration.
- More themes: Xbox 360, Xbox One, Xbox Series X|S.

## Disclaimer

Ancilla is an unofficial fan project. It is not affiliated with, endorsed, sponsored or approved by
Microsoft, Xbox or Halo Studios. Halo, Xbox and related names and images are trademarks or property
of Microsoft Corporation. The services it calls are undocumented and can change without notice.
