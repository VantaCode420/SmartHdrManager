# Smart HDR Manager

Turns Windows HDR on automatically when you need it – HDR games, HDR videos on YouTube, local videos in your video player (including SDR videos with NVIDIA RTX Video HDR) – and back to SDR when you are done. It lives in the system tray.

**New in 2.6:** the app only switches back HDR that it turned on itself – HDR you (or madVR, a game, another tool) turned on stays on. New **Apps** tab: one mode per video player, browser, creative app and recording app – programs that are not listed are never touched. **Check for updates** with one-click install, a hint when a game closes right after HDR came on (e.g. a crash with NVIDIA RTX HDR), and a choice how often the app looks for new games. **2.6.1:** games set to *Never HDR* can be restored (Settings › General › Never HDR games); cs2 and valorant are no longer excluded by default. **2.5.2:** games with a launcher (The Witcher 3 from GOG, …), HDR stays on while a video is paused, *SDR for this video*. See [CHANGELOG.md](CHANGELOG.md).

   ![Smart HDR Manager main window](docs/main.png)

> **Beta.** Tested on Windows 11 25H2 with an NVIDIA GeForce RTX 5070 Ti and one HDR monitor (ASUS PG32UCDMR); RTX Video HDR tested with MPC-HC, VLC and PotPlayer.
> Not tested yet: AMD and Intel graphics, Windows on ARM (Snapdragon), Windows 10 / Windows 11 24H2 and older, several HDR monitors, laptops.
> If something does not work on your PC, please report it (see *Reporting a problem*).

   ☕ **Like the app?** You can support development on [Ko-fi](https://ko-fi.com/vantacode420).

---

## Download – which file?

| File | Size | Use it if … |
|---|---|---|
| `SmartHdrManager-v…-setup.exe` | large | **You are not sure.** Installer for x64 and Windows on ARM (picks the right version): Start menu entry, optional *Start with Windows*, uninstall from *Settings › Apps*. No administrator rights, nothing else to install. |
| `SmartHdrManager-v…-win-x64-standalone.zip` | large | You prefer a folder you unzip yourself (portable). Runs on any 64-bit Windows 10/11, nothing else to install. |
| `SmartHdrManager-v…-win-x64.zip` | small | You already have the **.NET 10 Desktop Runtime** installed ([download](https://dotnet.microsoft.com/download/dotnet/10.0) → ".NET Desktop Runtime", x64). |
| `SmartHdrManager-v…-win-arm64-standalone.zip` | large | *Experimental, untested.* Windows on ARM (e.g. Snapdragon X laptops), nothing else to install. |
| `SmartHdrManager-v…-win-arm64.zip` | small | *Experimental, untested.* Windows on ARM with the .NET 10 Desktop Runtime **Arm64** installed. |

Not sure if your PC is ARM? *Settings › System › About › System type* says "ARM-based processor" on ARM PCs – everyone else takes x64.

Requirements: Windows 10 or 11 (64-bit) and an HDR-capable monitor with HDR available in *Windows Settings › Display*.

## Installation

**With the installer** (`…-setup.exe`):

1. Start the setup. **"Windows protected your PC"?** The installer is not code-signed either: click **More info › Run anyway** (how to check the file first: see *From the zip*, step 3).
2. Choose whether the app starts with Windows and whether you want a desktop shortcut. It installs for your Windows user only, in `%LocalAppData%\Programs\Smart HDR Manager` – no administrator rights needed, and *Check for updates* keeps working.
3. To install a newer version by hand, run its setup: the running app is closed first, your settings, games and *Start with Windows* choice are kept.

To install for all users (in Program Files), start the setup with `/ALLUSERS` from a command prompt as administrator. *Check for updates* cannot write there: update by running the new setup.
Silent install for scripts: `/VERYSILENT` (add `/TASKS=autostart,desktopicon` to choose the options).

**From the zip:**

1. Unzip to a permanent folder, e.g. `C:\Tools\Smart HDR Manager`. (Don't run it from inside the zip or from Downloads – "Start with Windows" remembers the location.)
2. Start `SmartHdrManager.exe`.
3. **"Windows protected your PC"?** The app is not code-signed (signing costs money every year), so Windows does not know it yet. Click **More info › Run anyway**. You can check the file first: compare it with `SHA256SUMS.txt` from the release, or look at the VirusTotal link in the release notes.
4. Optional: in **Settings**, turn on **Start with Windows**.

**Updating:** from 2.6 on, *Settings › Updates › Check for updates* installs a new version with one click (the download is checked against the release's SHA256 checksums first). By hand: exit the app (tray icon › Exit), replace the files in your folder with the new ones and start it again. Your settings and game list are kept. (If you unzip to a new folder instead, start the new exe once – "Start with Windows" and the "(with HDR)" player entries are moved to it automatically.)

Settings are stored in `%AppData%\SmartHdrManager\config.json`, the log in `%LocalAppData%\SmartHdrManager\log.txt` (the older part moves to `log.old.txt`, both stay small).
**Portable mode:** put an empty file named `portable.txt` next to the exe, and the settings are kept in that folder instead.

## How it works

| HDR turns on when … | How it is detected |
|---|---|
| A game from your list is running | Games tab: games from Steam, Epic, GOG, Xbox, Ubisoft Connect, EA app and Rockstar are found automatically – switch HDR on per game. |
| A real HDR video plays on YouTube | The browser script marks the tab (see below). |
| A local HDR movie plays full screen | Video player (VLC, mpv, mpv.net, MPC-HC, MPC-BE, PotPlayer – see the Apps tab) + "HDR", "Dolby Vision", "DoVi" or "BT2020" in the window title. |
| A video is opened with "<Player> (with HDR)" | **Apps** tab › Video players: HDR switches on *before* the player starts – best for RTX Video HDR (see below). |
| *(optional)* Any video plays – for **RTX Video HDR** | Settings › Videos / RTX Video HDR. Browsers via the script, video players while they play. |

When the content ends, the app waits a few seconds (Settings › Back to SDR › *Wait before switching back*) and switches back to SDR. When you exit the app, it switches back to SDR if it turned HDR on (HDR that was already on, or a game that is still running, is left alone).

**The app only undoes what it did itself** (2.6). HDR that you turn on (Win+Alt+B, Windows Settings) or that another program turns on (madVR / MPC, a game's own HDR option, another tool) is never switched back to SDR – and if you turn HDR *off* while a game from your list runs, the app does not turn it on again until that game ends or the next game / video starts. Exceptions you chose yourself: a creative app set to SDR, or a recording app switched on in the Apps tab – afterwards HDR comes back on if it was on before. A full-screen game that switched HDR on by itself is switched back once it closes (some games leave HDR on). Want the old behaviour (always back to SDR when nothing in your lists needs HDR)? *Settings › Back to SDR › Keep the desktop in SDR*.

**Games with a launcher** (REDprelauncher, PlayGTAV, …): if a game in your list closes within a few seconds, the app keeps HDR on and waits up to 45 s for the real game from the same folder, follows it and remembers it – next time HDR switches straight away. The log says "Following '…' (started by '…')". Libraries that register a launcher instead of the game (e.g. The Witcher 3 from GOG) are detected correctly since 2.5.2.

**Games tab:** **Scan games** re-reads the game libraries and your drives. **Add program** / **Add running** adds any other program. **Options** sets a per-game delay before HDR (for games that crash or flicker if HDR switches during startup), how long HDR stays on after the game closes, and **Launch with HDR** (see below). **Never HDR** hides a game and never uses HDR for it – undo it in *Settings › General › Never HDR games*, or search for the game in the Games tab and turn it on again.

**Tray menu:** open the window, **SDR for this video** (only shown while a video uses HDR: back to SDR until that video stops – the next video switches as usual; *HDR for videos again* ends it early), pause automation, **HDR override** (force HDR on or SDR for a while), exit.

**Keyboard shortcuts** (Settings › Keyboard shortcuts – Win+Alt, Ctrl+Alt or off):
`+ H` use HDR for the game in front · `+ I` never HDR for it (browser or video player in front: *SDR for this video* – the browser itself is never excluded) · `+ O` override: HDR on → SDR → automatic.

**Command line** (for Stream Deck buttons, shortcuts or scripts – a second launch only passes the command to the running app):
```
SmartHdrManager.exe --force-hdr      HDR on until changed back
SmartHdrManager.exe --force-sdr      SDR until changed back
SmartHdrManager.exe --auto           back to automatic switching
SmartHdrManager.exe --toggle-pause   pause / resume automation
SmartHdrManager.exe --tray           start hidden in the tray
SmartHdrManager.exe --exit           exit the running app (as tray icon › Exit)
SmartHdrManager.exe --launch "C:\Games\Game\game.exe"   HDR on first, then start the game (see below)
SmartHdrManager.exe --launch-video "C:\...\mpc-hc64.exe" "movie.mkv"   same for a video player (ends when its window closes)
```

### Apps tab

Every program the app reacts to that is not a game, found automatically when installed – one mode per program. **Programs that are not listed (and not in the Games tab) are never touched.**

| Group | Modes |
|---|---|
| **Video players** (MPC-HC, MPC-BE, VLC, PotPlayer, mpv, mpv.net, Kodi, or *Add player*) | **Auto** – videos as set in Settings › Videos · **HDR only** – only HDR videos · **Off** – the player handles HDR itself (e.g. madVR). Plus *Default app…* / *More* to start the player with HDR already on (see RTX Video HDR). |
| **Web browsers** (Chrome, Edge, Firefox, Brave, Opera, Vivaldi) | **Auto** – YouTube HDR and videos as set in Settings · **HDR only** · **Off**. Needs the browser script. |
| **Creative apps** (Photoshop, Lightroom, Premiere Pro, After Effects, DaVinci Resolve; Illustrator, Media Encoder, Capture One, Krita start switched off) | **On** – SDR while the app is in front, for accurate colours · **Off**. |
| **Recording & streaming** (OBS Studio, Streamlabs) | **On** – while it runs nothing switches HDR on, games included, and HDR goes back to SDR the moment it starts, so you never record blown-out HDR footage (HDR you had on comes back afterwards) · **Off** (default) – running it changes nothing. |

A manual override (tray menu, `+ O`) always works. *Add …* adds any other program to a group.

### Launch with HDR

Some games only check whether Windows HDR is on at the moment they start, and hide their HDR option otherwise (or Windows Auto HDR does not kick in). Switching HDR when the game process appears is too late for them – you had to restart the game. **Launch with HDR** turns HDR on first, waits until it is really on, then starts the game, and keeps HDR on until the game closes.

- **Steam:** Games tab › select the game › **Options › Copy Steam launch option**, then in Steam: right-click the game › *Properties › General › Launch options* › paste. It looks like `"C:\Tools\Smart HDR Manager\SmartHdrManager.exe" --launch %command%`.
- **Other games:** **Options › Create HDR desktop shortcut** puts a "<Game> (HDR)" shortcut on your desktop.
- If you move the app folder, copy the launch option / create the shortcut again.
- The program to start only ever comes from the shortcut or launch option itself – the running app never receives a path from anywhere else.

<p>
  <img src="docs/settings.png" alt="Settings (part 1)" width="49%">
  <img src="docs/settings2.png" alt="Settings (part 2)" width="49%">
</p>

## RTX Video HDR (NVIDIA RTX graphics cards)

> ⚠️ **Turn off NVIDIA RTX HDR for games** (*NVIDIA App › Graphics › Global Settings › RTX HDR › Off*). With it on, some games crash when Windows switches to HDR while they start. RTX **Video** HDR (for videos, below) is a different setting and can stay on.

NVIDIA RTX Video HDR turns SDR videos into HDR – but only while Windows HDR is on. First switch it on in the **NVIDIA App › System › Video › RTX Video Enhancements › HDR** (Super Resolution there is optional). The app only *reads* this setting; it never changes driver settings.

### Local videos: start the player with HDR (recommended)

Video players decide when playback starts whether they output HDR. If Windows switches to HDR a moment later, RTX Video HDR does not kick in for SDR videos. **Apps › Video players** solves that by switching HDR on *before* the player starts. It lists your installed players (MPC-HC, MPC-BE, VLC, PotPlayer, mpv.net, Kodi – or add any other) and offers

- **Default app…** (easiest): registers "<Player> (with HDR)" as an app for video files and opens *Settings › Default apps*, where you click **Set default** once. From then on, every double-click on a video plays it with HDR on. (Windows does not let programs set themselves as default – that one click is yours. Alternative: right-click a video › *Open with* › *Choose another app* › "<Player> (with HDR)" › *Always*.)
- **More › Create desktop shortcut** "<Player> (HDR)": double-click it or drag a video onto it.
- **More › Add right-click entry** "Play with HDR (<Player>)" for video files (Windows 11: *Show more options*).

All of them switch HDR on first, wait until it is really on, start the player, and switch back to SDR about a second after the player window is closed. No administrator rights are needed.

![Apps tab](docs/apps.png)

### Player settings for RTX Video HDR

The player must also use a video output that supports RTX Video HDR. Tested:

| Player | RTX Video HDR | |
|---|---|---|
| **MPC-HC** | ✅ works | **recommended** |
| VLC | ✅ works | |
| PotPlayer | ✅ works | |
| MPC-BE | should work (same renderer as MPC-HC) | not tested |
| Kodi | ❌ no RTX Video HDR | HDR switching still works for real HDR videos |

**MPC-HC (recommended)**
1. *View › Options › Playback › Output* › **Video Renderer: MPC Video Renderer**.
2. Click **Properties** next to it (or right-click the video while playing › *Filters › MPC Video Renderer*):
   - ✅ **Use Direct3D 11**
   - ✅ **RTX Video HDR** (section *DXVA2 and D3D11 video processor*)
   - optional: *Request Super Resolution* (e.g. "for ≤ 1440p")
3. OK, then restart MPC-HC.

**VLC**
1. *Tools › Preferences* › bottom left: **Show settings: All**.
2. *Video › Output modules* › **Direct3D11**:
   - **HDR Output Mode: Generate HDR from SDR**
   - optional: *Video Upscaling Mode: Super Resolution*
3. **Save**, then restart VLC.

**PotPlayer**
1. Right-click › *Preferences* (F5) › **Video**.
2. **Video renderer: Built-in Direct3D 11 Video Renderer**.
3. ✅ **D3D11 GPU RTX Video HDR**, optional: ✅ *D3D11 GPU Super Resolution*.
4. OK, then restart PotPlayer.

### Automatic video switching (browsers and players)

With **Settings › Videos / RTX Video HDR › HDR for videos**, the app turns HDR on while a video plays – this is how browser videos (YouTube & co., via the browser script) get RTX Video HDR:

- **Smart** – HDR videos, plus SDR videos when RTX Video HDR is switched on in the NVIDIA App.
- **HDR only** – only real HDR videos. **Always** – every video.
- **When:** *Full screen* (recommended) or *Any* (also windowed).
- **Wait after the video stops:** 0 s up to 5 minutes. Pausing does not switch HDR off with browser script 3.1 (it keeps the marker for up to 10 minutes while the paused video's tab is visible); for video players, choose a longer delay if you pause often.
- **One video should stay in SDR:** tray menu › **SDR for this video** (or the `+ I` shortcut with the browser / player in front). Other and later videos switch as usual.
- **Video players:** detects players started normally. For RTX Video HDR in players, starting them with HDR works better (see above). Per player: *Apps › Video players* (Auto / HDR only / Off).
- Only the monitor that shows the video is switched (it must be enabled under Settings › Monitors).
- Games always come first. Recording? Turn on OBS in *Apps › Recording & streaming* – then nothing switches HDR while it runs. On battery power video switching is off unless you change that.
- **Kodi** (automatic mode): it does not show the file name in its window title, so the app cannot tell HDR from SDR – use *Which videos: Always*. It reacts while Kodi plays sound, so music in full-screen Kodi switches too. Other players: *Apps › Add player*.

---

## Browser script (needed for YouTube HDR and browser videos)

The file `SmartHdrManager_YouTube.user.js` is a small Tampermonkey script. While a video plays, it adds a marker to the tab title:
` [real_hdr_active]` for real HDR YouTube videos, ` [sdr_video_active]` for other videos. Smart HDR Manager reacts to that marker.

On YouTube it also makes the site offer the HDR version of a video while Windows is still in SDR. On other sites it only adds the marker – it reads nothing, sends nothing and changes nothing else. It runs on all sites so that videos on any site can be detected.

### Chrome, Brave, Opera, Vivaldi

1. **Install Tampermonkey** from the Chrome Web Store: <https://chromewebstore.google.com/detail/tampermonkey/dhdgffkkebhmkfjojejmpbldmpobfkfo> → **Add to Chrome** → **Add extension**.
2. **Allow user scripts** (required since Chrome 138 – without it, no script runs):
   - Open `chrome://extensions` (Brave: `brave://extensions`, Opera: `opera://extensions`, Vivaldi: `vivaldi://extensions`).
   - Click **Details** under Tampermonkey.
   - Turn on **Allow User Scripts**.
   - *Don't see that switch?* Your browser is older: turn on **Developer mode** (top right of the extensions page) instead.
3. **Add the script:**
   - Click the puzzle icon in the toolbar → **Tampermonkey** → **Create a new script…**
   - In the editor, select everything (**Ctrl+A**) and delete it.
   - Open `SmartHdrManager_YouTube.user.js` with Notepad, copy everything (**Ctrl+A**, **Ctrl+C**) and paste it into the editor (**Ctrl+V**).
   - Press **Ctrl+S** to save. The script now appears as *enabled* in the Tampermonkey dashboard.
4. **Reload** tabs that were already open (or restart the browser).

### Microsoft Edge

1. Install Tampermonkey from the Edge Add-ons store: <https://microsoftedge.microsoft.com/addons/detail/tampermonkey/iikmkjmpaadaobahmlepeloendndfphd> → **Get**.
2. Open `edge://extensions`, click **Details** under Tampermonkey and turn on **Allow User Scripts**. If there is no such switch, turn on **Developer mode** (left side of the extensions page).
3. Add the script as in step 3 above, then reload open tabs.

### Check that it works

Settings › YouTube in browser shows **Browser script: Detected ✓** once the app has seen the marker. Or open any YouTube video and hover over the browser tab: the title ends with **[sdr_video_active]** while it plays, or **[real_hdr_active]** for an HDR video (search YouTube for "4K HDR"). No marker? Check that the script is enabled in the Tampermonkey dashboard and that *Allow User Scripts* / *Developer mode* is on.

**Updating the script:** open the Tampermonkey dashboard, click the script, replace its text with the new file and save. The version is shown in the dashboard – **3.1** (2.5.2) keeps HDR on while a video is paused; 3.0 still works with the app.

---

## Safety and privacy

- No telemetry, no ads, and no internet connection – except when you click **Check for updates** (or turn on the weekly check, off by default): then the app asks `api.github.com` for the latest release of this project and, if you click *Update now*, downloads it from `github.com`. Nothing about you or your PC is sent. The download is only installed if its SHA256 matches the release's `SHA256SUMS.txt`.
- **Anti-cheat safe:** the app never reads or changes game memory and injects nothing. It only reads process names and window titles, like Task Manager does.
- Some antivirus programs may be suspicious of an unsigned tray app that watches running programs and has global keyboard shortcuts. The release notes link a VirusTotal scan of exactly these files.
- **Can the browser script make the app run code?** No. The script only changes the tab title; the app only checks whether a window title *contains* the marker text. Nothing from a title is executed or written anywhere. Any website can set any tab title anyway, so the worst a malicious script or site could do is switch HDR on. The app itself only ever starts Notepad (when you click *Open config.json / log file*), a fixed Windows colour-profile task (off by default), itself (*Start with Windows*, and the new version after *Update now*) and – with *Launch with HDR* / the Apps tab – the game or player named in your own shortcut, Steam launch option, right-click entry or "(with HDR)" app.
- **Note:** a website can read its own tab title, so in theory it could notice the marker while a video plays and tell that the script is installed. Nothing else is exposed.

## Known limitations

- RTX Video HDR status is read only; the app cannot switch it.
- Video players are detected by their audio output. A video file without an audio track is not detected in "RTX Video HDR" mode.
- On sites other than YouTube, every video counts as SDR (HDR can't be detected reliably there).
- Netflix, Disney+ and Apple TV are not supported yet.
- Kodi has no RTX Video HDR (HDR switching works for real HDR videos).
- HDR switching takes 1–2 seconds and the screen may go black briefly – that's Windows/the monitor, not the app.

## Troubleshooting

- **HDR does not turn on:** look at the **Log** tab – every decision is written there. Check *Settings › Monitors* (is your HDR monitor enabled?).
- **Colours look wrong after HDR:** the log shows a `COLOR` line after every switch back to SDR, comparing the display before and after. A reset calibration / night-light curve is restored automatically.
- **A game crashes or flickers at start:** first turn off **NVIDIA RTX HDR** for games (*NVIDIA App › Graphics › Global Settings › RTX HDR › Off* – RTX Video HDR can stay on); with it on, some games crash when HDR switches while they start. Still crashing? Use **Launch with HDR** (Games tab › Options), so HDR is on before the game starts, or *Delay before HDR turns on* = 3–5 s. When a game from your list closes within seconds after HDR came on, the log shows a `HINT`; the second time you also get a notification and a hint in the game's options.
- **A game hides its HDR option / needs a restart to show HDR:** use **Launch with HDR** (Games tab › Options).
- **A game switches HDR itself (its own HDR option):** the app notices and keeps HDR on until the game closes – the log says "switched HDR on itself". Adding the game to your list works too.
- **RTX Video HDR does not kick in for local videos:** start the player with HDR (Apps › Video players › *Default app…*), and check the player settings above.
- **Several monitors:** by default only the primary monitor is switched. If a game runs on another screen, the log shows a `HINT` line – select that monitor in Settings › Monitors.
- **A game from your list does not switch HDR:** look at the line under its name in the Games tab – it shows the exe the app watches (e.g. "GOG · witcher3.exe"). If that is a launcher, start the game once: the app follows the real game and remembers it. Or add the game's exe with **Add program**, or press `+ H` while the game is in front.
- **HDR stays on although nothing needs it:** it was probably turned on by you or another program – the app leaves that alone (the log says "turned on by you or another program"). Turn it off with Win+Alt+B, or turn on *Settings › Keep the desktop in SDR* for the old behaviour.
- **A video player / madVR switches HDR itself and the app gets in the way:** set that player to **Off** in the Apps tab.

## FAQ

**Why not just leave HDR on all the time?**
You can, if you are happy with it. Many people switch because of how Windows shows normal (SDR) content in HDR mode: Windows maps SDR with the piecewise sRGB curve, while most SDR content is made for gamma 2.2, so dark tones look greyer and the desktop looks washed out. OLED monitors are often less bright for full-screen white in HDR mode, some games and apps behave oddly or show banding with HDR on, and screenshots / recordings in HDR come out blown out. The app gives you HDR only while there is HDR content – and SDR the rest of the time.

**HDR switches too often for videos.**
Settings › Videos / RTX Video HDR: set *Which videos* to **HDR only** (SDR videos then stay in SDR – RTX Video HDR is not used for them), and/or set *Wait after the video stops* to 1–5 minutes. Update the browser script to 3.1, so pausing a video does not switch HDR off. If you don't want video switching at all, turn off *HDR for videos* – games still switch.

**One video should not use HDR.**
Tray menu › **SDR for this video**, or `+ I` (Win+Alt+I by default) while the browser / player is in front.

**I turned HDR on myself – will the app switch it off?**
No (since 2.6). The app only switches back HDR that it turned on itself. Exceptions are the ones you set up: a creative app set to SDR, or a recording app switched on in the Apps tab – and then HDR comes back on afterwards.

**My video player (madVR, MPC-BE, mpv …) switches HDR by itself.**
Set the player to **Off** in the Apps tab – the app then leaves it completely alone.

**Does it work with AMD / Intel graphics?**
HDR switching uses the normal Windows display functions, so it should – but it has not been tested yet. RTX Video HDR is NVIDIA only. Reports are very welcome.

## Reporting a problem

Please open an [issue on GitHub](https://github.com/VantaCode420/SmartHdrManager/issues/new/choose) – the form asks for everything needed: app version, Windows version, graphics card and driver, monitor(s), what you did, what happened – and the log file (Settings › **Open log file**, or `%LocalAppData%\SmartHdrManager\log.txt`).

## Uninstall

**Installed with the setup:** *Windows Settings › Apps › Installed apps › Smart HDR Manager › Uninstall*. The uninstaller closes the app and removes *Start with Windows*, the "Play with HDR" / "(with HDR)" player entries and the notification name. At the end it asks whether to delete your settings and log too (choose *No* to keep them for later). Remove the browser script in the Tampermonkey dashboard.

**Unzipped by hand:**

1. Settings › turn off **Start with Windows** (removes the startup entry).
2. Exit the app (tray icon › Exit) and delete its folder.
3. Optional: delete `%AppData%\SmartHdrManager` (settings) and `%LocalAppData%\SmartHdrManager` (log).
4. In the browser: remove the script in the Tampermonkey dashboard.
5. If you used *Default app…* / right-click entries for players: remove them in the Apps tab first (More menu). Otherwise Windows keeps showing them; they live under `HKEY_CURRENT_USER\Software\Classes` (`SmartHdrManager.HdrPlay.*`, `SystemFileAssociations\<.ext>\shell\SmartHdrManager.*`) and `HKEY_CURRENT_USER\Software\SmartHdrManager`.
6. Optional: delete `HKEY_CURRENT_USER\Software\Classes\AppUserModelId\VantaCode420.SmartHdrManager` (the app's name for Windows notifications).

Shortcut for steps 1, 5 and 6: exit the app, then run `SmartHdrManager.exe --unregister` once before you delete the folder.

## License

MIT – see `LICENSE.txt`.
