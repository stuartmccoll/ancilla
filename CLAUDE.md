# CLAUDE.md

Ancilla is an unofficial Halo Infinite stats viewer: a .NET 10 Blazor app that calls the undocumented
services behind halowaypoint.com with the user's Spartan token. The pages live in a Razor class
library (`Ancilla.UI`) with two hosts: the desktop app (`Ancilla.App`, .NET MAUI Blazor Hybrid, Windows
and macOS) and a web dev host (`Ancilla.Web`, interactive server rendering) for hot reload and the
browser tests. See README.md for features, the API endpoint table and data quirks.

## Commands

```powershell
dotnet build                                                   # from the repo root (Ancilla.sln)
dotnet test                                                    # every test project (first run downloads Chromium)
dotnet test --filter "FullyQualifiedName!~Ancilla.Web.E2E"     # everything but the browser tests
.github/scripts/privacy-check.sh                               # no tokens, XUIDs, film data or machine paths
dotnet run --project src/Ancilla.Web --launch-profile http     # http://localhost:5260
dotnet watch --project src/Ancilla.Web --launch-profile http   # same, with hot reload
dotnet run --project src/Ancilla.App                            # the desktop app (Windows; needs the MAUI workload)
```

`Ancilla.App` isn't in `Ancilla.sln`: it needs the MAUI workload and has no Linux target, so
`dotnet build` from the root skips it. Build it on its own after changing it, or anything it
registers. On Windows it builds the WinUI target; on macOS, run it with
`dotnet build src/Ancilla.App -t:Run -f net10.0-maccatalyst`.

With `dotnet watch`, some edits (new types, changed record parameters, `Program.cs`) can't be
hot reloaded and force a restart. A restart drops every session, so the user signs in again unless
`HALO_SPARTAN_TOKEN` is set. If you change a record's shape or other structure, tell the user
a restart is coming.

Tests (see the README's Tests section for the full table):

- `tests/Ancilla.Core.Tests` (xUnit): the API client, progress and service record maths, caches, snapshots.
- `tests/Ancilla.Data.Tests` (xUnit): the SQLite archive, its migrations, snapshot import and export.
- `tests/Ancilla.Theater.Tests` (xUnit): film decoding. Its films are synthetic, built bit by bit in
  `tests/Ancilla.Testing/Synthetic.cs`.
- `tests/Ancilla.UI.Tests` (xUnit, bUnit): `Ancilla.UI`'s services, `HaloSession`, and pages and components
  rendered without a browser. Derive from `Components/ComponentTest`; click through its `Click`/`OnRenderer`
  helpers, because pages re-render as background data arrives. It registers the web host's
  `IThemePreference` and `IFileExport`; set `Theme.Saved`, or register another `IFileExport`, to act as
  the desktop app.
- `tests/Ancilla.Web.E2E` (Playwright, axe): the web dev host in Chromium on a loopback port, signed in via
  `HALO_SPARTAN_TOKEN`, with axe run on every main page in every theme.
- `tests/Ancilla.Testing`: `FakeHalo`, a fake of the Halo services serving a made-up `HaloWorld`
  (films included, for its two custom games). Pages and the browser tests talk to it instead of halowaypoint.com. Add new endpoints here when you add them
  to `HaloWaypointClient`, and new pages to the routes in `SmokeTests` and `AccessibilityTests`.

All test data is made up and Halo-themed (Noble Team, Blue Team, Sangheili). Never add real film
data, API responses, gamertags or XUIDs as fixtures. Write XUIDs in hex (`Spartans.Xuid`), so the privacy
check never sees a decimal one. Verify changes by building with zero warnings and running the tests.
For UI changes, also run the app. CI (`.github/workflows/ci.yml`) runs the privacy check, then builds
with warnings as errors and runs the tests on Ubuntu and Windows, with the browser tests in a separate
Ubuntu job and the desktop app built on Windows and macOS, for every pull request and every push to `main`. Each build-and-test job writes a coverage
summary to its page (reported, not enforced); see the README for running it locally. Dependabot (`.github/dependabot.yml`) opens weekly PRs
updating NuGet packages and the Actions CI uses; minor and patch updates come grouped. Every page needs a real Spartan token,
so you can't check pages against live data yourself; the fake backend shows they work, not that the
real API still returns what the fake does. Say so rather than claiming a page works.

## Layout

- `src/Ancilla.Core`: no NuGet dependencies, no ASP.NET. Holds the API client
  (`HaloWaypointClient`), JSON models (`Models.cs`, `StatsModels.cs`), progress maths
  (`ProgressService`), `HaloCache`, `SnapshotStore` and the Forge map navmesh reader (`NavMesh`).
- `src/Ancilla.Data`: the local SQLite archive (`ArchiveStore`) of matches, skill results, UGC names and
  progress snapshots, keyed by XUID, with numbered schema migrations in `ArchiveSchema`. It's the only
  project that uses `Microsoft.Data.Sqlite`. Add a schema change as a new migration at the end; never
  edit a released one. `HaloSession` reads through it: memory cache, then archive, then the API.
- `src/Ancilla.UI`: the Blazor app itself, a Razor class library both hosts share. It uses packages
  only, never the ASP.NET shared framework (`HttpContext`, endpoints, middleware), so the MAUI head
  can reference it and it builds and tests on Linux. Its static files are served at `_content/Ancilla.UI/`.
  - `Services/HaloSession.cs`: the signed-in client and the player's data, and the only thing pages
    should call for Halo data. Scoped per circuit on the web host, a singleton in the desktop app.
  - `Services/AssetCache.cs`: token-gated CMS images, held in memory and served at `assets/{key}`
    (an endpoint on the web host, `AssetFileProvider` in the desktop app).
  - `Services/AncillaServices.cs`: `AddAncilla`, the registrations both hosts share.
    `Services/HostServices.cs`: what differs per host, `IThemePreference` and `IFileExport`.
  - `Services/Themes.cs` and `wwwroot/app.css`: the theme catalogue and its CSS variables.
  - `Components/Pages`: one page per route. `Components/Shared`: charts and widgets.
    Each component has a `.razor.css` scoped stylesheet.
- `src/Ancilla.App`: the desktop app, a .NET MAUI Blazor Hybrid head (WinUI and Mac Catalyst). It
  shows `Ancilla.UI`'s `Routes` in a `BlazorWebView` (`MainPage.cs`), whose file provider also serves
  `assets/`. It keeps the theme in MAUI `Preferences` and saves exports to Downloads.
- `src/Ancilla.Web`: the web dev host. `App.razor` (the page shell, which reads the theme cookie),
  `Program.cs` (the `/assets` endpoint) and the reconnect modal. Nothing else belongs here.
- `branding/`: source logo and the full favicon suite. The icons the hosts use are copied into
  `src/Ancilla.Web/wwwroot` and `src/Ancilla.App/Resources`, and the navbar logo is inlined in
  `src/Ancilla.UI/Components/Shared/Logo.razor` (charcoal parts use `currentColor` so it follows each theme).
- `tests/`: unit, component and browser tests, and the fake Halo backend they share (see Commands).
- `src/Ancilla.Theater`: library that decodes Theater films into match analysis (see its `README.md`
  and `FORMAT.md`). `MatchTimeline` is what the match timeline page shows. It references
  `Ancilla.Core`, and `Ancilla.UI` references it.
- `src/Ancilla.Theater.Cli`: the research console (`fetch`, `inspect`, `timeline`), run with
  `dotnet run --project src/Ancilla.Theater.Cli -- <command>`. Films contain every player's gamertag and XUID: keep them in its cache outside
  the repo, and never commit film data or paste other players' gamertags into code, docs or
  commit messages.

## Architecture rules

- All Halo calls happen in the app's .NET process, never in page JS or web storage. The Spartan
  token lives only inside `HaloWaypointClient`, in that process's memory. Never log it, render it,
  pass it to JS interop, put it in a URL, cookie, web storage, preferences or file, or include it in
  exception messages.
- On the web host, render mode is `InteractiveServer` with `prerender: false`, set once in
  `App.razor`, so the scoped `HaloSession` survives navigation and API calls aren't made twice. The
  desktop app has one `HaloSession` for the whole app. Don't write pages that depend on which host
  they're in: anything host-specific goes behind an interface in `Services/HostServices.cs`, with an
  implementation in each host.
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
- The web dev host is meant to run on localhost only. `HALO_SPARTAN_TOKEN` signs in every visitor, so
  never add anything that makes it listen on other interfaces by default. The desktop app doesn't
  listen at all; keep it that way.
- Keep the unofficial-fan-project disclaimer on the About page, in the footer and in the README.
