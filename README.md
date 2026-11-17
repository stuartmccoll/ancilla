# Ancilla

An unofficial Halo Infinite stats viewer, built with Blazor. It shows how many more matchmade games
you need to finish your battle passes and reach Hero, plus your service record, medals, match
history, trends, insights and the players you meet. It uses the services behind halowaypoint.com.

```
src/Ancilla.Core     API client, models, calculations, caching, snapshot store (no NuGet deps)
src/Ancilla.Web      Blazor Web App (interactive server rendering)
src/Ancilla.Theater  Theater film decoding and match analysis, used by the web app (see src/Ancilla.Theater/README.md)
src/Ancilla.Theater.Cli  research console for Theater films (fetch, inspect, timeline)
tests/               unit tests for Ancilla.Theater, on synthetic films (dotnet test)
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

## Pages

Home · Progress · Stats › Service record, Medals · Matches › History, Trends, Insights, Players · About

- **Home** (where you land): profile header plus an overview card per section: career and
  active-pass progress, headline stats and top medals, your last 10 results with a K/D sparkline
  and latest games, and the players you've seen most recently.
- **Progress**: battle passes and career rank (with rank icon), and games to go for each, plus a
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
  faced, kills drawn from killer to victim, a heatmap of where players spent time, and objective
  events, drawn on the map's own floor plan for Forge maps (from the map's navmesh); a table per player (best streak, traded and revenge kills, nemesis, objectives, kill
  distance) and the kill feed with each kill's medals. Filter the map by player or team, and the
  feed by player.
- **Trends**: your last 25/50/100 PvP games charted: K/D per match with a 10-game rolling K/D,
  kills above/below what the matchmaker expected, and CSR across ranked games.
- **Insights**: over your last 50/100/200 PvP games:
  - win rate and K/D by map, mode or playlist (dot plot; hollow dots mean fewer than 3 games)
  - actual vs expected kills per game (scatter around the y = x line)
  - win rate by team MMR gap, showing how much lobby balance decided your results
- **Players**: everyone who's been in at least two of your last 50/100/200 games, with your record
  with them as teammates and against them as opponents. Select a player (here or on any scoreboard)
  for a head-to-head: per-game averages side by side and the list of shared matches.
- **About**: what the app is, where the data comes from, what happens to your token and data,
  how each derived number is worked out, and the disclaimer.

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
- Each time progress loads, a snapshot (matches played and total career XP) is saved to
  `snapshots.json` in an `Ancilla` folder under the local application data folder. On macOS
  that's `~/Library/Application Support/Ancilla`, and on Windows `%LOCALAPPDATA%\Ancilla`.
  To store it elsewhere, set the `Snapshots` setting to a file path:

  ```powershell
  dotnet run --project src/Ancilla.Web -- --Snapshots="$HOME\Documents\Ancilla\snapshots.json"
  ```

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
