# CLAUDE.md

Ancilla is an unofficial Halo Infinite stats viewer: a .NET 10 Blazor Web App (interactive server
rendering) that calls the undocumented services behind halowaypoint.com with the user's Spartan
token. See README.md for features, the API endpoint table and data quirks.

## Commands

```powershell
dotnet build                                                   # from the repo root (Ancilla.sln)
dotnet test                                                    # every test project (first run downloads Chromium)
dotnet test --filter "FullyQualifiedName!~Ancilla.Web.E2E"     # everything but the browser tests
.github/scripts/privacy-check.sh                               # no tokens, XUIDs, film data or machine paths
dotnet run --project src/Ancilla.Web --launch-profile http     # http://localhost:5260
dotnet watch --project src/Ancilla.Web --launch-profile http   # same, with hot reload
```

With `dotnet watch`, some edits (new types, changed record parameters, `Program.cs`) can't be
hot reloaded and force a restart. A restart drops every session, so the user signs in again unless
`HALO_SPARTAN_TOKEN` is set. If you change a record's shape or other structure, tell the user
a restart is coming.

Tests (see the README's Tests section for the full table):

- `tests/Ancilla.Core.Tests` (xUnit): the API client, progress and service record maths, caches, snapshots.
- `tests/Ancilla.Theater.Tests` (xUnit): film decoding. Its films are synthetic, built bit by bit in
  `tests/Ancilla.Testing/Synthetic.cs`.
- `tests/Ancilla.Web.Tests` (xUnit, bUnit): web services, `HaloSession`, and pages and components rendered
  without a browser. Derive from `Components/ComponentTest`; click through its `Click`/`OnRenderer`
  helpers, because pages re-render as background data arrives.
- `tests/Ancilla.Web.E2E` (Playwright, axe): the real app in Chromium on a loopback port, signed in via
  `HALO_SPARTAN_TOKEN`, with axe run on every main page in every theme.
- `tests/Ancilla.Testing`: `FakeHalo`, a fake of the Halo services serving a made-up `HaloWorld`
  (films included, for its two custom games). Pages and the browser tests talk to it instead of halowaypoint.com. Add new endpoints here when you add them
  to `HaloWaypointClient`, and new pages to the routes in `SmokeTests` and `AccessibilityTests`.

All test data is made up and Halo-themed (Noble Team, Blue Team, Sangheili). Never add real film
data, API responses, gamertags or XUIDs as fixtures. Write XUIDs in hex (`Spartans.Xuid`), so the privacy
check never sees a decimal one. Verify changes by building with zero warnings and running the tests.
For UI changes, also run the app. CI (`.github/workflows/ci.yml`) runs the privacy check, then builds
with warnings as errors and runs the tests on Ubuntu and Windows, with the browser tests in a separate
Ubuntu job, for every pull request and every push to `main`. Each build-and-test job writes a coverage
summary to its page (reported, not enforced); see the README for running it locally. Dependabot (`.github/dependabot.yml`) opens weekly PRs
updating NuGet packages and the Actions CI uses; minor and patch updates come grouped. Every page needs a real Spartan token,
so you can't check pages against live data yourself; the fake backend shows they work, not that the
real API still returns what the fake does. Say so rather than claiming a page works.

## Layout

- `src/Ancilla.Core`: no NuGet dependencies, no ASP.NET. Holds the API client
  (`HaloWaypointClient`), JSON models (`Models.cs`, `StatsModels.cs`), progress maths
  (`ProgressService`), `HaloCache`, `SnapshotStore` and the Forge map navmesh reader (`NavMesh`).
- `src/Ancilla.Web`: the Blazor app.
  - `Services/HaloSession.cs`: per-circuit (scoped) state. It holds the signed-in client and the
    player's data, and is the only thing pages should call for Halo data.
  - `Services/AssetCache.cs`: token-gated CMS images, served from memory at `/assets/{key}`.
  - `Services/Themes.cs` and `wwwroot/app.css`: the theme catalogue and its CSS variables.
  - `Components/Pages`: one page per route. `Components/Shared`: charts and widgets.
    Each component has a `.razor.css` scoped stylesheet.
- `branding/`: source logo and the full favicon suite. The icons the app uses are copied into
  `src/Ancilla.Web/wwwroot`, and the navbar logo is inlined in `Components/Shared/Logo.razor`
  (charcoal parts use `currentColor` so it follows each theme).
- `tests/`: unit, component and browser tests, and the fake Halo backend they share (see Commands).
- `src/Ancilla.Theater`: library that decodes Theater films into match analysis (see its `README.md`
  and `FORMAT.md`). `MatchTimeline` is what the web app's match timeline page shows. It references
  `Ancilla.Core`, and the web app references it.
- `src/Ancilla.Theater.Cli`: the research console (`fetch`, `inspect`, `timeline`), run with
  `dotnet run --project src/Ancilla.Theater.Cli -- <command>`. Films contain every player's gamertag and XUID: keep them in its cache outside
  the repo, and never commit film data or paste other players' gamertags into code, docs or
  commit messages.

## Architecture rules

- All Halo calls happen on the server. The Spartan token lives only inside `HaloWaypointClient`
  in server memory. Never log it, render it, put it in a URL, cookie, browser storage or file,
  or include it in exception messages.
- Render mode is `InteractiveServer` with `prerender: false`, set once in `App.razor`, so the
  scoped `HaloSession` survives navigation and API calls aren't made twice.
- Shared caches (`HaloCache`, `AssetCache`) are singletons used by every session. Only put data
  in them that is the same for every user and never changes: finished matches, versioned UGC,
  medal metadata. Keep per-player data in `HaloSession`, or key it by XUID.
- Throttle parallel API calls through `HaloSession._throttle` (8 at a time).
- Catch `HttpRequestException` for optional data (gamertags, skill, UGC) and degrade quietly.
  Don't let one failed request break a page.
- Keep `Ancilla.Core` free of package and web dependencies.

## Conventions

- British English in UI text, comments and identifiers (`Normalise`, `colour`, `localised`).
- UI copy is plain and direct, uses sentence case, and avoids jargon. Explain derived numbers on
  the About page whenever you add or change one.
- Every chart has a "Show as table" alternative. Chart colours use `--viz-1`, `--viz-2` and
  `--viz-neg`. Keep 3:1 contrast for chart marks and 4.5:1 for text in every theme.
- Firefight/PvE matches (`RecentMatch.IsPve`) stay out of Trends and Insights.
- Nullable reference types are on. Keep the build free of warnings.
- Update README.md when you add a page, endpoint, setting or theme.

## Privacy and security

- Never commit tokens, personal gamertags, XUIDs, or machine paths. `bin/` and `obj/` are
  gitignored because they contain absolute paths.
- Don't render absolute paths in the UI. Use `SnapshotStore.DisplayPath`, which shows the home
  folder as `~`.
- IDs go into Halo API URLs, and some come from the address bar. `HaloWaypointClient` checks
  every ID it puts in a URL with `HaloIds.RequireGuid` / `RequireXuid`. Keep doing that for any
  new endpoint. Pages taking an ID from the route check it with `HaloIds.IsGuid` / `IsXuid`
  first and show a friendly message. `HaloSession` skips malformed IDs from API data, because a
  bad ID now throws `ArgumentException`, not `HttpRequestException`.
- The app is meant to run on localhost only. `HALO_SPARTAN_TOKEN` signs in every visitor, so
  never add anything that makes it listen on other interfaces by default.
- Keep the unofficial-fan-project disclaimer on the About page, in the footer and in the README.
