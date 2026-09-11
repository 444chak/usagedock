# AGENTS.md

Context for an AI agent working on UsageDock with no prior knowledge of it. Read this before
touching anything. `README.md` explains what the app does for a user; this file explains how to
work on it safely.

## What this is

A native Windows 11 dock, C# / .NET 10 / WPF, that shows Claude and Codex quota usage in a small
always on top strip on the right edge of the screen. Windows only. No WebView, no Electron, no
Tauri, no browser engine of any kind. Do not add one.

## Build and test

**The SDK may not be on PATH.** On the original development machine `dotnet` resolves to
`C:\Program Files\dotnet\dotnet.exe`, which carries the runtimes but no SDK, and `dotnet build`
fails with "No .NET SDKs were found". The SDK lives under `%LOCALAPPDATA%\Microsoft\dotnet`. If
that is your situation:

```bash
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
```

Then:

```bash
dotnet build -c Release     # must finish with 0 errors and 0 warnings
dotnet test                 # must be all green
```

Tests redirect everything the app writes by setting `USAGEDOCK_DATA_DIR` from a module
initializer, so a run never touches the log or cache of a real installation. Keep that: the
concurrency tests provoke write failures on purpose, and those errors landing in a user log look
like real faults.

**The build fails while the app is running.** MSBuild cannot overwrite `UsageDock.exe` while a
running instance holds it, and the error is a confusing MSB3027 about `apphost.exe`. Stop it
first:

```bash
powershell -NoProfile -Command "Stop-Process -Name UsageDock -Force -ErrorAction SilentlyContinue"
```

## Layout

```
src/UsageDock/
  App.xaml.cs        entry point, single instance mutex, wiring
  MainWindow.xaml.cs the dock window: placement, hit testing, hover
  Models/            QuotaWindow, UsageSnapshot, AppConfig, ActivityState
  Providers/         reading quotas from Claude and Codex
  Services/          config, cache, logging, backoff, hooks, polling, file watching
  Native/            Win32 interop, monitor enumeration, rounded rect hit testing
  Tray/              notification area icon and menu
  Ui/                usage ring, provider cell, hover card, provider marks
tests/UsageDock.Tests/
```

## Invariants

These are the reasons the app is built the way it is. Breaking one is a regression even if
everything still compiles and the tests pass.

**1. No invisible surface may ever block a click.** This is the single most important property.

- The dock window is exactly the size of the visible pill and nothing more.
- `AllowsTransparency` is **off**. Do not turn it on to get a soft shadow or a glow. Windows 11
  rounds the corners through DWM instead, and the four unpainted corner pixels are reported as
  `HTTRANSPARENT` from `WM_NCHITTEST` in `MainWindow.HitTestPoint`.
- The hover card is a **separate window**, sized to its own content, carrying `WS_EX_TRANSPARENT`
  so every hit test falls straight through it. It is parked off screen until positioned; a WPF
  window with no explicit size gets a large default and will flash mid screen.
- If you change the dock geometry, re-verify the corners. See "Verifying window behaviour".

**2. Never leak a credential.** Credential files are opened read only and never modified.

- No token value is ever logged, cached, written to `%APPDATA%\UsageDock\`, or put into an error
  message.
- `CodexAuthReader` deliberately checks only for the *presence* of a token. Do not make it return
  one.
- `ClaudeLocalUsageReader` deliberately ignores the `org` field, which identifies the user.
- Do not attempt to decrypt the Claude Desktop Electron store, the Windows Credential Manager, or
  any other protected credential store. If a token is not plainly available, fall back to a local
  source or report the situation honestly.

**3. Never steal focus.** `WS_EX_NOACTIVATE`, plus `WM_MOUSEACTIVATE` returning `MA_NOACTIVATE`.
Clicking the dock must not pull focus away from whatever the user is typing in.

**4. Positioning is per monitor, in physical pixels, through `SetWindowPos`.** Never use WPF
`Left`/`Top` for the dock or the card. The app is `PerMonitorV2`, and mixed DPI setups break the
moment logical coordinates creep in.

**5. Hooks must stay trivially cheap.** `HookEntryPoint` runs before WPF is constructed and must
stay that way, because Claude Code invokes it on every hook event. It currently costs 5 to 15 ms.
It must never throw, because that would fail the Claude Code turn it is attached to.

**6. Hook installation is surgical.** `HookInstaller` only ever adds or removes entries carrying
the `--usagedock-hook` marker. Users have hooks from other tools in the same `settings.json` and
they must survive. The file is backed up before the first write.

## The MSIX container, read this before verifying anything

Claude Desktop from the Store is an MSIX package (`Claude_<publisherhash>`). Claude Code runs
inside it, so **if you are an agent working in Claude Code, your shell is inside that container
too**, and so is every process you start. Check with the parent chain: `pwsh.exe <- claude.exe`.

Inside the container, the three AppData folders (`Roaming`, `Local`, `LocalLow`) are
**virtualised**: reads are merged with, and writes redirected to,
`%LOCALAPPDATA%\Packages\Claude_*\LocalCache\`. The profile root (`.claude`, `.codex`,
`.usagedock`) and `%LOCALAPPDATA%\Programs` are not virtualised.

What this did to this project, over three reboots:

- Every "verification" run from the agent shell launched the dock *inside* the container, where
  `%APPDATA%\Claude` appears to exist. Windows launches it *outside* at login, where it does not.
  The bug only ever reproduced on the user's reboot, never in the agent's tests.
- Reading `%APPDATA%\UsageDock\logs` from the shell showed the container's copy of the log when
  one existed, not the real one, so the evidence itself was misleading.
- The hooks run inside the container and wrote the activity file into the virtualised copy;
  the real dock read the real folder and stayed Idle forever.

Rules that follow:

- **To launch the dock the way Windows does, go through explorer**:
  `Start-Process explorer.exe -ArgumentList '"<path>\UsageDock.exe"'`. Confirm with the parent
  process, which must be `explorer.exe`.
- Anything a hook writes and the dock reads must live under the profile root, never AppData.
- If a path "obviously exists" but a Windows-launched process says it does not, you are looking
  at the virtualised view. Check `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\`.

## Traps that already cost time

- **`InvariantGlobalization` must stay `false`.** WPF resolves `XmlLanguage` through
  `CultureInfo`, `en-us` cannot be created in invariant mode, and XAML parsing then throws at
  runtime with no build error. Symptom: a window silently never appears.
- **`UseWindowsForms` replaces the implicit using set with the WinForms one.** That is why
  `UsageDock.csproj` removes `System.Drawing` and `System.Windows.Forms` globally and restores the
  usual base set. Without it, `Brush`, `Color`, `Point`, `Application` and `Timer` are all
  ambiguous. WinForms is used **only** for the tray icon.
- **`App.xaml` is a `Page`, not an `ApplicationDefinition`**, so the explicit `Main` in
  `App.xaml.cs` is the entry point and hook invocations can short circuit before WPF loads.
  Reverting that gives either "multiple entry points" or a slow hook.
- **Atomic writes need a unique temporary name per call.** Concurrent hook processes and threads
  collide otherwise. See `JsonIo.TryWriteFile`.
- **`WindowFromPoint` does not honour `HTTRANSPARENT`.** It reports the dock even where the window
  correctly reports itself transparent. To verify hit testing, send `WM_NCHITTEST` directly.
- **Never trust `Environment.GetFolderPath`.** It returns an empty string when the shell cannot
  resolve a folder, which happens to a process launched early at Windows login. `Path.Combine`
  then produces a *relative* path, so the app reads and writes next to its working directory,
  which for an autostarted process it cannot write to. Because `Log` swallows its own errors, the
  symptom is an app that runs for hours without a single log line, with one provider working
  (Codex, under `UserProfile`) and everything under `ApplicationData` silently failing. This cost
  three reboots and two wrong hypotheses to find. `AppPaths` now falls back through the
  environment variables and rejects anything not fully qualified; keep it that way, and keep the
  startup line that prints the resolved roots.

## Where the data comes from

Both providers prefer local files over the network. Nothing is sent anywhere.

**Codex** reads `%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl`. The CLI records the rate
limits the server reported into each session rollout as `token_count` events. Only the tail of the
most recent few files is scanned, backwards, until a `rate_limits` record turns up, because these
files get large. `auth.json` is probed only for whether a session exists.

**Claude** has two sources, tried in this order:

1. `%USERPROFILE%\.claude\.credentials.json`, the OAuth token of the standalone CLI, used against
   the usage endpoint. Live, with exact reset times. **This path has never been exercised against
   the real API**, because the development machine runs Claude Desktop, which leaves those token
   fields empty. Treat it as unverified.
2. `plan-usage-history.json`, written by Claude Desktop every fifteen minutes. Classic install:
   `%APPDATA%\Claude\`. Store (MSIX) install: `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\`.
   `AppPaths.ClaudeDesktopUsageFile` checks both on every access and the newest wins. Shape:
   `{ "t": <epoch ms>, "org": "...", "u": { "fh": <5h percent>, "sd": <7d percent> } }`. No token,
   no network. This is the source that actually runs day to day.

The history carries no reset timestamp. `ClaudeLocalUsageReader.InferReset` derives one: usage
only grows inside a window and drops when one rolls, so the start of the current run of non zero
samples locates the window. When it cannot be determined, report nothing rather than a guess.

Fifteen minutes is a hard ceiling on Claude freshness. `UsageFileWatcher` removes the additional
polling lag but cannot beat the sampling rate. Do not add faster polling hoping to improve it.

## Activity states

Claude: Claude Code hooks write `%APPDATA%\UsageDock\activity\claude.json`. A stale Working or
Waiting record decays to Idle after five minutes, because a hook can be missed when a terminal is
killed.

Codex: no hook system, so the freshness of its session log is the signal. Both go through
`ActivityService`, so a better Codex signal can be dropped in without touching the UI.

## Conventions

- Tests are xUnit, named as sentences, for example `A_stale_working_record_decays_to_idle`.
- Every parser must degrade rather than throw: malformed input yields fewer windows or null, never
  an exception. There are tests for this on each parser. Keep them.
- Comments explain **why**, not what. Do not narrate the code.
- Prefer drawing in `OnRender` over composing shapes for anything in the dock. The window is on
  screen permanently, so visual tree size and layout churn matter.
- No new NuGet dependencies without a real reason. The app currently has none outside the SDK.

## Verifying window behaviour

Compiling is not evidence. Run the app and check on screen. Worth re-verifying after any change to
window geometry, window styles, or the hover card:

- Send `WM_NCHITTEST` to the dock at the four corner pixels. Each must answer `HTTRANSPARENT`
  (`-1`). The interior must answer `HTCLIENT` (`1`).
- Enumerate the visible windows owned by the process. With the card hidden there must be exactly
  **one**, and it must be the size of the pill.
- Record the foreground window, click the dock, record it again. It must be unchanged.
- Check `%APPDATA%\UsageDock\logs\` afterwards. `App` handles dispatcher exceptions and keeps
  running, so a broken binding or a XAML fault shows up only in the log.
- Every poll logs its outcome per provider, but only when that outcome changes, so a steady state
  stays quiet while a provider that starts failing becomes visible. Do not remove it: an instance
  once ran for twenty three minutes showing stale numbers with a log that said nothing at all, and
  it could not be diagnosed afterwards.
