# Changelog

## Unreleased

### New
- **Installer** (`SmartHdrManager-v…-setup.exe`, one file for x64 and ARM64 that installs the right version): installs for your Windows user only, no administrator rights – Start menu entry, optional desktop shortcut and *Start with Windows*, uninstall from *Settings › Apps*. Installing a newer version closes the running app first and keeps your settings. The uninstaller removes everything the app registered in Windows and asks whether to delete your settings too. *Check for updates* works in installed copies as before. The zips are still there for anyone who prefers a portable folder.
- Command line: `--exit` exits the running app (as tray icon › Exit).

## 2.6.1 – 2026-10

### Fixed
- **"Never HDR" could not be undone** ([#2](https://github.com/VantaCode420/SmartHdrManager/issues/2)): an excluded game disappeared from the Games tab for good – the only way back was editing config.json. Now:
  - *Settings › General › Never HDR games* lists every excluded game, like the Games tab: turn a game on to give it HDR again, or select it and click **Restore**.
  - Searching in the Games tab also finds excluded games (marked *Never HDR*); turning one on gives it HDR again. Typing "never" lists them all.
  - Turning HDR on for a game always undoes "Never HDR" (before, the exclusion silently won).
- **cs2 and valorant were excluded without being asked:** older versions shipped them as "Never HDR" examples. They are removed once on the first start of 2.6.1 (exclude them again if you want).

## 2.6.0 – 2026-10

### Changed
- **The app only undoes what it did itself.** HDR that you turned on (Win+Alt+B, Windows Settings) or that another program turned on (madVR / MPC, a game's own HDR option, another tool) is no longer switched back to SDR – also HDR that was already on when the app started. If you turn HDR off while a game from your list runs, the app no longer turns it back on a moment later: it waits until that game ends or the next game / video starts. HDR lost through a display reconnect or wake-up is still switched on again. A full-screen game that switched HDR on by itself is still switched back once it closes. *Settings › Switching back to SDR › Keep the desktop in SDR* brings back the old behaviour.
- **Apps tab** (was: Videos tab): every program the app reacts to that is not a game, found automatically, with one mode each – **programs that are not listed are never touched**.
  - Video players: *Auto* (as set in Settings › Videos) / *HDR only* / *Off* (the player handles HDR itself, e.g. madVR). *Default app…* and *More* (start with HDR) as before.
  - Web browsers: *Auto* / *HDR only* / *Off* (Opera and Vivaldi are now recognised too).
  - Creative apps: on = SDR while in front (Photoshop, Lightroom, Premiere Pro, After Effects, DaVinci Resolve; also listed when installed, switched off: Illustrator, Media Encoder, Capture One, Krita). HDR that you had on comes back on afterwards.
  - Recording & streaming: an on/off switch per app – on = no HDR while it runs (replaces "No HDR while OBS is running"; HDR you had on comes back afterwards), off by default. OBS no longer keeps videos in SDR on its own – switch it on when you record.
  - Programs can be added to every group. The lists in config.json are moved over automatically (also names you added by hand).
- **Look for new games** (was: Scan drives automatically) is now a choice: Off / each start / daily / weekly (default) / monthly – and it is also checked while the app runs, not only at startup.
- The minimize button next to *Pause automation* is gone; minimizing the window now hides it straight to the tray (no flash, no "still running" notification).
- Notifications show "Smart HDR Manager" as the sender (was "VantaCode420.SmartHdrManager").
- Shorter, clearer descriptions in all tabs; some settings renamed (e.g. *Wait before switching back*, *HDR for videos*).

### New
- **Check for updates** (Settings › Updates): shows the release notes and installs the new version with one click – the right package for your copy (x64 / ARM64, standalone / small), checked against the release's SHA256SUMS before anything is replaced; settings are kept. Optional weekly check (off by default) – only a notification. No internet connection otherwise.
- **Crash hint:** a game from your list that closes within a few seconds after HDR came on gets a `HINT` in the log; the second time also a notification and a hint in its Options. Most likely cause: NVIDIA RTX HDR for games – turn it off (*NVIDIA App › Graphics › Global Settings › RTX HDR*), or use *Launch with HDR* (HDR on before the game starts). The hint disappears once the game runs normally.
- HDR changes by someone else are recognised sooner (right after the app's own switch has been confirmed, instead of after 8 s).
- Programs added by hand in the Apps tab have a *Remove* button.

## 2.5.2 – 2026-10

### Fixed
- **Games with a launcher did not switch HDR** ([#1](https://github.com/VantaCode420/SmartHdrManager/issues/1), The Witcher 3 GOG): GOG and Epic register the exe their launcher starts – for some games that is a launcher such as REDprelauncher.exe, not the game. The app then watched the launcher, which closes after a moment, and never saw the real game. Now a registered launcher is skipped and the game folder is searched for the real game exe.
- **HDR stayed on after "Exit"**: the app now switches back to SDR when you exit it, if it turned HDR on. HDR that was already on before, was switched by something else, or belongs to a game that is still running is left alone.
- **Log deleted at startup:** a log file over 1 MB was deleted when the app started – exactly the information needed after a crash. It now moves to `log.old.txt` instead.
- **Win+Alt+I with a browser in front** excluded the browser for good (no more YouTube HDR or browser videos). With a browser or video player in front it now means *SDR for this video*.

### New
- **Launcher safety net:** if a game in your list closes within 15 s, HDR stays on while the app waits up to 45 s for a program from the same folder (the real game). It follows it and remembers it (`LauncherGames` in config.json), so next time HDR switches as soon as the game starts. Also works for launchers nobody knows about yet. Only process names are compared – anti-cheat safe.
- **SDR for this video** (tray menu, shown while a video uses HDR): back to SDR now; this video stays in SDR until it stops or the next video starts (new title in the tab / player). *HDR for videos again* in the same menu ends it early.
- **Back to SDR after the video stops** can be set up to **5 minutes** (was 30 s): 0–3, 5, 10, 15, 20, 30 s, 1, 2, 3, 5 min.
- **Browser script 3.1:** a paused video keeps its marker for up to 10 minutes while its tab is visible, so pausing no longer switches HDR off and on again (3.0 kept it 60 s and only for HDR videos). Optional update – 3.0 keeps working.
- Several exes per game: games with separate exes (e.g. DX11 / DX12) are watched with all of them. The Games tab shows "witcher3.exe +1".
- GitHub issue form (asks for version, Windows, GPU, monitors and the log).

### Improved
- When a rescan finds different exes for a game, its HDR setting and game options move to the new exes (no game suddenly "off", no stray launcher entry). All library games are detected once more after the update.
- Log: "Display control" now shows the real Windows version and build (e.g. "Windows 11 25H2 (build 26200.9711)") instead of "24H2+".
- README: FAQ (Why not leave HDR on? HDR switches too often for videos, AMD / Intel), launcher section, issue link.

## 2.5.1 – 2026-10

### New
- **Videos tab** – start video players with HDR already on, so NVIDIA RTX Video HDR converts SDR videos from the first frame:
  - finds installed players automatically (MPC-HC, MPC-BE, VLC, PotPlayer, mpv.net, Kodi) or add any other,
  - **Default app…**: registers "<Player> (with HDR)" for video files – set it as default once, then every double-click on a video plays it with HDR,
  - desktop shortcut "<Player> (HDR)" (also takes drag & drop) and a right-click entry "Play with HDR",
  - HDR switches back off about a second after the player window is closed.
  - Tested with RTX Video HDR: MPC-HC (recommended), VLC, PotPlayer – the player settings are in the README.
- **Launch with HDR** for games: HDR on first, then the game starts – for games that hide their HDR option (or where Windows Auto HDR does not start) when HDR was off at launch. Steam launch option (one-click copy) or desktop shortcut, in the game's Options. Follows launcher stubs (Ubisoft, Rockstar, …) to the real game.
- **No HDR while OBS is running** (Settings): keeps games and videos in SDR while OBS is open, and switches back to SDR when OBS starts – no more blown-out recordings. Manual override still works.
- **More game libraries:** Ubisoft Connect, EA app and Rockstar Games Launcher (no duplicates with Steam).
- **Games that switch HDR themselves** are no longer fought: when a full-screen game turns HDR on by itself, the app keeps HDR on until the game closes.
- **Log:** a line whenever Windows HDR changes without the app (game, Win+Alt+B, Windows Settings), with the window in front; a `HINT` when a game runs on a monitor that is not selected for switching.
- **Windows on ARM** builds (experimental, untested).

### Improved
- **Settings page reorganised:** General · Monitors · Switching back to SDR · YouTube in browser · Videos / RTX Video HDR · Display fixes · Advanced – each with a short description. Video detail options only show while video switching is on; greyed-out switches say why.
- **Browser script status** in Settings ("Detected ✓" / "Not detected yet") with a link to the installation guide.
- **Reset to defaults** (Settings › Advanced) – keeps your games and per-game options.
- Maximized window on large screens: content stays in a centred column – easier to read, smoother scrolling.
- "Start with Windows" and the Videos tab entries repair themselves when the app is moved or updated into another folder.
- The log file stays small (rotates to `log.old.txt` at 2 MB).
- Own taskbar identity: the app keeps its own icon even when a "(HDR)" shortcut with a game icon exists.

### Fixed
- Elden Ring, Alan Wake 2 and Cyberpunk 2077 showed up as HDR games although not installed (leftover examples) – removed for new and existing users (kept if the game really is installed).
- Premiere Pro never switched to SDR (wrong process name); After Effects added.
- mpv.net, MPC-BE and Kodi added to the video players.
- The coloured status card could stay coloured although HDR was off (when HDR turned off again within 1.5 s).
- "&" missing in Settings headings; video option rows had a smaller height on scaled displays.
- Video players: RTX Video HDR now also gets the "display changed" nudge after HDR comes on.

## 2.5.0 – 2026-10-15

First public release: automatic HDR for games (Steam, Epic, GOG, Xbox), YouTube HDR via a Tampermonkey script, local HDR videos, optional HDR for all videos for NVIDIA RTX Video HDR, per-monitor switching, manual override, keyboard shortcuts and command line.
