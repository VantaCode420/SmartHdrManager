# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Smart HDR Manager: a Windows-only WinForms tray app (.NET 10, `net10.0-windows`, x64 / ARM64) that turns Windows HDR on when a listed game, an HDR video (YouTube via a Tampermonkey userscript, local players) is running, and back to SDR afterwards. User-facing behaviour is documented in `README.md` (read its "How it works" and "Apps tab" sections before changing detection logic) and `CHANGELOG.md`.

## Commands

There are no tests and no linter configured; verification is building and running the app.

```powershell
dotnet build                      # debug build
dotnet run                        # run the tray app (single instance: a second launch only signals the running one)
dotnet run -- --tray              # start hidden in the tray; other CLI flags: --force-hdr --force-sdr --auto --toggle-pause --launch <exe> --launch-video <player> <file>
dotnet build -c Release           # release-configuration build (a quick "does it compile" check)

powershell -ExecutionPolicy Bypass -File .\release.ps1 -Arch x64    # full release packaging (see below)
```

- Close a running instance (tray icon › Exit) before rebuilding, or the exe in `bin\` is locked.
- `release.ps1` **deletes everything in its output folder first** (default `S:\Smart HDR Manager`, must be outside the repo), builds small + standalone single-file packages per arch, zips them, writes SHA256SUMS. Only a whitelist of files is copied. `Updater.cs` depends on the zip names / `SHA256SUMS.txt` format and on the `BuildFlavor` assembly metadata from the csproj, so don't rename artifacts casually.
- The version lives only in `SmartHdrManager.csproj` (`<Version>`); `release.ps1` and `Updater.CurrentVersionText` read it from there.

## Repository gotchas

- Build output (`bin/`, `obj/`, `publish/`) is gitignored and can contain stale copies of the code; restrict Grep/Glob to the `*.cs` files in the repo root.
- Source files are flat in the repo root, single namespace `SmartHdrManager`; `ImplicitUsings` is disabled and `Nullable` is enabled.
- Each feature change is expected to update `CHANGELOG.md` (and `README.md` if user-visible), since both ship inside the release zips.

## Architecture

The app is a background decision engine plus a lazily created settings window:

- **`Program.cs`** – entry point, single-instance mutex + named events (second launch passes CLI commands to the running instance via `CommandEvents`), and `TrayAppContext`, which owns the engine, tray, hotkeys and the `MainForm`. There is no `ApplicationContext.MainForm`: closing the window only hides it (disposed after ~5 s to save memory); only Exit ends the process.
- **`Engine.cs` – `HdrEngine`** (the core, ~3.5k lines). Runs on its **own background thread** and is the only thing that switches the display. Each tick builds on an immutable `Snapshot` of config-derived lookup sets (rebuilt only when config changes). `Tick` → `Decide` → `EvaluateHdrRequirement` / `EvaluateVideo` decide whether HDR is needed; the rest handles overrides, per-game delays/hold times, launcher-following, crash hints, and "only undo what we turned on ourselves" bookkeeping (2.6 behaviour, `KeepDesktopSdr` restores the old one). Cadence: tick ~100 ms, process scan 1 s, HDR state re-read 2 s (or right after a switch / display change / wake).
- **Threading contract:** the UI mutates `engine.Config` only under `engine.Sync` and otherwise hands work to the engine with `Post(Action)`; the UI reads `engine.Status` (an immutable `EngineStatus` snapshot). Keep that split when adding settings or UI features.
- **`Core.cs`** – `AppConfig` (config.json schema, with `ConfigVersion` for one-time migrations of default lists), `HdrController` (Windows display-config P/Invoke: HDR state/switch, colour-state restore after SDR), `WindowDetector` (foreground window/fullscreen/process info), `GameScanner`, monitor filtering.
- **Detection of games:** `Library.cs` (`GameLibraryScanner`: Steam/Epic/GOG/Xbox/Ubisoft/EA/Rockstar/Battle.net/Amazon/itch/Riot/Heroic, Game Bar suggestions), `SteamAppInfo.cs` (binary `appinfo.vdf` parser to find the exe Steam really launches), `Sqlite.cs` (read-only access via Windows' built-in sqlite for the Amazon/itch DBs). A library game maps to several process names and install folders; a same-named process running outside the game's folder is ignored.
- **Detection of video:** `VideoPlayback.cs` (`VideoPlaybackMonitor`, audio-activity detection, RTX Video HDR status read from the NVIDIA app), `VideoFileInfo.cs` (reads MKV/WebM/MP4/MOV headers for PQ/HLG/Dolby Vision), `VideoPlayers.cs` (installed players, "(with HDR)" registry entries / shortcuts / context menu). Browsers are detected by a marker the userscript `SmartHdrManager_YouTube.user.js` appends to the tab title (`[real_hdr_active]` / `[sdr_video_active]`) — the app only does a *contains* check on window titles.
- **`AppCatalog.cs`** – the Apps tab model: installed-app discovery and one `AppRule` (category + mode) per program. Programs not listed are never touched.
- **`Launcher.cs`** – "Launch with HDR" (`--launch`, `--launch-video`): HDR on first, wait until confirmed, then start the target given on the command line, hold HDR until it exits. The path comes only from the shortcut/launch option itself, never from IPC.
- **`Updater.cs`** – GitHub release check, SHA256-verified self-update (also owns `UpdateDialog`).
- **UI:** `MainForm.cs` (tabs: Games / Apps / Settings / Log), `ModernControls.cs` (custom-drawn controls, `GameList`, `FrameLoop`), `Theme.cs` (colours, generated icons), `Tray.cs` (tray icon/menu, autostart registry entry, `GlobalHotkeys`).

## Constraints to keep in mind

- Anti-cheat safety is a stated product promise: only read process names/paths/window titles (and player command lines); never touch game memory or inject.
- No network access except the explicit update check (api.github.com / github.com); no telemetry.
- Persistent state: `%AppData%\SmartHdrManager\config.json` (or next to the exe with `portable.txt`), log in `%LocalAppData%\SmartHdrManager\log.txt`. Every engine decision is logged with a category tag (INFO, HINT, MANUAL, UPDATE, COLOR, …); the README's troubleshooting relies on those log lines, so keep them informative when changing behaviour.
- Windows-only P/Invoke code assumes a 64-bit process (`GetWindowLongPtr`); the csproj sets `PlatformTarget` accordingly.
