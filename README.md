# ClaudeBar

A small always-on-top panel on the Windows desktop, near the clock, that shows
how much of the current Claude Code usage limit is gone — and (soon) switches to
another Anthropic account so a session can carry on when one runs out.

Windows 11, .NET 10 + WPF, no Electron. It reads the same usage figure Claude
Code's own `/usage` reports, so the number is real rather than estimated.

**Status: the status bar works and shows real numbers.** The account switcher is
designed but not wired up yet — see "Phase 2" below, which needs one decision
before it gets built.

```
┌──────────────────────────────────────┐
│  5h  ●  ██████░░░░░░   42%   ↻4h 12m │
│  7d  ●  ███████░░░░░   56%   ↻1d 23h │
└──────────────────────────────────────┘
```

## Spike 1 — where the limit number comes from: SOLVED

**It is a real figure from Anthropic, not an estimate.** No transcript
scraping, nothing to label as a guess.

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <claudeAiOauth.accessToken from ~/.claude/.credentials.json>
```

Verified 2026-09-17 against `claude.exe` 2.1.274 by finding the endpoint in the
binary and then calling it directly with the stored token — HTTP 200, real
percentages that match what `/usage` shows in the TUI.

There is **no** `claude usage` CLI subcommand, and **no** cached limit figure
anywhere in `~/.claude/` or `~/.claude.json` (checked: `policy-limits.json` is
org policy, `stats-cache.json` is message counts). The API call is the only
local route to the number, which is why ClaudeBar makes it itself.

Response, trimmed to what matters:

```json
{
  "five_hour": { "utilization": 22.0, "resets_at": "2026-09-17T16:00:00Z" },
  "seven_day": { "utilization": 53.0, "resets_at": "2026-09-19T11:00:00Z" },
  "limits": [
    { "kind": "session",    "group": "session", "percent": 22,
      "severity": "normal", "resets_at": "...", "is_active": false },
    { "kind": "weekly_all", "group": "weekly",  "percent": 53,
      "severity": "normal", "resets_at": "...", "is_active": true }
  ],
  "extra_usage": { "is_enabled": false, "...": "..." }
}
```

**Read `limits[]`, not the top-level keys.** This is the thing most likely to
break on a Claude Code update, and it is worth being deliberate about: the
top-level response is littered with rotating internal codenames — this machine
saw `tangelo`, `nimbus_quill`, `cedar_ember`, `iguana_necktie`,
`omelette_promotional`, `copper_kite`, `harbor_lantern`, `amber_ladder`,
`juniper_tide`, `amber_gauge` — most of them `null`, and clearly not stable
across releases. `limits[]` carries the same information in a self-describing
shape (`kind`/`group`/`percent`/`severity`/`resets_at`), so ClaudeBar renders
one row per entry and ignores keys it does not recognise. A new limit window
appearing should just show up as a third row, not a crash.

`UsageService.Parse` falls back to `five_hour`/`seven_day` if `limits[]` ever
goes missing, and the pill degrades to a plain message if both do.

**The endpoint rate-limits.** It returns HTTP 429 if polled too often — easy to hit while
developing, since every restart polls immediately. Worth knowing: it has been seen
returning `Retry-After: 0` alongside a 429, so that header cannot be trusted on its own.
ClaudeBar therefore backs off exponentially (capped at 15 minutes), only honours
`Retry-After` when it is longer than its own backoff, and caches the last good reading to
`%LOCALAPPDATA%\ClaudeBar\last-usage.json` so a restart shows something familiar instead
of an empty pill and a fresh call. Cached readings are shown dimmed, with their timestamp
in the tooltip — never presented as current.

**Token handling.** ClaudeBar only ever *reads* `.credentials.json`. It
deliberately does **not** refresh the token: a refresh rotates the refresh
token, and racing Claude Code for that is how an account gets logged out for
real. If the token is expired, the pill says so and waits for Claude Code to
renew it. Access tokens last ~8h, refresh tokens ~27 days.

## Spike 2 — how an account switch works: mechanism chosen, not yet built

What was proven, 2026-09-17:

- Credentials live in the **file**, not Windows Credential Manager
  (`tengu_windows_credman` is `false` on this machine), so they are swappable.
- **`CLAUDE_CONFIG_DIR` works and fully isolates auth.** Pointing it at an empty
  directory gives `loggedIn: false` from `claude auth status --json`, with the
  real `~/.claude` untouched. Proven non-destructively.
- `claude auth status --json` is a clean way to confirm *which* account is live:
  it prints `email`, `orgName`, `subscriptionType`.
- `claude auth login --email <addr>` exists and pre-populates the login page.

**The decisive evidence came from the existing workflow.** Today, when a limit
is hit, you run `/login` in one terminal, pick a different account, and **every
other running terminal follows** — no restarts. That only works because
every `claude` process re-reads `~/.claude/.credentials.json` from disk when its
cached token expires or gets a 401.

So: **swapping that file in place does exactly what `/login` does, minus the
browser, and propagates to all terminals the same way.** That is the mechanism,
and it is not a guess — the existing workflow is the proof.

Chosen over the alternative: separate `CLAUDE_CONFIG_DIR` per account is safer
in the abstract, but it splits history, settings, transcripts and project trust
per account, and new terminals would need the env var set. Not worth it.

### Phase 2 plan (needs one decision before wiring)

1. **Add an account** — once per account, and only once: `claude auth login`,
   then ClaudeBar captures the resulting credentials into its own store at
   `%LOCALAPPDATA%\ClaudeBar\accounts\<accountUuid>.json`. After that it is a
   button, with no browser.
2. **Switch** —
   1. re-capture the *live* credentials into the current account's slot first,
      so a rotated refresh token is never left stale (this is the one step that
      protects against silently logging an account out);
   2. back up `.credentials.json` and `.claude.json`;
   3. write the target's `claudeAiOauth` into `.credentials.json`, atomically;
   4. patch only the `oauthAccount` block of `.claude.json`, leaving the rest;
   5. verify with `claude auth status --json`, and **restore the backup if the
      email that comes back is not the one asked for**.
3. **Be honest about timing** — running sessions pick the change up when their
   token next expires or 401s, exactly as `/login` behaves today. The UI should
   say that rather than implying it is instant.

**Decided:** the pill will show usage for **all stored accounts**, with a menu toggle
(`ShowAllAccounts`) to fall back to the active account only. The setting and its menu
item exist already but are disabled, because there is nothing to show until accounts can
be added — it lands with the switcher.

This is the one place the "never refresh a token" rule above gets relaxed: an idle
account's access token expires after ~8h, so ClaudeBar has to refresh it to read its
usage. That is safe precisely because the account is idle — Claude Code is not using it
concurrently, so there is no race over the rotating refresh token. ClaudeBar writes the
rotated token straight back to its own slot. The rule stays absolute for the *active*
account.

Nothing credential-shaped goes in this repo; `.gitignore` covers it from the
first commit.

## Stack: .NET 10 + WPF

Picked per the brief, with one change: **.NET 10, not .NET 8.** The
`Microsoft.WindowsDesktop.App 10.0.5` runtime is already installed on this
laptop, and 10 is the current LTS. Everything else is as the brief argued —
native always-on-top, native tray, no Chromium for a 300×60 readout.

Release builds publish as a **self-contained single-file exe** so the other
laptop needs nothing installed.

```
src/ClaudeBar/
  ClaudeBar.csproj      net10.0-windows, WPF + WinForms (tray icon only)
  App.xaml.cs           single-instance guard, tray wiring
  MainWindow.xaml(.cs)  the pill: rows, polling, placement, context menu
  LimitRow.cs           one row's view model + the colour-blind-safe cue logic
  MonitorApi.cs         Win32 monitor enumeration (physical pixels)
  Native.cs             tool-window style, GetWindowRect, SetWindowPos
  Diagnostics.cs        opt-in tracing, CLAUDEBAR_DIAG=1
  Models/Usage.cs       LimitEntry, UsageSnapshot
  Services/
    UsageService.cs     the /api/oauth/usage call and its parsing
    CredentialStore.cs  read-only access to .credentials.json
    AppSettings.cs      settings.json next to the exe
    ScreenService.cs    monitor choice and "above the clock" placement
```

### The one real trap: DPI and multi-monitor

Worth writing down, because it cost a round of debugging and will bite again.
This desk is 2560×1440 at 125% next to a second display. Three coordinate
spaces are in play:

- `Window.Left`/`Top` in WPF are **device-independent units**, and under
  PerMonitorV2 their meaning changes with the monitor the window is on;
- `System.Windows.Forms.Screen` reported a **different space again** — it put
  the pill 100px past the right edge of the primary monitor and onto the next;
- Win32 `SetWindowPos` takes **physical pixels**.

ClaudeBar now uses physical pixels end to end: monitors come from
`EnumDisplayMonitors`/`GetMonitorInfo` (`MonitorApi.cs`), placement goes through
`SetWindowPos` (`Native.MoveTo`). No DPI arithmetic anywhere. Do not reintroduce
`Forms.Screen` or `Window.Left` for placement.

`SizeToContent` is on, so the pill also **re-anchors on every size change** —
otherwise it grows rightward off its corner when the first reading replaces
"starting…".

### Snapping

Placement is anchor-based rather than free-floating: `BottomRight` (above the clock),
`BottomLeft`, `TopRight`, `TopLeft`, `BottomCentre`, `TopCentre`, or `Free`. Dropping the
pill within `SnapThreshold` physical pixels of an edge or corner snaps it there and
remembers the anchor; dropping it in open space keeps the exact position as `Free`.
`SnapPadding` is the gap it keeps from the edges it is snapped to. Because the working
area excludes the taskbar, a bottom anchor snaps to the taskbar's edge where there is one
and the screen edge where there is not. Both values have sliders in the right-click menu.

### Why the bar is drawn by hand

`SegmentBar` renders the segments in `OnRender` instead of using an `ItemsControl` of
`Rectangle`s. The control version looked subtly wrong and it was not imagination: at 125%
scaling a 6px rectangle is 7.5 physical pixels, so WPF rounded some segments to 7 and
others to 8, and accumulated fractional offsets shifted one row against the next. Segment
widths, gaps and x positions are now computed in **whole physical pixels** and converted
back to device-independent units only to draw, so every segment is identical and the rows
line up exactly.

## Colour, and not relying on it

This is built for a red/green colour blind user, so **every state carries three
cues**, and the palette contains no red/green pair at all:

| State | Colour | Glyph | Segments | Number |
| --- | --- | --- | --- | --- |
| normal | teal-green `#34D399` | ● | few filled | shown |
| warning (≥70%) | amber `#FBBF24` | ▲ | ~8/12 filled | shown |
| critical (≥90%) | magenta `#E879F9` | ■ | ~11/12 filled | shown |

The bar is **12 countable segments**, not a smooth fill, so the level is
readable as a quantity even in greyscale. The tray icon uses fill *height* as
well as colour for the same reason. Thresholds are configurable.

`severity` from the API overrides the local thresholds when it is louder.

## Running it

```bash
cd src/ClaudeBar
dotnet run                                   # debug
dotnet publish -c Release                    # self-contained single exe
CLAUDEBAR_DIAG=1 dotnet run                  # trace placement to %LOCALAPPDATA%\ClaudeBar\diag.log
```

Right-click the pill (or the tray icon) for: refresh, **which monitor to show on**,
**snap position + padding sliders**, **background opacity slider**, start with Windows,
hide, open settings.json, quit. Left-click-drag moves it, snapping to whichever edge or
corner it is dropped near. Left-click the tray icon to show/hide.

### settings.json

Written next to the exe, or `%LOCALAPPDATA%\ClaudeBar\` if that is read-only.

| Key | Default | Meaning |
| --- | --- | --- |
| `MonitorDeviceName` | primary | which monitor to park on, e.g. `\.\DISPLAY2` |
| `Anchor` | `BottomRight` | edge/corner to snap to, or `Free` |
| `SnapPadding` | 12 | physical-pixel gap from the snapped edges |
| `SnapThreshold` | 64 | how near an edge a drop must land to snap (0 = off) |
| `OffsetX` / `OffsetY` | 12 | inset from bottom-right, used only when `Anchor` is `Free` |
| `PollSeconds` | 60 | clamped 15–3600; backs off on failure, up to 15 min |
| `WarnAt` / `CriticalAt` | 70 / 90 | threshold percentages |
| `BackgroundOpacity` | 0.92 | background alpha only — text stays fully opaque |
| `ShowAllAccounts` | false | Phase 2; no effect until accounts can be added |
| `Visible` | true | remembered across restarts |

### Behaviour when things are missing

The pill never crashes and never shows a stale number as if it were fresh:
"Claude Code not found", "not signed in", "signed out — run /login", "offline".
If a poll fails while a good reading is on screen, the numbers stay but dim, and
the tooltip says when they were taken.

## Conventions

- **Accessibility is not optional here.** Colour never carries a state on its
  own — see the table above. Any new state needs a shape, a count or a number
  alongside the colour, and the palette must stay clear of red/green pairs.
- **Keep a build runnable.** `dotnet run` should always work from a clean clone.
- **This README is the memory.** The project moves between machines, so
  decisions, findings and gotchas belong here or in the code, not in a chat log.
- **Commit straight to `master`.** No branches unless there is a reason.

## Reference: what is on this machine

- Claude Code state: `%USERPROFILE%\.claude\` and `%USERPROFILE%\.claude.json`.
- `~/.claude/.credentials.json` — one `claudeAiOauth` object: `accessToken`,
  `refreshToken`, `expiresAt`, `refreshTokenExpiresAt`, `scopes`,
  `subscriptionType`, `rateLimitTier`. **Never log it, never copy it off this
  machine, never put it in git.**
- `~/.claude.json` — `oauthAccount` block: `accountUuid`, `emailAddress`,
  `organizationUuid`, `displayName`, `organizationName`, `seatTier`,
  `billingType`, plus rate-limit tiers. Good for naming accounts in the switcher.
- Transcripts: JSONL per session under `~/.claude/projects/<escaped-path>/`.
  456 MB here — only ever stream them. **Not needed any more**: Spike 1 found a
  real number, so transcript estimation is off the table.

## Status

| | |
| --- | --- |
| Folder created | 2026-09-17 |
| Stack | .NET 10 + WPF — decided, see above |
| Spike 1 (limit source) | **done** — `/api/oauth/usage`, verified live |
| Spike 2 (account switch) | **done** — swap `.credentials.json` in place |
| Status bar | **working** — real numbers, multi-monitor, tray, autostart |
| Account switcher | designed, not built — one open question above |
