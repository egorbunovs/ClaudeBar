# ClaudeBar

A small always-on-top panel on the Windows desktop, near the clock, that shows how much of
the current Claude Code usage limit is gone — and switches to another Anthropic account so a
session can carry on when one runs out.

Windows 11, .NET 10 + WPF, no Electron. It reads the same usage figure Claude Code's own
`/usage` reports, so the number is real rather than estimated.

```
┌────────────────────────────────────────────┐
│  you@example.com                           │
│  5h  ●  ██████░░░░░░   66%      3h 11m     │
│  7d  ●  ███████░░░░░   60%      1d 22h     │
└────────────────────────────────────────────┘
```

**Status:** the readout and the account switcher both work. Showing *all* accounts' usage at
once is the one thing still outstanding — see "Still to do".

## Spike 1 — where the limit number comes from: SOLVED

**It is a real figure from Anthropic, not an estimate.** No transcript scraping, nothing to
label as a guess.

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <claudeAiOauth.accessToken from ~/.claude/.credentials.json>
```

Verified 2026-09-17 against `claude.exe` 2.1.274 by finding the endpoint in the binary and
then calling it directly with the stored token — HTTP 200, real percentages matching what
`/usage` shows in the TUI.

There is **no** `claude usage` CLI subcommand, and **no** cached limit figure anywhere in
`~/.claude/` or `~/.claude.json` (checked: `policy-limits.json` is org policy,
`stats-cache.json` is message counts). The API call is the only local route to the number.

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

**Read `limits[]`, not the top-level keys.** This is the thing most likely to break on a
Claude Code update. The top-level response is littered with rotating internal codenames —
this machine saw `tangelo`, `nimbus_quill`, `cedar_ember`, `iguana_necktie`,
`omelette_promotional`, `copper_kite`, `harbor_lantern`, `amber_ladder`, `juniper_tide`,
`amber_gauge` — most of them `null` and clearly not stable across releases. `limits[]`
carries the same information self-describingly (`kind`/`group`/`percent`/`severity`/
`resets_at`), so ClaudeBar renders one row per entry and ignores keys it does not recognise.
A new limit window should just appear as a third row, not a crash.

`UsageService.Parse` falls back to `five_hour`/`seven_day` if `limits[]` ever goes missing,
and the pill degrades to a plain message if both do.

**The endpoint rate-limits.** HTTP 429 if polled too often — easy to hit while developing,
since every restart polls immediately. It has been seen returning `Retry-After: 0` alongside
a 429, so that header cannot be trusted on its own. ClaudeBar backs off exponentially
(capped at 15 minutes), only honours `Retry-After` when it is longer than its own backoff,
and caches the last good reading to `%LOCALAPPDATA%\ClaudeBar\last-usage.json` so a restart
shows something familiar instead of an empty pill and a fresh call. Cached readings are
dimmed and timestamped in the tooltip — never presented as current.

**Token handling.** For the *active* account ClaudeBar only ever **reads**
`.credentials.json`, and deliberately does not refresh it: a refresh rotates the refresh
token, and racing Claude Code for that is how an account gets logged out for real. Access
tokens last ~8h, refresh tokens ~27 days.

## Spike 2 — account switching: BUILT

What was proven, 2026-09-17:

- Credentials live in the **file**, not Windows Credential Manager
  (`tengu_windows_credman` is `false` here), so they are swappable.
- **`CLAUDE_CONFIG_DIR` works and fully isolates auth.** Pointing it at an empty directory
  gives `loggedIn: false` from `claude auth status --json`, with the real `~/.claude`
  untouched. Claude Code moves its whole config there, `.claude.json` included.
- `claude auth status --json` reports `email`, `orgName`, `subscriptionType` — the authority
  for verifying a switch worked.

**The decisive evidence came from the existing workflow.** When a limit is hit, you run
`/login` in one terminal, pick a different account, and **every other running terminal
follows**, with no restarts. That only works because every `claude` process re-reads
`~/.claude/.credentials.json` from disk when its cached token expires or gets a 401.

So **writing that file does exactly what `/login` does, minus the browser**, and propagates
to all terminals the same way. Not a guess — the existing workflow is the proof.

Chosen over the alternative: a separate `CLAUDE_CONFIG_DIR` per account is safer in the
abstract, but it splits history, settings, transcripts and project trust per account, and
new terminals would need the env var set.

### How a switch runs

1. **Re-capture the live account first.** Claude Code rotates refresh tokens as it goes, so
   the copy taken when the account was added goes stale. Re-capturing means the stored copy
   is always the live one. *This is the step that stops an account being silently logged out.*
2. **Back up** `.credentials.json` and `.claude.json` to
   `%LOCALAPPDATA%\ClaudeBar\backups\<timestamp>\`.
3. **Write** the target's `claudeAiOauth` into `.credentials.json`, atomically, preserving
   any other top-level keys.
4. **Patch** only the `oauthAccount` block of `.claude.json` — the other ~79KB of settings
   are left exactly as they were.
5. **Verify** with `claude auth status --json`, and **roll back** if the email that comes
   back is not the one asked for.

Running sessions pick the change up when their token next expires or 401s, exactly as
`/login` behaves today. The toast says so rather than implying it is instant. The *pill*
itself updates within about a second, via the watcher below.

### Where the credentials are kept

`%LOCALAPPDATA%\ClaudeBar\accounts\<accountUuid>.json`, with the OAuth blob **encrypted at
rest with DPAPI** scoped to the current user. Claude Code keeps its copy in plaintext; there
is no reason for a second plaintext copy to exist. Identity fields (email, org) stay in the
clear so the menu can be built without decrypting anything. Nothing from inside the blob is
ever logged, shown in the UI, or put in a tooltip.

Nothing credential-shaped goes in this repo; `.gitignore` blocks the shape of it.

### Adding accounts — automatic

**Sign in however you like and ClaudeBar notices.** A `FileSystemWatcher` on
`.credentials.json` (`CredentialWatcher`) fires within about a second of any change —
`/login` in a terminal, `claude auth login` by hand, or `Account → Add another account`,
which just runs `claude auth login` for you. Whatever account is then signed in is saved to
the store and the pill refreshes immediately; if it is a different account, a toast says so.

There is deliberately no separate "save" step. The first version had one, and it produced
exactly the confusing outcome you would expect: sign in, see the pill switch, open the menu,
and find the new account missing and the old one still marked current. The watcher removes
the step; the menus now repopulate every time they open so they cannot go stale.

The browser round trip is unavoidable **once per account** — only Anthropic can mint a
session — but it never happens again for that account.

Two more things the watcher gives for free:

- it re-captures on *every* credential write, so the stored copy keeps up with the refresh
  token Claude Code rotates as it goes;
- before `Add another account` launches the login, the account being replaced is saved
  first, so it can never be lost.

`Account → Save the signed-in account` remains as a manual fallback.

### Proving it without risking a login

`--selftest` exercises the store and the switcher against whatever `CLAUDE_CONFIG_DIR`
points at. Run it against a **throwaway copy**, never the live config:

```powershell
$sandbox = "C:\temp\cfgsandbox"
mkdir $sandbox
copy "$env:USERPROFILE\.claude\.credentials.json" "$sandbox\.credentials.json"
copy "$env:USERPROFILE\.claude.json"              "$sandbox\.claude.json"
$env:CLAUDE_CONFIG_DIR = $sandbox
.\ClaudeBar.exe --selftest
```

It checks the capture, the DPAPI round trip, that a **switch whose verification fails rolls
the credentials back byte-for-byte**, and that a switch to the real identity verifies. Last
run, all four passed:

```
PASS capture    : you@example.com (org Example)
PASS round trip : oauth decrypted, accessToken=yes refreshToken=yes (498 chars, not shown)
PASS rollback   : ok=False rolledBack=True credentialsRestored=True
PASS switch     : switched to you@example.com
```

## Stack: .NET 10 + WPF

Picked per the brief, with one change: **.NET 10, not .NET 8** — the
`Microsoft.WindowsDesktop.App 10.0.5` runtime is already installed here and 10 is the
current LTS. Native always-on-top, native tray, no Chromium for a 300×70 readout. Release
builds publish as a self-contained single-file exe.

```
src/ClaudeBar/
  App.xaml.cs           single-instance guard, tray wiring, --selftest entry
  MainWindow.xaml(.cs)  the pill: rows, polling, placement, dragging, menu
  LimitRow.cs           one row's view model + the colour-blind-safe cue logic
  SegmentBar.cs         the usage bar, drawn on whole physical pixels
  GhostWindow.cs        the dashed snap preview shown while dragging
  SettingsWindow.cs     the sliders
  ToastWindow.cs        ClaudeBar's own notifications
  TrayController.cs     tray icon, drawn at runtime, warning thresholds
  MonitorApi.cs         Win32 monitor enumeration (physical pixels)
  Native.cs             window styles, cursor, GetWindowRect, SetWindowPos, console
  Diagnostics.cs        opt-in tracing, CLAUDEBAR_DIAG=1
  SelfTest.cs           the sandboxed account-switching check
  Models/
    Usage.cs            LimitEntry, UsageSnapshot
    Account.cs          StoredAccount (identity only, never tokens)
  Services/
    CredentialWatcher.cs notices sign-ins from anywhere and saves the account
    UsageService.cs     the /api/oauth/usage call and its parsing
    UsageCache.cs       last good reading, for restarts
    CredentialStore.cs  read-only access to .credentials.json
    AccountStore.cs     DPAPI-encrypted per-account credential copies
    AccountSwitcher.cs  backup / write / verify / roll back
    AppSettings.cs      settings.json next to the exe
    ScreenService.cs    monitor choice, anchors, snapping
```

## Things that bit, and must not be reintroduced

**DPI and multi-monitor.** This desk is 2560×1440 at 125% beside a second display, and three
coordinate spaces are in play: WPF's `Window.Left`/`Top` are device-independent units whose
meaning changes with the monitor under PerMonitorV2; `System.Windows.Forms.Screen` reported a
*different* space again and parked the pill 100px past the primary monitor's edge; Win32
`SetWindowPos` takes physical pixels. ClaudeBar now uses **physical pixels end to end** —
monitors from `EnumDisplayMonitors`/`GetMonitorInfo`, placement through `SetWindowPos`. Do
not reintroduce `Forms.Screen` or `Window.Left` for placement.

**Rows drifting apart.** Two causes, both fixed. The bar was an `ItemsControl` of
`Rectangle`s, and at 125% a 6px rectangle is 7.5 physical pixels, so WPF rounded some
segments to 7 and others to 8 and accumulated fractional offsets shifted one row against the
next — `SegmentBar` now computes segment widths, gaps and x positions in whole physical
pixels. Separately, each row is its own `Grid`, so `Auto` columns were sized **per row** and
drifted; fixed with `Grid.IsSharedSizeScope` plus `SharedSizeGroup` on the bar, percentage
and reset columns. Verified by measuring rendered pixels, not by eye.

**`SizeToContent` and placement.** The pill re-anchors on every size change, or it grows
rightward off its corner when the first reading replaces "starting…".

**Sliders in a ContextMenu do not work.** A WPF `ContextMenu` takes mouse capture for its own
navigation, so a `Slider` inside one never sees a continuous drag — the value only lurches
when the menu lets an event through. They live in `SettingsWindow` instead.

**Opacity has to be visible to be useful.** It originally faded only the background brush, on
the theory that keeping text opaque protected readability. Over a dark desktop that was a
~10-level change between 0% and 100% — mechanically working, visually invisible, and
indistinguishable from broken. It now fades the whole window, floored at 20%.

**Two things must never write the same opacity.** Staleness dims `Rows.Opacity`; the window's
own `Opacity` belongs solely to the slider. Writing the shell's opacity from the render path
made the pill jump back to full brightness at every poll.

**Dragging needs a threshold.** A plain click must not move the pill or flash a ghost, so the
drag only begins once the pointer passes Windows' own drag distance.

**`DragMove` cannot show a preview.** It runs its own blocking modal loop, so nothing else
gets a look in while it is up. Dragging is hand-rolled to keep the ghost updating.

**Shell balloon tips get the wrong title.** `NotifyIcon.ShowBalloonTip` routes through the
Windows toast system, which titles the toast with the app's AppUserModelID when the app has
no registered shell identity — warnings arrived headed `Microsoft.Explorer.Notification...`
plus a hash. `ToastWindow` draws ClaudeBar's own instead.

**Menus must repopulate on open.** Built once, the Account and monitor submenus went stale
the moment anything changed outside the menu — a login in a terminal, a monitor plugged in —
and showed the wrong account as current. Each submenu rebuilds itself on `SubmenuOpened`.

**Do not swallow exceptions silently.** `DispatcherUnhandledException` is still handled so a
bad poll cannot kill the pill, but it logs first. Swallowing it hid a real bug for two rounds.

## Colour, and not relying on it

Built for a red/green colour blind user, so **every state carries three cues** and the
palette contains no red/green pair:

| State | Colour | Glyph | Segments | Number |
| --- | --- | --- | --- | --- |
| normal | teal-green `#34D399` | ● | few filled | shown |
| warning (≥80%) | amber `#FBBF24` | ▲ | ~10/12 filled | shown |
| critical (≥90%) | magenta `#E879F9` | ■ | ~11/12 filled | shown |
| reached (100%) | magenta `#E879F9` | ■ | all filled | shown |

Notifications fire once per tier on the way up: "getting close", "nearly gone", and at 100%
"limit reached", which also says to switch account. The first version said "nearly gone" at
100%, which is not what 100% means.

The bar is **12 countable segments**, not a smooth fill, so the level reads as a quantity
even in greyscale. The tray icon uses fill *height* as well as colour. Toasts carry the same
glyphs. `severity` from the API overrides the local thresholds when it is louder.

## Running it

```bash
cd src/ClaudeBar
dotnet run                                   # debug
CLAUDEBAR_DIAG=1 dotnet run                  # trace to %LOCALAPPDATA%\ClaudeBar\diag.log
```

### Release build

```bash
cd src/ClaudeBar
dotnet publish -c Release -o ../../release
```

Produces a single self-contained `release/ClaudeBar.exe` (~72MB, no runtime needed), which
is what gets attached to a GitHub Release. **`release/` is gitignored** — a 72MB binary does
not belong in the repo, and GitHub Releases is the right home for it.

Run the pill from `release/` rather than `bin/Debug` if it is in your startup, so a
`dotnet clean` cannot break it. `Start with Windows` records the path of whichever build
enabled it, so toggle it from the build you actually want launching.

Right-click the pill (or the tray icon) for: refresh, **which monitor**, **snap position**,
**settings sliders**, **Account** (switch / save / add), start with Windows, hide, open
settings.json, quit. Left-click-drag moves it, showing a dashed ghost of where it will snap.
Left-click the tray icon to show/hide.

Autostart has no entry in `settings.json` on purpose: the `HKCU\...\Run` key is the single
source of truth, and a copy in the settings file could only ever disagree with it. Note it
records the path it was enabled from, so re-point it after publishing a Release build.

### settings.json

Written next to the exe, or `%LOCALAPPDATA%\ClaudeBar\` if that is read-only.

| Key | Default | Meaning |
| --- | --- | --- |
| `MonitorDeviceName` | primary | which monitor to park on, e.g. `\\.\DISPLAY2` |
| `Anchor` | `BottomRight` | edge/corner to snap to, or `Free` |
| `SnapPadding` | 12 | physical-pixel gap from the snapped edges |
| `SnapThreshold` | 64 | how near an edge a drop must land to snap (0 = off) |
| `OffsetX` / `OffsetY` | 12 | inset from bottom-right, used only when `Anchor` is `Free` |
| `PollSeconds` | 60 | clamped 15–3600; backs off on failure, up to 15 min |
| `WarnAt` / `CriticalAt` | 80 / 90 | threshold percentages |
| `Opacity` | 0.92 | whole-pill opacity, floored at 0.2 |
| `ShowAllAccounts` | false | not implemented yet — see "Still to do" |
| `Visible` | true | remembered across restarts |

### When things are missing

The pill never crashes and never shows a stale number as if it were fresh: "Claude Code not
found", "not signed in", "signed out - run /login", "offline", "rate limited - retrying". If
a poll fails while a good reading is on screen, the numbers stay but dim, and the tooltip
says when they were taken.

## Still to do

**Show all accounts' usage at once**, with `ShowAllAccounts` toggling between that and the
active account only. The account store now exists, so this is unblocked; what it needs is
the one place the "never refresh a token" rule gets relaxed. An idle account's access token
expires after ~8h, so reading its usage means refreshing it. That is safe precisely because
the account is idle — Claude Code is not using it concurrently, so there is no race over the
rotating refresh token, and ClaudeBar writes the rotated token straight back to its own slot.
The rule stays absolute for the active account.

## Conventions

- **Accessibility is not optional here.** Colour never carries a state on its own. Any new
  state needs a shape, a count or a number alongside the colour, and the palette must stay
  clear of red/green pairs.
- **Keep a build runnable.** `dotnet run` should always work from a clean clone.
- **This README is the memory.** The project moves between machines, so decisions, findings
  and gotchas belong here or in the code, not in a chat log. Update it in the same commit as
  the change.
- **Commit straight to `master`.** No branches unless there is a reason.

## Reference: what is on this machine

- Claude Code state: `%USERPROFILE%\.claude\` and `%USERPROFILE%\.claude.json`.
- `~/.claude/.credentials.json` — one `claudeAiOauth` object: `accessToken`, `refreshToken`,
  `expiresAt`, `refreshTokenExpiresAt`, `scopes`, `subscriptionType`, `rateLimitTier`.
  **Never log it, never copy it off this machine, never put it in git.**
- `~/.claude.json` — `oauthAccount` block: `accountUuid`, `emailAddress`, `organizationUuid`,
  `displayName`, `organizationName`, `seatTier`, `billingType`, plus rate-limit tiers.
- Transcripts: JSONL per session under `~/.claude/projects/<escaped-path>/`. **Not needed** —
  Spike 1 found a real number, so transcript estimation is off the table.
