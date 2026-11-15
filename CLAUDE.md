# CLAUDE.md

Ancilla is an unofficial Halo Infinite stats viewer: a .NET 10 Blazor Web App (interactive server
rendering) that calls the undocumented services behind halowaypoint.com with the user's Spartan
token. See README.md for features, the API endpoint table and data quirks.

## Commands

```powershell
dotnet build                                                   # from the repo root (Ancilla.sln)
dotnet test                                                    # tests/Ancilla.Theater.Tests
.github/scripts/privacy-check.sh                               # no tokens, XUIDs, film data or machine paths
dotnet run --project src/Ancilla.Web --launch-profile http     # http://localhost:5260
dotnet watch --project src/Ancilla.Web --launch-profile http   # same, with hot reload
```

With `dotnet watch`, some edits (new types, changed record parameters, `Program.cs`) can't be
hot reloaded and force a restart. A restart drops every session, so the user signs in again unless
`HALO_SPARTAN_TOKEN` is set. If you change a record's shape or other structure, tell the user
a restart is coming.

`tests/Ancilla.Theater.Tests` (xUnit) covers `Ancilla.Theater` and the Core pieces it uses. Its films
are synthetic, built bit by bit in `Synthetic.cs`: never add real film data or real gamertags or
XUIDs as fixtures. Core and Web otherwise have no tests. Verify changes by building with zero
warnings, running the tests and, for UI changes, running the app. CI (`.github/workflows/ci.yml`)
runs the privacy check, then builds with warnings as errors and runs the tests on Ubuntu and
Windows, for every pull request and every push to `main`. Dependabot (`.github/dependabot.yml`) opens weekly PRs
updating NuGet packages and the Actions CI uses; minor and patch updates come grouped. Every page needs a real Spartan token,
so you usually can't check pages end to end yourself. Say so rather than claiming a page works.

## Layout

- `src/Ancilla.Core`: no NuGet dependencies, no ASP.NET. Holds the API client
  (`HaloWaypointClient`), JSON models (`Models.cs`, `StatsModels.cs`), progress maths
  (`ProgressService`), `HaloCache` and `SnapshotStore`.
- `src/Ancilla.Web`: the Blazor app.
  - `Services/HaloSession.cs`: per-circuit (scoped) state. It holds the signed-in client and the
    player's data, and is the only thing pages should call for Halo data.
  - `Services/AssetCache.cs`: token-gated CMS images, served from memory at `/assets/{key}`.
  - `Services/Themes.cs` and `wwwroot/app.css`: the theme catalogue and its CSS variables.
  - `Components/Pages`: one page per route. `Components/Shared`: charts and widgets.
    Each component has a `.razor.css` scoped stylesheet.
- `tests/Ancilla.Theater.Tests`: unit tests on synthetic films (see Commands).
- `src/Ancilla.Theater`: decodes Theater films into match analysis (see its `README.md` and
  `FORMAT.md`). For now it's a console app used to research the format. It references
  `Ancilla.Core`, and the web app doesn't reference it yet; it will once Theater data feeds the
  app's analysis. Films contain every player's gamertag and XUID: keep them in its cache outside
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
