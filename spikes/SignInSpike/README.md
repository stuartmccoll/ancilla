# Sign-in spike (issue #16)

Throwaway. Answers whether Microsoft sign-in finishes inside a MAUI app's embedded web view and
whether the app can read the `343-spartan-token` cookie. Not in `Ancilla.sln`; never merged.

The page shows facts about the token (found, expiry, format, an 8-character hash), never the token.
The token is held in memory only, in `SpikeService`.

## Run

macOS (needs Xcode and `sudo dotnet workload install maui-maccatalyst`):

    dotnet build -t:Run -f net10.0-maccatalyst

Windows (needs the .NET 10 SDK and `dotnet workload install maui-windows`, from an administrator
prompt; on Windows the project targets Windows only, so restore doesn't ask for the Mac Catalyst workload):

    dotnet build -t:Run -f net10.0-windows10.0.19041.0

## What each section answers

| Question | How |
| --- | --- |
| 1. Sign-in finishes in WKWebView / WebView2 | "Open sign-in", sign in with 2FA. The log lists each request (host and path, never the query). If Microsoft refuses the browser, tick the user agent box and try again. |
| 2. Cookie readable | "Token found", with HttpOnly shown. |
| 3. Token works with `HaloWaypointClient` | Section 3 (status codes only). |
| 4. Hidden refresh | Section 4. A visible sign-in teaches the spike the entry path (`/sign-in`). "Hidden sign-in" replays it in a hidden web view with `prompt` rewritten to `none`, so Microsoft signs in silently. A plain reload of Waypoint doesn't issue a token. |
| 5. Profile in a folder we choose | Section 5. Quit and reopen, then "Hidden sign-in", to test "stay signed in" (the token is a session cookie, so it doesn't survive a restart). "Sign out", then "Hidden sign-in", should end with `error=login_required`. |

The results for both platforms are in issue #16.

Profiles: macOS uses a `WKWebsiteDataStore` with its own identifier (stored under the app
container's `Library/WebKit`). Windows uses `%LOCALAPPDATA%\AncillaSignInSpike\WebView2`.
