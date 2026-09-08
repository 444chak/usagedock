# UsageDock

A small native Windows 11 dock that shows Claude Code and Codex quota usage on the right edge of
the screen.

- C# / .NET 10 / WPF. No WebView, no Electron, no Tauri.
- Always on top, never steals focus, never appears in the taskbar or Alt+Tab.
- Sized to exactly the visible UI, so it cannot block clicks anywhere else on screen.

![the dock](docs/dock.png)

## What it shows

The dock is a vertical pill with one cell per provider. Each cell has two concentric rings around
the provider mark: the **outer ring is the rolling 5 hour window**, the **inner ring is the weekly
window**. Below sits the 5 hour percentage, and an activity dot rides the top right corner.

Hovering a cell opens a detail card to its left with the plan, the activity state, every quota
window with a progress bar and reset countdown, used and left percentages, and how old the
reading is. The card disappears when the pointer leaves the dock.

Ring colour follows usage: green under 70%, amber from 70%, red from 90%, applied to each ring
independently. The provider mark keeps its brand colour rather than the usage colour, so it reads
as an identity while the rings carry the meaning. The activity dot is blue for Working, amber for
Waiting for user, and grey for Idle.

Both marks are vector paths drawn at render time, so there are no image assets to ship and they
stay crisp at any DPI.

## Requirements

- Windows 10 1607 or later; rounded corners need Windows 11
- .NET 10 Desktop Runtime to run, .NET 10 SDK to build

## Build

```bash
dotnet build -c Release
```

The executable lands at:

```
src\UsageDock\bin\Release\net10.0-windows\win-x64\UsageDock.exe
```

Run the tests with:

```bash
dotnet test
```

Stop the app before building. MSBuild cannot overwrite a running `UsageDock.exe`, and the failure
reads as a confusing MSB3027 about `apphost.exe`.

## Install

Grab a build from [Releases](https://github.com/444chak/usagedock/releases), or build it yourself
with the steps above.

| File | Size | Needs |
| --- | --- | --- |
| `UsageDock-<version>-win-x64.exe` | ~140 MB | nothing, just run it |
| `UsageDock-<version>-win-x64-framework-dependent.exe` | ~0.3 MB | .NET 10 Desktop Runtime |

Both are the same app and use about the same memory, around 60 MB; the large one carries the .NET
runtime inside it. The standalone build is deliberately **not** compressed: compression would
halve the download to about 63 MB, but assemblies would then be decompressed into memory instead
of mapped from the bundle, which pushes resident memory from 59 MB to 136 MB. For something that
sits on screen all day that is the wrong trade.

**Do not run it from the build folder, or from Downloads.** The Claude Code hooks record an absolute path to the
executable, so a clean build deletes the target from under Claude Code, and Windows then shows its
"install .NET" dialog on every hook event. Copy the app somewhere permanent first:

```bash
dotnet build -c Release
mkdir -p "$LOCALAPPDATA/Programs/UsageDock"
cp -r src/UsageDock/bin/Release/net10.0-windows/win-x64/* "$LOCALAPPDATA/Programs/UsageDock/"
"$LOCALAPPDATA/Programs/UsageDock/UsageDock.exe"
```

Then from the tray icon: **Start with Windows**, and **Install Claude Code hooks** if you want the
Working / Waiting / Idle indicator on Claude. Restart Claude Code afterwards so it picks the hooks
up.

If you later move or rebuild the app, start the new copy once. It notices that the installed hooks
point somewhere else and rewrites its own entries to the current path, leaving hooks belonging to
other tools alone. Note that this happens for **any** copy you launch, so running a freshly
downloaded build from your Downloads folder will quietly claim the hooks; start your real copy
again afterwards to hand them back.

To uninstall: tray menu, **Uninstall Claude Code hooks**, untick **Start with Windows**, quit, then
delete the program folder and `%APPDATA%\UsageDock\`.

### Self-contained build

For a copy that needs no .NET runtime installed at all (about 148 MB, a single file):

```bash
dotnet publish src/UsageDock/UsageDock.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

Note that this also switches the ordinary `bin/Release` output to a self-contained layout, so run a
clean `dotnet build -c Release` afterwards if you want the framework-dependent one back.

## Tray menu

Refresh usage now, reset position, start with Windows, install and uninstall the Claude Code
hooks, open the data folder, quit.

## Where the data lives

Everything is under `%APPDATA%\UsageDock\`:

| File | Contents |
| --- | --- |
| `config.json` | monitor, vertical anchor, edge margin, poll interval |
| `cache.json` | last known good quota reading per provider |
| `activity\claude.json` | current Working / Waiting / Idle state written by the hooks |
| `logs\` | daily log, seven days kept |

No secret is ever written here.

## How each provider is read

### Claude

Claude Code and Claude Desktop keep their sessions in different places, so there are two sources
and the app takes whichever is available.

**Source 1, the usage endpoint.** The standalone Claude Code CLI stores an OAuth token in
`%USERPROFILE%\.claude\.credentials.json`. When one is there it is read, strictly read only, and
used as a bearer token for one request. This source is preferred because it is live and carries
exact reset times. The file is never modified, and the token is never logged, never cached and
never written anywhere.

401 and 403 are reported as a credentials problem rather than retried. 429 honours `Retry-After`.
Anything else falls back to capped exponential backoff, 30s doubling to a 30 minute ceiling.

**Source 2, the local usage history.** Claude Desktop keeps its session in an encrypted Electron
store, not in `.credentials.json`, which on those machines is present but holds empty token
strings. There is therefore no bearer token to use, and decrypting another application's
protected credential store is not something this app does.

It does not need to. Claude Desktop writes `%APPDATA%\Claude\plan-usage-history.json`, a series
of samples roughly every fifteen minutes:

```json
{ "t": 1788899116963, "org": "...", "u": { "fh": 52, "sd": 14 } }
```

`fh` is the five hour window and `sd` the seven day window, both percentages. Reading this needs
no token and no network, exactly like the Codex path. The `org` identifier is deliberately never
read.

The file carries no reset timestamp, but usage only grows inside a window and returns to zero
when one rolls, so the start of the current run of non zero samples locates the window and the
reset is one window length later. The true boundary sits between two samples, so the midpoint is
used and the estimate is good to about half a sampling interval. When the run begins before the
retained history, or the estimate has already passed, the card says `unknown` rather than showing
a made up value.

**How fresh the numbers are.** Claude Desktop writes a sample every fifteen minutes exactly, so
that cadence is the ceiling on freshness and no amount of polling beats it. The app watches the
file and refreshes the moment a sample lands, rather than waiting for the next poll, and the card
always states the age of the reading. The usage endpoint, when a token is available, is live and
has no such limit.

When a refresh fails the last cached reading stays on screen, annotated with the reason. If
neither source yields anything the dock shows `--`.

### Codex

Codex quotas are read entirely from local files, so no token leaves the machine and there is no
request to be throttled.

The Codex CLI records the rate limits the server reported into each session rollout under
`%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl`. UsageDock scans the tail of the most recent
few of those, backwards, for the newest `rate_limits` record, which carries both the short 5 hour
window and the weekly window.

`%USERPROFILE%\.codex\auth.json` is opened read only and probed only for whether a session exists
and which auth mode is in use. No token value is read out of it.

Because the numbers come from the last Codex turn, the card reports the age of that turn rather
than the age of the poll.

## Activity detection

Claude Code activity comes from hooks. Install them from the tray menu; they map onto states like
this:

| Hook event | State |
| --- | --- |
| `SessionStart`, `Stop`, `SessionEnd` | Idle |
| `UserPromptSubmit`, `PreToolUse`, `PostToolUse` | Working |
| `Notification` | Waiting for user |

Each hook runs `UsageDock.exe --usagedock-hook <state>`, which writes a small state file and
exits before WPF is loaded; measured at 5 to 15 ms, so it adds nothing meaningful to a turn, and
it can never fail the turn it is attached to. A hook can be missed when a terminal is killed, so
a stale Working or Waiting record decays back to Idle after five minutes.

The installer edits `settings.json` surgically: only entries carrying the `--usagedock-hook`
marker are added or removed, so hooks belonging to other tools survive, and the original file is
backed up to `settings.json.usagedock-bak` before the first write.

Codex has no hook system. Its activity is inferred from how recently the CLI wrote to its session
log, which is a coarse but honest signal; both providers go through one interface, so a better
Codex signal can replace it without touching the UI.

## Why the window cannot block clicks

This is the constraint the whole window design is built around.

- The dock window is exactly the size of the visible pill, 58 x 156 logical pixels. There is no
  oversized transparent surface anywhere.
- `AllowsTransparency` is off. The window is opaque, and Windows 11 rounds the corners through
  DWM rather than through a transparent layer.
- The only unpainted pixels are the four rounded corners, and `WM_NCHITTEST` returns
  `HTTRANSPARENT` for them, so clicks there land on whatever is underneath.
- The hover card is a separate window, sized to its own content, and carries `WS_EX_TRANSPARENT`,
  so it is purely informational and every hit test falls straight through it.
- `WS_EX_NOACTIVATE` plus `WM_MOUSEACTIVATE` returning `MA_NOACTIVATE` mean clicking the dock
  cannot pull focus away from whatever the user is typing in.
- `WS_EX_TOOLWINDOW` keeps it out of the taskbar and out of Alt+Tab.

Verified on a 1920x1200 display at 125% scale: the window measures 73x195 physical, the four
corner pixels answer `HTTRANSPARENT`, the interior answers `HTCLIENT`, the foreground window is
unchanged by a click on the dock, and only one window is visible when the card is hidden.

## Multi-monitor and DPI

The app is `PerMonitorV2` aware. Position and size are computed per monitor in physical pixels
and applied with `SetWindowPos`, never through WPF `Left`/`Top`, so a secondary display at a
different scale lands exactly on its own right edge. `WM_DPICHANGED`, `WM_DISPLAYCHANGE` and
`WM_SETTINGCHANGE` all trigger a reposition, which covers moving between displays, unplugging a
monitor, and the taskbar changing the work area.

Set `monitorDeviceName` in `config.json` to pin the dock to a display, for example
`\.\DISPLAY2`. If that display is gone the dock falls back to the primary one.

## Layout

```
UsageDock.sln
src/UsageDock/
  App.xaml, MainWindow.xaml     the dock window
  Models/                       quota windows, snapshots, config
  Providers/                    Claude and Codex readers and parsers
  Services/                     config, cache, logging, backoff, hooks, polling
  Native/                       Win32 interop, monitors, hit testing
  Tray/                         notification area icon and menu
  Ui/                           usage ring, provider cell, hover card
tests/UsageDock.Tests/
```
