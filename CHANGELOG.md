# Changelog

## 3.0.0 – 2026-11

### New
- **Always-HDR mode (experimental, Windows 11).** *Settings › Always-HDR*, right below *General*. Windows HDR stays on, and the app fixes how Windows shows SDR content in HDR: Windows decodes SDR with the sRGB curve while nearly all SDR content is made for gamma 2.2, so shadows look washed out. The app makes a copy of your Windows HDR Calibration profile with a gamma 2.2 correction for your current SDR brightness, and switches each display between that copy (desktop, browser, SDR games) and your unchanged calibration (HDR content). A switch takes about a second, with no black screen.
  - **Correction off for HDR content:** everything that switches HDR on in the normal mode turns the correction off instead – games switched on in the Games tab, RenoDX games, HDR videos, SDR videos with RTX Video HDR, the tray's *HDR on* and *Launch with HDR*. Games see HDR on from their first frame.
  - **The app can't see whether a game outputs HDR.** Switch your HDR games on in the Games tab; an HDR game that isn't switched on keeps the correction, and its shadows look too dark.
  - **Real SDR when needed:** creative apps set to SDR (while in front), recording apps set to *No HDR* and the tray's *SDR* really turn HDR off, as in the normal mode. HDR comes back with the correction when they are done. The window says which app needs SDR.
  - **Follows the SDR brightness slider** (*Windows Settings › Display › HDR*): a matching profile is made a moment after you stop moving it. After standby the profile is set again, because Windows can drop it.
  - **Pause** leaves the correction on and stops switching.
  - **Exit:** your calibration profile goes back, and HDR goes back to how it was when the app started – or stays on with *Keep HDR on when the app exits*. **Remove Always-HDR profiles** turns the mode off and removes everything it created; turning the mode off does the same.
  - **Window and tray:** the status card shows *Always-HDR · SDR corrected*, *HDR content · no correction* or *SDR for 'app'*, with the HDR colours and animation whenever HDR is really on; the tray icon follows the real HDR state.
  - **Settings:** *Keep the desktop in SDR* is greyed out in this mode (it doesn't apply), and *Keep HDR on when the app exits* only while the mode is on.
  - **Needs a Windows HDR Calibration profile.** If none of your HDR displays has one, the mode isn't turned on: the app explains how to make one with the free *Windows HDR Calibration* app (run it once with HDR on) and can open it in the Microsoft Store. No administrator rights. The app only creates, changes or removes colour profiles named `SHM_*`; your calibration profile is never changed. Off by default – with it off, the app works exactly as in 2.7.
  - **Anti-cheat:** the app still never reads or changes game memory and injects nothing. Always-HDR only switches Windows colour profiles for the display, the same way the Windows HDR Calibration app and other colour tools do. It hasn't been tested with every anti-cheat system, so if you play games with kernel-level anti-cheat, be careful, or use the normal mode for them.
- **Colour diagnostics for bug reports:** `SmartHdrManager.exe --hdr-lab dump` writes `always-hdr-diagnostics.txt` (displays, colour profiles, the calibration profile's contents) to `%LocalAppData%\SmartHdrManager`. Always-HDR logs every profile change to `always-hdr-log.txt` in the same folder.

## 2.7.0 – 2026-11

### New
- **RenoDX Games.** A new switch at the top of the Games tab turns HDR on automatically for every game with the RenoDX add-on (`renodx-*.addon64` / `.addon32`) next to its exe, in its game folder or in the Unreal folders `Binaries\Win64` / `WinGDK`. Games found later are covered after the next scan, and unchecking the switch turns those games off again. Games on *Never HDR* are skipped, and a game whose own switch you set keeps your choice. If the switch conflicts with those choices, a list of the games appears and you decide for each one whether to keep your choice or override it. When every RenoDX game has been turned off by hand, the switch turns itself off too. Off by default. Games with the add-on show "RenoDX" under their name, and searching for "renodx" lists them. Only file names are checked, for library games.

### Fixed
- **Deleted games leave the Games tab.** A game or program whose exe is gone was still listed, for games with HDR on as "Program". A game you uninstall now disappears the next time the games are read: when the app starts, when you open its window and with **Scan games**. Other programs disappear with the next **Scan games** or automatic scan, so one that was moved is found again first. A drive that is not connected, or a game folder that still holds other programs (for example during an update), does not count as deleted. If you reinstall a game, turn HDR on for it again (*RenoDX Games* covers its games by itself); *Never HDR* and game options are kept.
- ***Reload config.json*** after a locked `config.json` at start-up: settings are saved again without restarting the app.
- **Steam games no longer disappear when `libraryfolders.vdf` is busy.** The file is read without blocking Steam, with a few retries. If it still cannot be read, games in Steam's main folder stay listed and the log says so.
- **A settings file that is briefly locked can no longer be replaced by default settings.** If `config.json` could not be read at start-up (for example while antivirus, a backup tool or another program had it open), the app treated it as damaged and saved default values over it. Now it tries again for a moment, and if the file still cannot be read it is left untouched: the app runs with default values, saves nothing until the next start or *Reload config.json*, and the log says so. A really damaged file is backed up before anything is replaced – under a new name if a backup already exists, and if no backup can be made the file is not overwritten.

## 2.6.2 – 2026-11

### New
- **Battle.net games** are found automatically, with their real names (Diablo IV, Overwatch 2, Call of Duty, …). Test realms and betas are skipped.
- **Amazon Games** (Prime Gaming) are found automatically. The app reads the Amazon Games install list with the SQLite that is part of Windows – nothing extra to install.
- **itch.io games** installed with the itch app are found automatically, with the executables the itch app itself would launch. Tools, comics and soundtracks are skipped.
- **Riot Games** (VALORANT, League of Legends, …) are found automatically. Like every other game, they are only watched from outside, as Task Manager does – nothing that anti-cheat systems such as Vanguard react to.
- **Games installed with Heroic Games Launcher** (Epic, GOG and Amazon games) are found automatically, with the exe Heroic launches.
- **Xbox / PC Game Pass games in another install folder** are found too: the app reads the folder chosen in the Xbox app (*Settings › General › Game install options*) instead of looking in `XboxGames` only.
- **Games without a launcher** (DRM-free downloads, emulators, …) are suggested in the Games tab when the Xbox Game Bar recognised them while they ran. They are listed under the name of their game folder and stay off until you turn them on. Programs from the Apps tab are never suggested. A program that the Game Bar saw in the folder of a library game is added to that game instead, for example the real game exe behind a starter.

### Changed
- **Steam games: the exe Steam really starts.** The app reads the game's launch options from Steam's local app cache (`appcache\appinfo.vdf`) instead of guessing the exe from file names and sizes. Launchers in the launch options are skipped as before, and so are the editors, tools and comics that some games also ship. Software, videos and soundtracks that Steam installs like games (a budget app, a trailer) are no longer listed. If the cache cannot be read, the old search is used. Library games are detected once more after the update; your HDR choices are kept.
- **Programs that only share a game's exe name no longer switch HDR.** For a game from a launcher library, a process with the same name that runs from outside the game's install folder (two unrelated `game.exe`, a tool called like a game) is ignored, and the log says so. Folder links and junctions are followed. Process names that no library game uses, games whose location is unknown and processes whose path cannot be read are matched by name as before.

### Fixed
- **Local HDR videos without "HDR" in the title** (mpv, MPC-HC, VLC, …) now switch HDR on. mpv shows the title stored in the file, and many files have no "HDR" in their name, so *HDR only* never switched. When the title has no HDR keyword, the app looks at the file on the player's command line: its name, and the video header (MKV/WebM and MP4/MOV: PQ or HLG transfer, HDR mastering data, Dolby Vision). Read-only, once per file. A file opened later from the player's own menu or by drag and drop is still recognised by the title only.
- *Settings › Never HDR games* shows the right count after excluding a game with the keyboard shortcut.
- **More launchers are recognised:** Paradox's launcher (`dowser.exe`), PlayLAN, Thief: Deadly Shadows' `runme.exe`, the FINAL FANTASY XIV and Elite Dangerous launchers and programs in a `Launcher` folder are treated as launchers, so the app watches the game they start.
- **Games whose starter runs another exe:** the app also watches the real game exe in a subfolder when its name extends the starter's name, e.g. `HeroesOfTheStorm_x64.exe` next to `Heroes of the Storm.exe`.
- **Installers, runtimes and helper programs are no longer taken for the game:** exes in installer folders and bundled Java runtimes (`_CommonRedist`, `Support`, `DotNetCore`, `jre`, …), .NET installers (`netfx…`), companion apps, crash watchdogs, PunkBuster, the Java runtime (`javaw.exe`) and support tools are skipped when the app looks for a game's exe. Editors, consoles, dedicated servers, debug builds and support programs are no longer watched as extra exes.
- **Games with a small exe or a short exe name are found:** the exe search also takes exes under 300 KB that are named like the game, or when the folder has no larger exe (Far Cry 3 Blood Dragon, Mount & Blade II: Bannerlord), and exes named after the game's initials (`AOW4.exe` for Age of Wonders 4).
- **"Never HDR" games stay excluded after a rescan:** when a rescan finds new exes for a game you excluded, they are excluded too. Before, the game came back to the Games tab.

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
