using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHdrManager
{
    public enum HdrOverride { None, ForceHdr, ForceSdr }

    public sealed class EngineStatus
    {
        public bool HdrOn { get; init; }
        public bool Paused { get; init; }
        public string ActiveProcess { get; init; } = string.Empty;
        public bool IsFullscreen { get; init; }
        public string? RunningGame { get; init; }
        public HdrOverride Override { get; init; }
        public long OverrideRemainingMs { get; init; } = -1;   // -1 = until turned back
        public string? WaitingForGame { get; init; }            // per-game start delay in progress
        public int WaitingSecondsLeft { get; init; }
        public int HoldSecondsLeft { get; init; }               // minimum HDR time / keep-after-exit in progress
        public string? Video { get; init; }                     // video that holds HDR right now (video automation)
        public string? BlockedBy { get; init; }                 // "No HDR while OBS is running": the program that blocks HDR
        public bool LaunchHold { get; init; }                   // "Launch with HDR": HDR is held for a game being started
        public string? FollowingLauncher { get; init; }         // a launcher closed: waiting for the game it starts
    }

    /// <summary>One row of the Games tab: a library game (several processes possible) or a single program.</summary>
    public sealed class GameRow
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Subtitle { get; init; } = string.Empty;
        public List<string> Processes { get; init; } = new();
        public string? ExePath { get; init; }
        public bool Hdr { get; init; }
        public bool HasOptions { get; init; }
        public bool Excluded { get; init; }                     // "Never HDR" (2.6.1: shown again so it can be undone)
    }

    /// <summary>
    /// The HDR/SDR decision loop. Runs on its own background thread and owns every display switch.
    /// The UI only changes the configuration (under <see cref="Sync"/>) or posts work with <see cref="Post"/>.
    ///
    /// Cost per tick (default every 100 ms): one foreground-window lookup with a cached process name.
    /// The process list is scanned once per second and the display HDR state is read every 2 seconds,
    /// or immediately after a switch, a display change or resume from sleep.
    /// </summary>
    public sealed class HdrEngine
    {
        /// <summary>Old location (next to the exe). Not writable when the app is installed in "Program Files".</summary>
        public static readonly string LegacyConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");

        /// <summary>
        /// Portable mode: a file named "portable.txt" next to the exe keeps the settings next to the exe
        /// (e.g. for a USB stick). Otherwise settings live in %AppData%\SmartHdrManager\config.json.
        /// </summary>
        public static readonly bool IsPortable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

        public static readonly string ConfigPath = IsPortable
            ? LegacyConfigPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartHdrManager", "config.json");
        public static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SmartHdrManager", "log.txt");

        private const int ProcessScanIntervalMs = 1000;   // how often running processes are checked for HDR games
        private const int HdrRequeryIntervalMs = 2000;    // how often the real HDR state is re-read from Windows
        private const int SwitchSpacingMs = 1500;         // minimum time between two automatic switch attempts
        private const int FailuresBeforeCooldown = 3;
        private const int FailureCooldownMs = 30000;      // stop retrying for 30 s after repeated failures
        private const int StartupScanDelayMs = 20000;     // let Windows finish logging in before scanning drives
        private const int ScanDueCheckMs = 3600000;       // while running: once an hour, check whether the automatic scan is due
        private const int AutoScanRecheckMs = 60000;      // automatic scan waits while a game / full-screen app runs
        private const int DisplayEventGraceMs = 5000;     // HDR lost this soon after a display change / wake-up = not a user decision
        private const int QuickExitMs = 12000;            // a game closing this soon after HDR came on may have crashed (crash hint)
        private const int QuickExitClearMs = 60000;       // a game that ran this long with HDR is fine again (hint cleared)
        public const int MaxVideoRevertSeconds = 300;     // "Back to SDR after the video stops": up to 5 minutes (2.5.2)

        // A watched game that closes this soon after it appeared may only have been a launcher (REDprelauncher,
        // PlayGTAV, …): HDR stays on while the app waits for a program from the same folder to take over.
        private const int LauncherShortRunMs = 15000;
        private const int LauncherFollowMs = 45000;   // Steam can take 15 s or more to hand over (seen with The Witcher 3)

        private static readonly object FileLogLock = new();
        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;

        public AppConfig Config { get; private set; }
        public object Sync { get; } = new();
        public volatile bool Paused;
        public EngineStatus Status { get; private set; } = new();

        /// <summary>True while hotkeys must be polled (only if RegisterHotKey failed).</summary>
        public volatile bool PollHotkeys = true;          // the H / I shortcuts
        public volatile bool PollOverrideHotkey = true;   // the override shortcut

        private Thread? _thread;
        private volatile bool _stop;
        private readonly AutoResetEvent _wake = new(false);
        private readonly ConcurrentQueue<Action> _posted = new();
        private int _scanning;

        // ---- precomputed lookup sets, rebuilt only when the configuration changes ----
        private sealed class Snapshot
        {
            public readonly HashSet<string> HdrGames;
            public readonly HashSet<string> Ignored;
            public readonly HashSet<string> ForceSdr;
            public readonly HashSet<string> Browsers;
            public readonly HashSet<string> LocalPlayers;
            public readonly string[] VideoKeywords;
            public readonly string BrowserToken;
            public readonly bool EnableOnProcessStart, BrowserWindowed, RequireKeyword, ReloadProfile, KeepBrowserHdr, RefreshApps;
            public readonly int FallbackSeconds, TickMs;
            public readonly long MinimumHdrMs;
            public readonly MonitorFilter Monitors;
            public readonly string HotkeyMode;
            public readonly Dictionary<string, GameOptions> GameOptions;
            // video playback (RTX Video HDR)
            public readonly bool VideoAuto, VideoBrowsers, VideoPlayers, VideoFullscreenOnly, VideoBattery;
            public readonly string VideoMode, SdrToken;
            public readonly long VideoActivationMs, VideoRevertMs;
            public readonly HashSet<string> VideoBlockers;   // recording apps: no video HDR while they run
            public readonly HashSet<string> AllBlockers;     // recording apps set to "No HDR": no automatic HDR at all
            public readonly HashSet<string> VideoApps;  // browsers + video players (nudged after HDR comes on for a video)
            public readonly HashSet<string> AllVideoApps;   // browsers + players, also the ones set to "don't touch HDR"
            public readonly HashSet<string> HdrOnlyApps;    // browsers / players set to "only HDR videos"
            public readonly bool KeepDesktopSdr;            // 2.5 behaviour: also switch back HDR the app did not turn on
            // HDR library games whose exe location is known: process name -> the game's install folder(s) and exe(s). A
            // process with the same name that runs from somewhere else is another program (e.g. two unrelated "game.exe").
            public readonly Dictionary<string, (string[] Folders, string[] Exes)> GameFolders;

            public Snapshot(AppConfig c)
            {
                // "?? empty" guards: a list set to null in config.json must not crash every tick
                HdrGames = new HashSet<string>(c.HdrGamesAndApps ?? new List<string>(), Cmp);
                Ignored = new HashSet<string>(c.IgnoredGames ?? new List<string>(), Cmp);

                // Apps tab (2.6): one mode per program; "off" = the program is not touched at all
                ForceSdr = new HashSet<string>(Cmp);
                Browsers = new HashSet<string>(Cmp);
                LocalPlayers = new HashSet<string>(Cmp);
                VideoBlockers = new HashSet<string>(Cmp);
                AllBlockers = new HashSet<string>(Cmp);
                AllVideoApps = new HashSet<string>(Cmp);
                HdrOnlyApps = new HashSet<string>(Cmp);
                foreach (var kv in c.AppRules ?? new Dictionary<string, AppRule>())
                {
                    var rule = kv.Value;
                    if (rule == null || string.IsNullOrWhiteSpace(kv.Key)) continue;
                    string name = kv.Key;
                    switch (rule.Category)
                    {
                        case AppCategories.Browser:
                        case AppCategories.Player:
                            AllVideoApps.Add(name);
                            if (rule.Mode == AppModes.Off) break;
                            (rule.Category == AppCategories.Browser ? Browsers : LocalPlayers).Add(name);
                            if (rule.Mode == AppModes.HdrOnly) HdrOnlyApps.Add(name);
                            break;
                        case AppCategories.Creative:
                            if (rule.Mode == AppModes.Sdr) ForceSdr.Add(name);
                            break;
                        case AppCategories.Recording:
                            if (rule.Mode == AppModes.Off) break;
                            VideoBlockers.Add(name);
                            if (rule.Mode == AppModes.NoHdr) AllBlockers.Add(name);
                            break;
                    }
                }
                KeepDesktopSdr = c.KeepDesktopSdr;

                VideoKeywords = (c.HdrVideoKeywords ?? new List<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).ToArray();
                BrowserToken = string.IsNullOrWhiteSpace(c.BrowserRealHdrToken) ? string.Empty : c.BrowserRealHdrToken;
                Monitors = new MonitorFilter(c.OnlyPrimaryMonitor, c.SelectedMonitors);
                HotkeyMode = HotkeySettings.Normalize(c.HotkeyModifier);
                MinimumHdrMs = Math.Clamp(c.MinimumHdrSeconds, 0, 3600) * 1000L;
                GameOptions = new Dictionary<string, GameOptions>(c.PerGameOptions ?? new Dictionary<string, GameOptions>(), Cmp);
                EnableOnProcessStart = c.EnableHdrOnProcessStart;
                BrowserWindowed = c.EnableHdrForBrowserInWindowedMode;
                RequireKeyword = c.RequireKeywordForFullscreenVideo;
                ReloadProfile = c.ReloadColorProfileAfterSdr;
                KeepBrowserHdr = c.KeepBrowserHdrWhenNotFocused;
                RefreshApps = c.RefreshAppsAfterSdr;
                FallbackSeconds = Math.Max(0, c.SdrFallbackDelaySeconds);
                TickMs = Math.Clamp(c.ProcessScanIntervalMs, 50, 1000);
                VideoAuto = c.VideoHdrAutomation && (c.VideoHdrInBrowsers || c.VideoHdrInPlayers);
                VideoBrowsers = c.VideoHdrInBrowsers;
                VideoPlayers = c.VideoHdrInPlayers;
                VideoFullscreenOnly = VideoTriggers.Normalize(c.VideoHdrTrigger) == VideoTriggers.Fullscreen;
                VideoBattery = c.VideoHdrDisableOnBattery;
                VideoMode = VideoHdrModes.Normalize(c.VideoHdrMode);
                SdrToken = string.IsNullOrWhiteSpace(c.BrowserSdrVideoToken) ? string.Empty : c.BrowserSdrVideoToken;
                VideoActivationMs = Math.Clamp(c.VideoHdrActivationSeconds, 0, 30) * 1000L;
                VideoRevertMs = Math.Clamp(c.VideoHdrRevertSeconds, 0, MaxVideoRevertSeconds) * 1000L;
                VideoApps = new HashSet<string>(Browsers.Concat(LocalPlayers), Cmp);
                GameFolders = BuildGameFolders(c, HdrGames);
            }

            /// <summary>
            /// Install folders and known exes of the HDR games from the launcher libraries, by process name. Strings only:
            /// the snapshot is built under the config lock, so nothing here touches the disk (a sleeping drive or an
            /// offline NAS would block the UI). Programs added by hand are not listed and stay matched by name.
            /// </summary>
            private static Dictionary<string, (string[] Folders, string[] Exes)> BuildGameFolders(AppConfig c, HashSet<string> hdrGames)
            {
                var folders = new Dictionary<string, HashSet<string>>(Cmp);
                var exes = new Dictionary<string, HashSet<string>>(Cmp);
                void Add(string process, string? exePath)
                {
                    if (string.IsNullOrEmpty(exePath)) return;
                    try
                    {
                        string? folder = InstallFolder(exePath);
                        if (string.IsNullOrEmpty(folder)) return;
                        if (!folders.TryGetValue(process, out var set)) folders[process] = set = new HashSet<string>(Cmp);
                        set.Add(folder.TrimEnd('\\'));
                        if (!exes.TryGetValue(process, out var files)) exes[process] = files = new HashSet<string>(Cmp);
                        files.Add(Path.GetFullPath(exePath));
                    }
                    catch { }
                }

                // Library exes only: ProgramPaths holds the first exe of that name found anywhere (drive scan, Game Bar,
                // program caches), which may be the other program. A moved game passes because its old exe is gone.
                foreach (var g in c.LibraryGames ?? new List<LibraryGame>())
                    foreach (string p in g.Processes ?? new List<string>())
                        if (hdrGames.Contains(p)) Add(p, g.ExePath);   // the game's other exes (Unreal shipping exe, learned games) live in the same folder
                return folders.ToDictionary(kv => kv.Key, kv => (kv.Value.ToArray(), exes[kv.Key].ToArray()), Cmp);
            }

            /// <summary>
            /// The game's install folder for an exe: its game folder, or the folder above when the exe sits in one of the
            /// game's own subfolders ("boot\", "Launcher\", "game\", "_retail_\"). Never a shared folder.
            /// </summary>
            private static string? InstallFolder(string exePath)
            {
                string? folder = HdrLauncher.GameFolder(exePath) ?? Path.GetDirectoryName(Path.GetFullPath(exePath));
                if (string.IsNullOrEmpty(folder)) return null;
                string name = Path.GetFileName(folder);
                if (!name.StartsWith('_') && !GameSubfolders.Contains(name)) return folder;
                return HdrLauncher.GameFolder(folder) ?? folder;   // GameFolder of a folder: its parent, same checks
            }

            private static readonly HashSet<string> GameSubfolders = new(StringComparer.OrdinalIgnoreCase)
            {
                "boot", "launcher", "game", "client", "retailclient", "system", "engine", "content", "live", "release", "shipping"
            };
        }

        private volatile Snapshot? _snapshot;
        private Snapshot? _lastSnapshot;

        // ---- decision state (engine thread only) ----
        private bool _hdrAny, _hdrAll;
        private bool _verifyAllOn;   // after a "successful" ON: confirm on the next re-read that every display really is on
        private long _nextHdrQueryMs;
        private int _forceHdrQuery = 1;
        private MonitorFilter? _queriedMonitors;
        private long _hdrOnSinceMs;            // when HDR was last switched/seen on (0 = off)
        private bool _firstHdrQueryDone;
        private bool _apiLogged;

        // manual override (written from any thread, read by the engine thread)
        private volatile int _overrideMode;    // (int)HdrOverride
        private long _overrideUntilMs;         // 0 = until turned back
        private readonly object _overrideLock = new();
        private int _overrideChanged;          // 1 = engine thread must reset its timers (set by any thread)

        // per-game options
        private string? _delayGame;
        private long _delaySinceMs;
        private long _holdHdrUntilMs;          // keep-after-exit
        private string? _waitingGame;
        private long _waitingMsLeft;
        private long _holdMsLeft;

        private string? _runningGame;
        private long _nextProcessScanMs;
        private string? _activeHdrGameProcess;
        private string? _backgroundHdrBrowser;
        private long _nextBackgroundCheckMs;

        // ---- video playback (engine thread only) ----
        private const int VideoCheckIntervalMs = 250;
        private const int RtxRecheckMs = 60000;
        private readonly VideoPlaybackMonitor _video = new();
        private long _nextVideoCheckMs;
        private VideoCandidate? _videoCandidate;     // latest raw detection (already checked against the monitor rules)
        private string? _pendingVideoKey;            // activation delay
        private long _pendingSinceMs;
        private bool _videoActive;                   // a video holds HDR (incl. the revert delay)
        private string? _activeVideoKey;
        private string _activeVideoText = string.Empty;
        private bool _activeVideoNudge;              // SDR video: nudge browser / player after HDR is on (RTX Video HDR starts sooner)
        private MonitorFilter? _activeVideoTarget;
        private long _videoLostAtMs = -1;
        private bool _videoEnded;                    // switch back to SDR right away (the revert delay already ran)
        private bool _videoSdrRequested;             // "SDR for this video" was chosen: SDR now, even if HDR was not ours
        private string _videoEndedWhat = string.Empty;
        private string? _videoBlockLogged;
        private string? _runningVideoBlocker;        // e.g. obs64 (process scan)
        private string? _runningAllBlocker;          // a recording app set to "No HDR" (process scan)
        private IntPtr _checkedHwnd, _checkedMonitor;
        private MonitorFilter? _checkedAllowed, _checkedTarget;
        private string? _lastVideoProblem;
        private int _displaysChanged;
        private bool _onBattery;
        private long _nextBatteryCheckMs;
        private RtxVideoStatus? _rtx;
        private long _nextRtxCheckMs;
        private readonly Dictionary<string, MonitorFilter> _videoFilters = new(StringComparer.OrdinalIgnoreCase);

        // Monitors that are switched right now: a single monitor while a video holds HDR, otherwise the Settings selection
        private MonitorFilter? _videoTarget;
        private MonitorFilter Target(Snapshot s) => _videoTarget ?? s.Monitors;

        // "No HDR while OBS is running"
        private string? _blockedBy;          // set every tick while automatic HDR is blocked
        private string? _allBlockLogged;

        // "Launch with HDR" (SmartHdrManager.exe --launch): a launcher process keeps signalling while its game runs
        private const int LaunchHoldMs = 6000;   // the launcher signals every 2 s; HDR is held 6 s after the last signal
        private long _launchHoldUntilMs;
        private bool _launchHoldLogged;
        private int _launchReleased;     // set by ReleaseLaunchHold (any thread)
        private bool _launchEnded;       // switch back to SDR right away, no "return to SDR after" wait
        private string? _monitorCheckedGame;   // multi-monitor hint: game already checked

        // Launchers (2.5.2): a watched game that closes within seconds may only have started the real game
        private string? _seenGame;             // HDR game found by the last process scan
        private long _seenGameSinceMs;
        private bool _launcherTrackingStarted; // false until the first process scan after the app started
        private string? _followLauncher;       // launcher that just closed: waiting for its game (HDR stays on meanwhile)
        private long _followUntilMs;
        private bool _followHoldsHdr;          // HDR was on when the launcher closed: keep it on while waiting
        private DateTime _followNotBefore;     // the game it starts must be newer than the launcher
        private int _followGeneration;         // a new follow ignores the folder list of an older one
        private volatile HashSet<string>? _followNames;   // exe names in the launcher's game folder (filled in the background)

        // HDR switched on by this app (and not off since): switched back to SDR when the app exits (2.5.2).
        // 2.6: the app only ever switches back HDR that it turned on itself (unless "Keep the desktop in SDR" is on).
        private bool _hdrOnByApp;

        // Ownership (2.6). "Demand" = what wants HDR right now, e.g. "game:witcher3", "video:…", "sdr:photoshop", "none".
        // When HDR is changed by something else (you, a game, another program), the app keeps its hands off until the
        // demand changes – no switching back, no switching on again – so it never fights you.
        private string? _handOffDemand;
        private string _lastDemand = "none";
        private bool? _expectAny;                   // HDR state after the app's own last switch (or the last state seen)
        private bool _switchConfirmed = true;       // Windows has reported that state since the switch
        private long _displayEventMs = long.MinValue / 2;   // last display change / wake-up (any thread, Interlocked)
        private string? _monitorSignature;          // connected monitors (device paths) at the last display event
        private readonly object _monitorSignatureLock = new();
        private long _forceSdrSeenMs = long.MinValue / 2;   // an SDR app was last in front (HDR comes back 3 s later)
        private const int RestoreAfterMs = 3000;            // no flip-flop while e.g. a splash screen hands over to the app
        private bool _restoreHdr;                   // HDR that was not ours was switched off for an SDR / recording app
        private string _restoreFor = string.Empty;  // ... for this program: HDR comes back on once it is gone
        private bool _externalEnded;                // the full-screen game that switched HDR on itself has closed
        private string _externalEndedWhat = string.Empty;

        // Crash hint (2.6): a game that closes within seconds after the app switched HDR on for it
        private string? _hdrOnForGame;
        private long _hdrOnForGameAtMs;
        private string? _quickExitGame;             // waiting for the launcher check before it counts
        private long _quickExitAfterMs;
        private readonly ConcurrentQueue<(string Title, string Text)> _notifications = new();

        // Automatic drive scan (2.6: interval setting, also checked while the app runs)
        private long _nextScanDueCheckMs;
        private int _autoScanRunning;

        // "SDR for this video" (2.5.2): this video stays in SDR until it stops
        // Identified by its window and its title (tab title / file name): the next video in the same window has another
        // title, so it switches HDR again.
        private IntPtr _skippedHwnd;
        private string _skippedName = string.Empty;
        private long _skipLostAtMs = -1;
        private const int SkipEndsAfterMs = 3000;   // minimum time the skipped video must be gone before it counts as stopped
        private IntPtr _activeVideoHwnd;             // window and title of the video that holds HDR (for "SDR for this video")
        private string _activeVideoName = string.Empty;

        // HDR switched by something else (game, Win+Alt+B, another tool): logged with the window in front
        private const int ExternalChangeGraceMs = 8000;   // changes this soon after an own switch attempt are our own
        private string _foregroundProcess = string.Empty;
        private bool _foregroundFullscreen;
        // A full-screen game switched Windows HDR on by itself: keep it on while that game runs instead of fighting it
        private string? _externalHoldProcess;
        private long _nextExternalCheckMs;

        private List<DisplayColorState>? _sdrBaseline;   // colour state right before HDR was switched on

        private long _sdrFallbackAtMs = -1;
        private long _lastAttemptMs = long.MinValue / 2;
        private long _cooldownUntilMs;
        private int _consecutiveFailures;
        private long _lastPolledHotkeyMs = long.MinValue / 2;

        // ---- in-memory log ring buffer (Log tab) + file log ----
        private readonly object _logLock = new();
        private readonly List<string> _log = new();
        private int _logBase;

        public HdrEngine()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                // Never delete the log at startup (after a crash it is exactly what is needed): a large one moves to
                // log.old.txt, like the rotation while running
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                    File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.txt"), overwrite: true);
            }
            catch { }

            MigrateLegacyConfig();
            Config = LoadOrCreateConfig();
            UpgradeConfig(Config);
            _browserScriptSeen = Config.BrowserScriptSeenUtc != null;
            Save();
        }

        // ------------------------------------------------------------------
        // Browser script status (Settings › YouTube & browser)
        // ------------------------------------------------------------------
        private volatile bool _browserScriptSeen;

        /// <summary>True once a browser tab with the userscript's marker has been seen (remembered in config.json).</summary>
        public bool BrowserScriptSeen => _browserScriptSeen;

        private void NoteBrowserScript(Snapshot s, string procName, string title)
        {
            if (_browserScriptSeen || title.Length == 0 || !s.Browsers.Contains(procName)) return;
            bool marked = (s.BrowserToken.Length > 0 && title.Contains(s.BrowserToken, StringComparison.OrdinalIgnoreCase)) ||
                          (s.SdrToken.Length > 0 && title.Contains(s.SdrToken, StringComparison.OrdinalIgnoreCase));
            if (!marked) return;

            _browserScriptSeen = true;
            lock (Sync)
            {
                Config.BrowserScriptSeenUtc ??= DateTime.UtcNow;
                Save();
            }
            Log("INFO", $"Browser script detected ({procName}).");
        }

        /// <summary>
        /// Settings › Reset to defaults: every setting back to its default value. Kept: the game lists, per-game options,
        /// found games and program locations, extra game folders and the browser-script status.
        /// "Start with Windows" is a Windows setting, not part of config.json, and stays as it is.
        /// </summary>
        public void ResetSettings()
        {
            lock (Sync)
            {
                var c = Config;
                var fresh = new AppConfig
                {
                    ConfigVersion = c.ConfigVersion,
                    HdrGamesAndApps = c.HdrGamesAndApps,
                    IgnoredGames = c.IgnoredGames,
                    DiscoveredGames = c.DiscoveredGames,
                    LibraryGames = c.LibraryGames,
                    PerGameOptions = c.PerGameOptions,
                    ProgramPaths = c.ProgramPaths,
                    CustomGameFolders = c.CustomGameFolders,
                    LastDriveScanUtc = c.LastDriveScanUtc,
                    ShowOtherPrograms = c.ShowOtherPrograms,
                    BrowserScriptSeenUtc = c.BrowserScriptSeenUtc,
                    HdrPlayerPaths = c.HdrPlayerPaths,
                    LauncherGames = c.LauncherGames,
                    AppRules = c.AppRules,                     // the Apps tab (like the games: your list, not a setting)
                    QuickExitCounts = c.QuickExitCounts,
                    LastUpdateCheckUtc = c.LastUpdateCheckUtc,
                    LastNotifiedVersion = c.LastNotifiedVersion
                };
                Normalize(fresh);
                foreach (var prop in typeof(AppConfig).GetProperties())
                    if (prop.CanWrite) prop.SetValue(Config, prop.GetValue(fresh));
                InvalidateSnapshot();
                Save();
            }
            Log("CONFIG", "Settings reset to defaults (games, per-game options and the Apps tab kept).");
        }

        // ------------------------------------------------------------------
        // One-time fixes of default lists in existing config.json files (AppConfig.ConfigVersion)
        // ------------------------------------------------------------------
        private const int CurrentConfigVersion = 5;

        private void UpgradeConfig(AppConfig c)
        {
            if (c.ConfigVersion >= CurrentConfigVersion) return;
            bool existingFile = File.Exists(ConfigPath);   // false on a fresh install (nothing to move over)

            if (c.ConfigVersion < 2)
            {
                // 2.5.0 and older shipped three example games as "HDR on". Remove them unless the game is really
                // on this PC (found by a scan or added by hand) – then the user may want to keep it.
                var installed = new HashSet<string>(c.DiscoveredGames, Cmp);
                foreach (var g in c.LibraryGames)
                    foreach (var p in g.Processes) installed.Add(p);
                foreach (var p in c.ProgramPaths.Keys) installed.Add(p);

                foreach (string example in new[] { "cyberpunk2077", "eldenring", "alanwake2" })
                {
                    if (installed.Contains(example)) continue;
                    if (c.HdrGamesAndApps.RemoveAll(n => string.Equals(n, example, StringComparison.OrdinalIgnoreCase)) > 0)
                        Log("CONFIG", $"Removed the example entry '{example}' from the HDR games (not installed on this PC).");
                }

                // Premiere Pro's process is "Adobe Premiere Pro" ("premiere" never matched); mpv.net and MPC-BE players
                AddMissing(c.ForceSdrApps, "adobe premiere pro", "afterfx");
                AddMissing(c.LocalVideoPlayers, "mpvnet", "mpc-be64");
            }

            if (c.ConfigVersion < 3)
            {
                // Kodi: only used by "Videos & RTX Video HDR" (detected while it plays sound, so music in full screen
                // also counts – switch that off manually if needed)
                AddMissing(c.LocalVideoPlayers, "kodi");
            }

            if (c.ConfigVersion < 4)
            {
                // 2.6: the program lists (browsers, video players, SDR apps, apps that block HDR) become the Apps tab
                var rules = AppCatalog.FromLegacy(c);
                if (existingFile)
                {
                    // A default program that is missing from the old lists was removed by hand: keep it untouched
                    // (otherwise the Apps tab would add it again as soon as it finds the program installed)
                    foreach (var kv in AppCatalog.DefaultRules())
                        if (!rules.ContainsKey(kv.Key))
                            rules[kv.Key] = new AppRule { Category = kv.Value.Category, Mode = AppModes.Off };
                }
                c.AppRules = rules;
                c.DriveScanIntervalDays = c.AutoScanDrivesOnStartup ? 7 : -1;
                if (existingFile)
                    Log("CONFIG", $"Program lists moved to the new Apps tab ({rules.Count} programs). " +
                                  "New: HDR that you or another program turned on is no longer switched back to SDR.");
            }

            if (c.ConfigVersion < 5)
            {
                // 2.6.1: cs2 and valorant were shipped as "Never HDR" examples – the user never chose them, and since
                // the list was hidden they could not be undone. Remove the examples (the user can exclude them again).
                foreach (string example in new[] { "cs2", "valorant" })
                    if ((c.IgnoredGames?.RemoveAll(n => string.Equals(n, example, StringComparison.OrdinalIgnoreCase)) ?? 0) > 0 && existingFile)
                        Log("CONFIG", $"Removed the example entry '{example}' from the \"Never HDR\" games.");
            }

            c.ConfigVersion = CurrentConfigVersion;
        }

        private static void AddMissing(List<string> list, params string[] names)
        {
            foreach (string name in names)
                if (!list.Contains(name, Cmp)) list.Add(name);
        }

        /// <summary>
        /// One-time move of config.json from the exe folder (versions up to 2.3) to %AppData%\SmartHdrManager.
        /// The old file is renamed to config.json.old so it is clear which file is used.
        /// </summary>
        private void MigrateLegacyConfig()
        {
            if (IsPortable || File.Exists(ConfigPath) || !File.Exists(LegacyConfigPath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.Copy(LegacyConfigPath, ConfigPath);
                Log("CONFIG", $"Settings moved to {ConfigPath}.");
                try { File.Move(LegacyConfigPath, LegacyConfigPath + ".old", overwrite: true); } catch { }   // read-only folder: keep it
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Old settings could not be moved ({ex.Message}).");
            }
        }

        private static long Now => Environment.TickCount64;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------
        public void Start()
        {
            Log("INFO", "Smart HDR Manager started – monitoring active.");

            // Settings › General › Scan drives automatically (manual "Scan games" still works any time)
            if (IsDriveScanDue(atStartup: true)) StartAutoScan(StartupScanDelayMs);
            _nextScanDueCheckMs = Now + ScanDueCheckMs;   // later checks: once an hour on the engine thread (Tick)
            MonitorsChanged();                             // remember the connected monitors (for display events)

            // Game library (Steam / Epic / GOG / Xbox): quick refresh in the background at every start
            // (known games reuse their cached executables, so this mostly reads small manifest files)
            var libraryThread = new Thread(() =>
            {
                Thread.Sleep(3000);
                if (!_stop) RefreshLibrary(fullRescan: false);
            })
            { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "GameLibrary" };
            libraryThread.Start();

            _thread = new Thread(Run) { IsBackground = true, Name = "HdrEngine" };
            _thread.Start();
        }

        /// <summary>
        /// True when the automatic drive scan should run: -1 = never, 0 = at every start, N = when the last scan is more
        /// than N days old (checked at startup and once an hour while the app runs).
        /// </summary>
        private bool IsDriveScanDue(bool atStartup)
        {
            lock (Sync)
            {
                int days = Config.DriveScanIntervalDays;
                if (days < 0) return false;
                if (days == 0) return atStartup;
                return Config.LastDriveScanUtc == null || DateTime.UtcNow - Config.LastDriveScanUtc.Value > TimeSpan.FromDays(days);
            }
        }

        /// <summary>
        /// Starts the automatic drive scan in the background: low priority, delayed, and never while a game or a
        /// full-screen app runs (it waits until that is closed). Only one at a time.
        /// </summary>
        private void StartAutoScan(int delayMs)
        {
            if (Interlocked.Exchange(ref _autoScanRunning, 1) == 1) return;
            var scanThread = new Thread(() =>
            {
                try
                {
                    for (int waited = 0; waited < delayMs && !_stop; waited += 1000) Thread.Sleep(1000);
                    bool deferLogged = false;
                    while (!_stop && IsGameOrFullscreenActive(out string what))
                    {
                        if (!deferLogged)
                        {
                            Log("SCAN", $"Automatic drive scan postponed – {what} is running. It starts once it is closed.");
                            deferLogged = true;
                        }
                        for (int i = 0; i < AutoScanRecheckMs / 1000 && !_stop; i++) Thread.Sleep(1000);
                    }
                    if (!_stop) ScanDrives(automatic: true);
                }
                catch (Exception ex)
                {
                    Log("ERROR", $"Automatic drive scan failed: {ex.Message}");
                }
                finally
                {
                    Volatile.Write(ref _autoScanRunning, 0);
                }
            })
            { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "GameScan" };
            scanThread.Start();
        }

        /// <summary>True when the set of connected monitors changed since the last call (first call: false).</summary>
        private bool MonitorsChanged()
        {
            string signature;
            try
            {
                signature = string.Join("|", HdrController.ListMonitors().Select(m => m.DevicePath)
                                                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                return true;   // unknown: treat it like a monitor change (as before 2.6)
            }
            lock (_monitorSignatureLock)
            {
                bool changed = _monitorSignature != null && !string.Equals(signature, _monitorSignature, StringComparison.OrdinalIgnoreCase);
                _monitorSignature = signature;
                return changed;
            }
        }

        /// <summary>Raised (on a background thread) after the game library was refreshed.</summary>
        public event EventHandler? LibraryChanged;

        private readonly object _libraryLock = new();

        public void RefreshLibrary(bool fullRescan)
        {
            lock (_libraryLock)   // startup refresh, startup scan and the Scan button never run at the same time
            try
            {
                Dictionary<string, LibraryGame>? previous = null;
                HashSet<string> apps;
                lock (Sync)
                {
                    // Copies: the scan runs outside the lock, while the engine thread may add a learned launcher game
                    if (!fullRescan)
                        previous = (Config.LibraryGames ?? new List<LibraryGame>())
                            .GroupBy(g => g.Id, Cmp).ToDictionary(g => g.Key, g => CopyOf(g.First()), Cmp);
                    apps = new HashSet<string>((Config.AppRules ?? new Dictionary<string, AppRule>()).Keys, Cmp);   // Apps tab: not games
                }

                var games = GameLibraryScanner.Scan(previous, msg => Log("SCAN", msg), apps);
                var knownPaths = GameLibraryScanner.FindKnownExePaths();
                int iconsFound = 0;
                lock (Sync)
                {
                    MergeLearnedLaunchers(games);
                    // A Game Bar suggestion for a game process learned from its launcher would list the game twice
                    var covered = new HashSet<string>(games.Where(g => g.Source != "Game Bar").SelectMany(g => g.Processes), Cmp);
                    games.RemoveAll(g => g.Source == "Game Bar" && g.Processes.Any(covered.Contains));
                    CarryOverGameSettings(Config.LibraryGames ?? new List<LibraryGame>(), games);
                    Config.LibraryGames = games;
                    InvalidateSnapshot();

                    // Icons for programs outside the libraries (and library games without an exe path):
                    // take exe locations Windows already knows (Game Bar list, program caches, installed apps)
                    var wanted = Config.HdrGamesAndApps
                        .Concat(Config.DiscoveredGames)
                        .Concat(games.Where(g => g.ExePath == null).SelectMany(g => g.Processes))
                        .Distinct(Cmp);
                    foreach (string proc in wanted)
                    {
                        bool known = Config.ProgramPaths.TryGetValue(proc, out var current) && File.Exists(current);
                        if (!known && knownPaths.TryGetValue(proc, out var found))
                        {
                            Config.ProgramPaths[proc] = found;
                            iconsFound++;
                        }
                    }
                    Save();
                }
                if (iconsFound > 0) Log("SCAN", $"Program locations found for {iconsFound} program(s) (icons).");
                Log("SCAN", $"Game library: {games.Count} games found (Steam, Epic, GOG, Xbox, Ubisoft, EA, Rockstar, Battle.net, Amazon, itch.io, Riot, Heroic, Game Bar).");
                LibraryChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Game library could not be read: {ex.Message}");
            }
        }

        private static LibraryGame CopyOf(LibraryGame g) => new()
        {
            Id = g.Id, Name = g.Name, Source = g.Source, Processes = new List<string>(g.Processes), ExePath = g.ExePath, ScanVersion = g.ScanVersion
        };

        /// <summary>Adds the game processes learned from launchers (LauncherGames) to their library games. Caller holds Sync.</summary>
        private void MergeLearnedLaunchers(List<LibraryGame> games)
        {
            var learned = Config.LauncherGames;
            if (learned == null || learned.Count == 0) return;
            foreach (var g in games)
            {
                foreach (string launcher in g.Processes.ToList())
                {
                    if (!learned.TryGetValue(launcher, out var children)) continue;
                    foreach (string child in children)
                        if (!g.Processes.Contains(child, Cmp)) g.Processes.Add(child);
                }
            }
        }

        /// <summary>
        /// A rescan can detect other exes for a game than before (2.5.2: launchers are skipped, several exes per game).
        /// HDR on/off and the game options are stored per process name, so carry them over to the new names – otherwise
        /// a game that was turned on would suddenly show as off, plus a stray row for the old launcher. "Never HDR" and
        /// the game options are carried over the same way. Caller holds Sync.
        /// </summary>
        private void CarryOverGameSettings(List<LibraryGame> oldGames, List<LibraryGame> newGames)
        {
            var oldById = oldGames.GroupBy(g => g.Id, Cmp).ToDictionary(g => g.Key, g => g.First(), Cmp);
            var hdr = new HashSet<string>(Config.HdrGamesAndApps, Cmp);
            var ignored = new HashSet<string>(Config.IgnoredGames, Cmp);
            var stillUsed = new HashSet<string>(newGames.SelectMany(g => g.Processes), Cmp);
            bool changed = false;

            foreach (var game in newGames)
            {
                if (!oldById.TryGetValue(game.Id, out var old)) continue;
                var added = game.Processes.Where(p => !old.Processes.Contains(p, Cmp)).ToList();
                var removed = old.Processes.Where(p => !game.Processes.Contains(p, Cmp)).ToList();
                if (added.Count == 0 && removed.Count == 0) continue;

                bool wasOn = old.Processes.Any(hdr.Contains);
                if (wasOn)
                {
                    foreach (string p in added)
                        if (!ignored.Contains(p) && hdr.Add(p)) { Config.HdrGamesAndApps.Add(p); changed = true; }

                    // The old exe (e.g. a launcher) was only in the HDR list because of this game. Keep it if another
                    // game uses it or it is a learned launcher (it then switches HDR on before the game starts).
                    foreach (string p in removed)
                    {
                        if (stillUsed.Contains(p) || Config.LauncherGames.ContainsKey(p)) continue;
                        if (Config.HdrGamesAndApps.RemoveAll(n => n.Equals(p, StringComparison.OrdinalIgnoreCase)) > 0)
                        {
                            hdr.Remove(p);
                            changed = true;
                        }
                    }
                }

                // "Never HDR" follows the game too: a new exe must not bring an excluded game back to the list
                if (!wasOn && old.Processes.Any(ignored.Contains))
                {
                    foreach (string p in added)
                        if (ignored.Add(p)) { Config.IgnoredGames.Add(p); changed = true; }
                }

                // Game options (start delay, keep HDR after exit) follow the game as well
                var options = old.Processes.Select(p => Config.PerGameOptions.TryGetValue(p, out var o) ? o : null)
                                           .FirstOrDefault(o => o != null && !o.IsDefault);
                if (options != null)
                {
                    foreach (string p in added)
                    {
                        if (Config.PerGameOptions.ContainsKey(p)) continue;
                        Config.PerGameOptions[p] = new GameOptions
                        {
                            HdrDelaySeconds = options.HdrDelaySeconds,
                            KeepHdrAfterExitSeconds = options.KeepHdrAfterExitSeconds
                        };
                        changed = true;
                    }
                }

                Log("SCAN", $"'{game.Name}': now watching {string.Join(", ", game.Processes.Select(p => p + ".exe"))} " +
                            $"(before: {string.Join(", ", old.Processes.Select(p => p + ".exe"))})" +
                            (wasOn ? " – HDR setting kept." : "."));
            }

            if (changed) Config.HdrGamesAndApps.Sort(Cmp);
        }

        // ------------------------------------------------------------------
        // Manual override (tray menu / the override shortcut)
        // ------------------------------------------------------------------
        public HdrOverride OverrideMode => (HdrOverride)_overrideMode;

        /// <summary>Force HDR on/off. duration null = until turned back. HdrOverride.None = back to automatic.</summary>
        public void SetOverride(HdrOverride mode, TimeSpan? duration)
        {
            long until = mode != HdrOverride.None && duration.HasValue ? Now + (long)duration.Value.TotalMilliseconds : 0;
            lock (_overrideLock)
            {
                Interlocked.Exchange(ref _overrideUntilMs, until);
                _overrideMode = (int)mode;
            }
            Interlocked.Exchange(ref _overrideChanged, 1);

            string length = duration.HasValue ? $"for {FormatDuration(duration.Value)}" : "until turned back";
            Log("MANUAL", mode switch
            {
                HdrOverride.ForceHdr => $"HDR forced ON {length}.",
                HdrOverride.ForceSdr => $"HDR forced OFF (SDR) {length}.",
                _ => "Back to automatic HDR switching."
            });
            _wake.Set();
        }

        /// <summary>the override shortcut: automatic -> HDR on -> SDR -> automatic. Returns the new mode.</summary>
        public HdrOverride CycleOverride()
        {
            var next = OverrideMode switch
            {
                HdrOverride.None => HdrOverride.ForceHdr,
                HdrOverride.ForceHdr => HdrOverride.ForceSdr,
                _ => HdrOverride.None
            };
            SetOverride(next, null);
            return next;
        }

        private static string FormatDuration(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{t.TotalHours:0.#} hour{(t.TotalHours >= 2 ? "s" : "")}" : $"{t.TotalMinutes:0} minutes";

        public List<MonitorInfo> GetMonitors() => HdrController.ListMonitors();

        public void Stop()
        {
            _stop = true;
            _wake.Set();
            bool finished = true;
            try { finished = _thread == null || _thread.Join(2000); } catch { }
            if (finished) RestoreSdrOnExit();   // never while the engine thread might still switch
            lock (Sync) Save();
        }

        /// <summary>
        /// Exit: if this app switched HDR on, go back to SDR (2.5.2 – before, HDR stayed on after "Exit"). HDR that was
        /// on before the app started or was switched by something else is left alone, and so is HDR for a game that is
        /// still running (switching now would interrupt it).
        /// </summary>
        private void RestoreSdrOnExit()
        {
            try
            {
                if (!_hdrAny || !_hdrOnByApp) return;
                string? busy = _runningGame != null ? $"'{_runningGame}' is still running"
                    : _externalHoldProcess != null ? $"'{_externalHoldProcess}' is still running"
                    : Now < Interlocked.Read(ref _launchHoldUntilMs) ? "a game started with \"Launch with HDR\" is still running"
                    : null;
                if (busy != null)
                {
                    Log("SDR", $"App closed – HDR left on because {busy}.");
                    return;
                }

                var target = Target(GetSnapshot());
                Log("SDR", $"App closed – switching back to SDR ({target.Describe()}), HDR was switched on by this app.");
                if (!HdrController.SetHdrState(false, target)) Log("ERROR", HdrController.LastError);
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Could not switch back to SDR on exit: {ex.Message}");
            }
        }

        /// <summary>Runs an action on the engine thread (used for hotkeys, so all display switching stays on one thread).</summary>
        public void Post(Action action)
        {
            _posted.Enqueue(action);
            _wake.Set();
        }

        /// <summary>
        /// "Launch with HDR": a launcher process (SmartHdrManager.exe --launch ...) asks to hold HDR while its game starts
        /// and runs. It repeats the request every 2 s; the hold ends a few seconds after the last one. Only holds HDR –
        /// nothing is started or read from the request, so any program sending it can at most keep HDR on.
        /// </summary>
        public void HoldHdrForLaunch()
        {
            Interlocked.Exchange(ref _launchHoldUntilMs, Now + LaunchHoldMs);
            _wake.Set();
        }

        /// <summary>"Launch with HDR": the launcher's game / player was closed – end the hold and go back to SDR now.</summary>
        public void ReleaseLaunchHold()
        {
            Interlocked.Exchange(ref _launchHoldUntilMs, 0);
            Interlocked.Exchange(ref _launchReleased, 1);
            _wake.Set();
        }

        /// <summary>Forces the HDR state to be re-read on the next tick (display change, resume from sleep).</summary>
        public void InvalidateHdrState(bool resumed = false)
        {
            // HDR lost right after a monitor was plugged in / switched on, or after waking up, was not turned off by you:
            // switch it on again if needed. Windows also reports "display settings changed" when HDR itself is
            // toggled (e.g. Win+Alt+B in a game), so only a changed monitor list counts – not every event.
            if (resumed || MonitorsChanged()) Interlocked.Exchange(ref _displayEventMs, Now);
            Interlocked.Exchange(ref _forceHdrQuery, 1);
            Interlocked.Exchange(ref _displaysChanged, 1);   // video windows may now be on another display
            _wake.Set();
        }

        public void HotkeyAddCurrent() => Post(() =>
        {
            var (p, _, _) = WindowDetector.GetActiveWindowState();
            AddGame(p, fromHotkey: true, WindowDetector.GetForegroundExePath());
        });

        public void HotkeyIgnoreCurrent() => Post(() =>
        {
            var (p, _, _) = WindowDetector.GetActiveWindowState();
            IgnoreOrSkipVideo(p);
        });

        private void Run()
        {
            while (!_stop)
            {
                int waitMs = 100;
                try
                {
                    while (_posted.TryDequeue(out var action))
                    {
                        try { action(); }
                        catch (Exception ex) { Log("ERROR", $"Action failed: {ex.Message}"); }
                    }

                    var s = GetSnapshot();
                    waitMs = s.TickMs;
                    Tick(s);
                }
                catch (Exception ex)
                {
                    Log("ERROR", $"Engine error: {ex.Message}");
                    waitMs = 1000;
                }

                _wake.WaitOne(waitMs);   // returns early when work is posted or the display changes
            }
        }

        private Snapshot GetSnapshot()
        {
            var s = _snapshot;
            if (s != null) return s;
            lock (Sync)
            {
                s = _snapshot ??= new Snapshot(Config);
                return s;
            }
        }

        /// <summary>Call after every configuration change (while holding <see cref="Sync"/>).</summary>
        private void InvalidateSnapshot()
        {
            _snapshot = null;
            _wake.Set();
        }

        // ------------------------------------------------------------------
        // One monitoring tick
        // ------------------------------------------------------------------
        private void Tick(Snapshot s)
        {
            long now = Now;

            // Configuration changed: re-check running games right away
            if (!ReferenceEquals(s, _lastSnapshot))
            {
                _lastSnapshot = s;
                _nextProcessScanMs = 0;
            }

            if ((PollHotkeys || PollOverrideHotkey) && s.HotkeyMode != HotkeySettings.Off) PollHotkeysFallback(now, s.HotkeyMode);

            var (procName, title, isFullscreen, procId) = WindowDetector.GetActiveWindowInfo();
            _foregroundProcess = procName;
            _foregroundFullscreen = isFullscreen;
            _foregroundElsewhere = s.HdrGames.Contains(procName) && s.GameFolders.ContainsKey(procName) &&
                                   !IsGameLocation(s, procName, procId);
            NoteBrowserScript(s, procName, title);

            RefreshHdrState(s, now);

            if (now >= _nextProcessScanMs)
            {
                _runningGame = FindRunningHdrGame(s, out string? runningPath, out _runningVideoBlocker, out _runningAllBlocker);
                _nextProcessScanMs = now + ProcessScanIntervalMs;
                if (_runningGame != null && runningPath != null) RememberProgramPath(_runningGame, runningPath);
                TrackLauncherFollow(s, now);
            }

            // Automatic drive scan due (daily / weekly / monthly setting)? Checked once an hour.
            if (now >= _nextScanDueCheckMs)
            {
                _nextScanDueCheckMs = now + ScanDueCheckMs;
                if (!IsScanning && Volatile.Read(ref _autoScanRunning) == 0 && IsDriveScanDue(atStartup: false))
                    StartAutoScan(0);
            }

            // Manual override has priority over everything (also over "pause")
            HdrOverride mode;
            long until;
            bool expired = false;
            lock (_overrideLock)
            {
                mode = (HdrOverride)_overrideMode;
                until = Interlocked.Read(ref _overrideUntilMs);
                if (mode != HdrOverride.None && until > 0 && now >= until)
                {
                    // expire only this exact override (a new one chosen meanwhile is not touched: same lock)
                    _overrideMode = (int)HdrOverride.None;
                    Interlocked.Exchange(ref _overrideUntilMs, 0);
                    mode = HdrOverride.None;
                    until = 0;
                    expired = true;
                }
            }
            if (expired)
            {
                Log("MANUAL", "Override time is up – back to automatic HDR switching.");
                Interlocked.Exchange(ref _overrideChanged, 1);
            }

            // After any override change, start the automatic logic fresh (no stale timers or "game closed" events)
            if (Interlocked.Exchange(ref _overrideChanged, 0) == 1)
            {
                _sdrFallbackAtMs = -1;
                _holdHdrUntilMs = 0;
                _activeHdrGameProcess = null;
                _delayGame = null;
                _handOffDemand = null;   // you decided: the automatic logic starts fresh
                _restoreHdr = false;
                _externalEnded = false;
            }

            _waitingGame = null;
            _holdMsLeft = 0;
            _blockedBy = null;
            if (mode == HdrOverride.None && !Paused)
                Decide(s, now, procName, title, isFullscreen);
            else if (mode != HdrOverride.None)
                ApplyOverride(s, now, mode);

            Status = new EngineStatus
            {
                HdrOn = _hdrAny,
                Paused = Paused,
                ActiveProcess = procName,
                IsFullscreen = isFullscreen,
                RunningGame = _runningGame,
                Override = mode,
                OverrideRemainingMs = mode != HdrOverride.None && until > 0 ? Math.Max(0, until - now) : -1,
                WaitingForGame = _waitingGame,
                WaitingSecondsLeft = (int)Math.Ceiling(_waitingMsLeft / 1000.0),
                HoldSecondsLeft = (int)Math.Ceiling(_holdMsLeft / 1000.0),
                Video = _videoActive ? _activeVideoText : null,
                BlockedBy = _blockedBy,
                LaunchHold = now < Interlocked.Read(ref _launchHoldUntilMs),
                FollowingLauncher = _followLauncher
            };
            Volatile.Write(ref _videoMenuState, _skippedHwnd != IntPtr.Zero ? 2 : _videoActive ? 1 : 0);
        }

        private void ApplyOverride(Snapshot s, long now, HdrOverride mode)
        {
            bool canAttempt = now - _lastAttemptMs >= SwitchSpacingMs && now >= _cooldownUntilMs;
            ResetVideo();   // a video must not take over again the moment the override ends
            if (SyncTarget(s, now, null, mode == HdrOverride.ForceHdr, canAttempt)) return;
            if (!canAttempt) return;

            if (mode == HdrOverride.ForceHdr && !_hdrAll)
                TurnHdrOn("Manual override", Target(s));
            else if (mode == HdrOverride.ForceSdr && _hdrAny)
                TurnHdrOff("Manual override", Target(s), s.ReloadProfile);
        }

        private void RefreshHdrState(Snapshot s, long now)
        {
            bool forced = Interlocked.Exchange(ref _forceHdrQuery, 0) == 1;
            var target = Target(s);
            if (!forced && now < _nextHdrQueryMs && ReferenceEquals(_queriedMonitors, target))
                return;

            // Keep the last known state if Windows could not be queried
            if (HdrController.QueryHdrState(target, out bool any, out bool all))
            {
                if (!_apiLogged && HdrController.HdrApiName != "not determined yet")
                {
                    _apiLogged = true;
                    Log("INFO", $"Display control: {HdrController.HdrApiName} – {WindowsVersion.Describe()}.");
                }
                bool sameTarget = ReferenceEquals(_queriedMonitors, target);
                if (!_firstHdrQueryDone || !sameTarget)
                {
                    // First look (or other monitors): this is the starting point, nothing was switched by anyone yet
                    _expectAny = any;
                    _switchConfirmed = true;
                    if (!_firstHdrQueryDone && any && !s.KeepDesktopSdr)
                        Log("HDR", "HDR is already on – it is left as it is (the app only switches back HDR that it turned on itself).");
                }

                // HDR changed although this app did not switch it: a game, Win+Alt+B, Windows Settings or another tool did
                // it. Logged with the window in front, so reports show which games switch HDR themselves. Right after an
                // own switch, Windows may still report the old state for a moment: that is not a change by someone else
                // (until the new state has been seen once, or 8 s have passed).
                bool towardOwn = _expectAny.HasValue && any == _expectAny.Value;
                if (towardOwn) _switchConfirmed = true;
                if (_firstHdrQueryDone && any != _hdrAny && sameTarget && !towardOwn &&
                    (_switchConfirmed || now - _lastAttemptMs > ExternalChangeGraceMs))
                {
                    string front = string.IsNullOrEmpty(_foregroundProcess) ? "unknown" : _foregroundProcess;
                    Log("INFO", $"Windows HDR turned {(any ? "ON" : "OFF")} without this app – by a game, Win+Alt+B, Windows " +
                                $"Settings or a display reconnect (window in front: {front}).");
                    _hdrOnByApp = false;   // not ours any more: never switched back by this app, left as it is on exit
                    _restoreHdr = false;   // you decided: nothing to put back later
                    _hdrOnForGame = null;  // crash hint: closing the game now is not a crash after the HDR switch
                    _expectAny = any;
                    _switchConfirmed = true;

                    // A full-screen game turned HDR on itself (its own HDR option): switching it back off a few
                    // seconds later would fight the game – flicker, black screens. Keep HDR while that game runs, and
                    // switch back once it has closed (some games leave HDR on). Not for video players (madVR, MPC …)
                    // or browsers: they handle HDR themselves and are simply left alone.
                    if (any && _foregroundFullscreen && front != "unknown" && front != "explorer" && front != "smarthdrmanager" &&
                        !s.AllVideoApps.Contains(front) && !s.ForceSdr.Contains(front) && !s.Ignored.Contains(front))
                    {
                        _externalHoldProcess = front;
                        _externalEnded = false;
                        Log("HDR", $"'{front}' switched HDR on itself – keeping HDR on while it runs.");
                    }
                    else if (!Paused && OverrideMode == HdrOverride.None && !s.KeepDesktopSdr)
                    {
                        // Hands off until the next game / video (or until the current one ends). HDR lost right after a
                        // display change or wake-up is not your decision: that one is switched on again if needed.
                        bool displayEvent = now - Interlocked.Read(ref _displayEventMs) < DisplayEventGraceMs;
                        if (any || !displayEvent)
                        {
                            _handOffDemand = _lastDemand;
                            if (any)
                                Log("HDR", "HDR was turned on by you or another program – it stays on. This app only switches back " +
                                           "HDR that it turned on itself.");
                            else if (_lastDemand != "none")
                                Log("HDR", $"HDR was turned off by you or another program – not switched on again for {DescribeDemand(_lastDemand)} " +
                                           "until it ends (or the next game / video starts).");
                        }
                    }
                }
                if (!any) _externalHoldProcess = null;

                // e.g. switched on by hand (Win+Alt+B). At app start (first query) HDR counts as "on for long already".
                if (any && !_hdrAny && _hdrOnSinceMs == 0) _hdrOnSinceMs = _firstHdrQueryDone ? now : 1;
                _firstHdrQueryDone = true;
                if (!any) _hdrOnSinceMs = 0;
                _hdrAny = any;
                _hdrAll = all;

                // Windows reported success, but a display did not actually switch on: count it as a failure,
                // so a monitor that refuses HDR cannot cause an endless retry loop (cooldown after 3 tries).
                if (_verifyAllOn && !forced)
                {
                    _verifyAllOn = false;
                    if (!all)
                    {
                        Log("ERROR", "HDR was switched on, but not every HDR-capable display reports HDR as active.");
                        RegisterFailure(now);
                    }
                    else
                    {
                        _consecutiveFailures = 0;
                    }
                }
            }
            _queriedMonitors = target;
            _nextHdrQueryMs = now + HdrRequeryIntervalMs;
        }

        private void PollHotkeysFallback(long now, string mode)
        {
            if (now - _lastPolledHotkeyMs < 2000) return;

            if (PollHotkeys && WindowDetector.IsHotkeyPressed(mode, 0x48))          // H
            {
                _lastPolledHotkeyMs = now;
                var (p, _, _) = WindowDetector.GetActiveWindowState();
                AddGame(p, fromHotkey: true, WindowDetector.GetForegroundExePath());
            }
            else if (PollHotkeys && WindowDetector.IsHotkeyPressed(mode, 0x49))     // I
            {
                _lastPolledHotkeyMs = now;
                var (p, _, _) = WindowDetector.GetActiveWindowState();
                IgnoreOrSkipVideo(p);
            }
            else if (PollOverrideHotkey && WindowDetector.IsHotkeyPressed(mode, 0x4F))   // O
            {
                _lastPolledHotkeyMs = now;
                CycleOverride();
            }
        }

        private void Decide(Snapshot s, long now, string procName, string title, bool isFullscreen)
        {
            // A recording / streaming app set to "No HDR" (Apps tab) is running: nothing switches HDR on automatically
            // (games included), and HDR goes back to SDR right away when it starts, so a recording is never made in HDR
            // by mistake. A manual override (tray menu, shortcut, --force-hdr) still works: it never reaches Decide.
            if (_runningAllBlocker != null)
            {
                string blocker = _runningAllBlocker;
                _blockedBy = blocker;
                if (!string.Equals(_allBlockLogged, blocker, StringComparison.OrdinalIgnoreCase))
                {
                    _allBlockLogged = blocker;
                    Log("HDR", $"Automatic HDR blocked – '{blocker}' is running (Apps › Recording & streaming: No HDR).");
                }
                ResetVideo();
                _delayGame = null;
                _activeHdrGameProcess = null;
                _holdHdrUntilMs = 0;
                _sdrFallbackAtMs = -1;
                _launchEnded = false;
                _externalEnded = false;
                if (HandedOff(s, "block:" + blocker)) return;   // HDR turned on by you while it runs: your decision
                if (_hdrAny && now - _lastAttemptMs >= SwitchSpacingMs && now >= _cooldownUntilMs)
                {
                    bool wasOurs = _hdrOnByApp || s.KeepDesktopSdr;
                    TurnHdrOff($"'{blocker}' is running (no HDR while it runs)", Target(s), s.ReloadProfile);
                    if (!wasOurs && !_hdrAny) RememberRestore(blocker);
                }
                return;
            }
            if (_allBlockLogged != null)
            {
                Log("HDR", $"'{_allBlockLogged}' closed – automatic HDR switching active again.");
                _allBlockLogged = null;
            }

            bool shouldEnableHdr = EvaluateHdrRequirement(s, procName, title, isFullscreen, _foregroundElsewhere, out string reason);
            bool isForceSdr = s.ForceSdr.Contains(procName);
            if (isForceSdr) _forceSdrSeenMs = now;

            // "Launch with HDR": a game is being started through SmartHdrManager.exe --launch. Hold HDR from before the
            // game starts until a few seconds after it closes (games that only check HDR at startup then see it on).
            bool launchHold = now < Interlocked.Read(ref _launchHoldUntilMs);
            if (Interlocked.Exchange(ref _launchReleased, 0) == 1)
            {
                launchHold = false;
                if (_launchHoldLogged) _launchEnded = true;   // only if this hold really kept HDR on
            }
            if (launchHold)
            {
                if (!_launchHoldLogged)
                {
                    _launchHoldLogged = true;
                    Log("HDR", "Launch with HDR: switching HDR on before the game or player starts.");
                }
                if (!shouldEnableHdr && !isForceSdr)
                {
                    shouldEnableHdr = true;
                    reason = "Launch with HDR";
                }
            }
            else if (_launchHoldLogged)
            {
                _launchHoldLogged = false;
                Log("HDR", "Launch with HDR: the game or player was closed.");
            }

            // A full-screen game switched HDR on by itself: keep HDR on until that game has closed, then switch back
            if (_externalHoldProcess != null)
            {
                if (now >= _nextExternalCheckMs)
                {
                    _nextExternalCheckMs = now + 2000;
                    if (!IsProcessRunning(_externalHoldProcess))
                    {
                        Log("HDR", $"'{_externalHoldProcess}' closed – switching back the HDR it turned on.");
                        _externalEndedWhat = _externalHoldProcess;
                        _externalEnded = true;
                        _externalHoldProcess = null;
                    }
                }
                if (_externalHoldProcess != null && !shouldEnableHdr)
                {
                    shouldEnableHdr = true;
                    reason = $"'{_externalHoldProcess}' switched HDR on itself";
                }
            }

            // A watched game closed within seconds (probably a launcher): keep HDR on while waiting for the real game
            if (_followLauncher != null && _followHoldsHdr && !shouldEnableHdr && !isForceSdr)
            {
                shouldEnableHdr = true;
                reason = $"waiting for the game started by '{_followLauncher}'";
            }

            // Early detection when a selected HDR game process is running
            if (!shouldEnableHdr && s.EnableOnProcessStart && !isForceSdr && _runningGame != null)
            {
                shouldEnableHdr = true;
                reason = $"HDR game started/running ({_runningGame})";
            }

            // HDR is on and a browser window still shows the HDR token, but something else has focus
            // (a browser pop-up/menu, another app, a second monitor): keep HDR on instead of flickering to SDR.
            string? backgroundBrowser = null;
            if (!shouldEnableHdr && _hdrAny && s.KeepBrowserHdr && s.BrowserToken.Length > 0 && !(s.VideoAuto && s.VideoBrowsers) &&
                !isForceSdr && !s.Ignored.Contains(procName))
            {
                if (now >= _nextBackgroundCheckMs)
                {
                    _backgroundHdrBrowser = WindowDetector.FindBrowserWindowWithToken(s.Browsers, s.BrowserToken, requireFullscreen: !s.BrowserWindowed);
                    _nextBackgroundCheckMs = now + 500;
                }
                if (_backgroundHdrBrowser != null)
                {
                    shouldEnableHdr = true;
                    reason = $"YouTube HDR video still playing ({_backgroundHdrBrowser})";
                    backgroundBrowser = _backgroundHdrBrowser;
                }
            }
            else
            {
                _nextBackgroundCheckMs = 0;   // look again immediately the next time it is needed
            }

            // Per-game start delay: wait N seconds after the game appears before switching HDR on
            string? hdrGame = s.HdrGames.Contains(procName) && !_foregroundElsewhere ? procName : _runningGame;
            if (hdrGame == null)
            {
                _delayGame = null;
            }
            else if (shouldEnableHdr && !_hdrAny && !launchHold &&
                     s.GameOptions.TryGetValue(hdrGame, out var startOptions) && startOptions.HdrDelaySeconds > 0)
            {
                if (!string.Equals(_delayGame, hdrGame, StringComparison.OrdinalIgnoreCase))
                {
                    _delayGame = hdrGame;
                    _delaySinceMs = now;
                    Log("HDR", $"'{hdrGame}' started – switching to HDR in {startOptions.HdrDelaySeconds} s (game option).");
                }
                long left = _delaySinceMs + startOptions.HdrDelaySeconds * 1000L - now;
                if (left > 0)
                {
                    _waitingGame = hdrGame;
                    _waitingMsLeft = left;
                    return;
                }
            }

            if (_runningGame != null)
                _activeHdrGameProcess = _runningGame;

            // Multi-monitor hint (once per game start): the game window is on a monitor that is not selected under
            // Settings › Monitors, so HDR gets switched on the other screen and the game does not see it.
            if (hdrGame == null)
            {
                _monitorCheckedGame = null;
            }
            else if (shouldEnableHdr && s.HdrGames.Contains(procName) && !_foregroundElsewhere &&
                     !string.Equals(_monitorCheckedGame, procName, StringComparison.OrdinalIgnoreCase))
            {
                _monitorCheckedGame = procName;
                if (HdrController.ResolveVideoMonitor(WindowDetector.ForegroundMonitor(), s.Monitors, out string problem) == null &&
                    problem.Contains("not switched on", StringComparison.OrdinalIgnoreCase))
                {
                    Log("HINT", $"'{procName}' runs on a monitor that is not selected for HDR switching, so HDR is switched on " +
                                "another monitor. Select the game's monitor in Settings › Monitors.");
                }
            }

            bool canAttempt = now - _lastAttemptMs >= SwitchSpacingMs && now >= _cooldownUntilMs;

            // Video playback (RTX Video HDR / native HDR video). Games and every other HDR source come first:
            // while they want HDR, the video logic stays out of the way and the normal monitors are used.
            MonitorFilter? videoTarget = null;
            if (s.VideoAuto)
            {
                bool otherSource = shouldEnableHdr || _runningGame != null || _activeHdrGameProcess != null;
                if (EvaluateVideo(s, now, procName, otherSource, out string videoReason, out videoTarget) && !shouldEnableHdr)
                {
                    shouldEnableHdr = true;
                    reason = videoReason;
                }
                else
                {
                    videoTarget = null;
                }
            }
            else if (_videoActive || _pendingVideoKey != null)
            {
                ResetVideo();   // automation switched off in Settings
            }

            // Video HDR switches only the monitor the video is on; move HDR there (or back) if needed
            if (SyncTarget(s, now, videoTarget, shouldEnableHdr, canAttempt)) return;

            // What wants HDR right now (stable while the same game / video runs, e.g. also when you alt-tab)
            string demand;
            if (!shouldEnableHdr) demand = isForceSdr ? "sdr:" + procName : "none";
            else if (_runningGame != null) demand = "game:" + _runningGame;
            else if (s.HdrGames.Contains(procName) && !_foregroundElsewhere) demand = "game:" + procName;
            else if (launchHold) demand = "launch";
            else if (_externalHoldProcess != null) demand = "ext:" + _externalHoldProcess;
            else if (_followLauncher != null && _followHoldsHdr) demand = "follow:" + _followLauncher;
            else if (videoTarget != null) demand = "video:" + _activeVideoKey + "|" + _activeVideoName;
            else demand = "focus:" + (backgroundBrowser ?? procName);

            // HDR was changed by you / another program: hands off until the demand changes
            if (HandedOff(s, demand))
            {
                _sdrFallbackAtMs = -1;
                _videoEnded = false;
                _videoSdrRequested = false;
                _launchEnded = false;
                _externalEnded = false;
                if (_runningGame == null) _activeHdrGameProcess = null;
                return;
            }

            // The app only undoes what it did itself: HDR that you or another program turned on is never switched
            // back here (2.6). "Keep the desktop in SDR" (Settings) brings back the old behaviour.
            bool owns = _hdrOnByApp || s.KeepDesktopSdr;

            if (shouldEnableHdr)
            {
                _sdrFallbackAtMs = -1;
                _holdHdrUntilMs = 0;   // game came back during "keep HDR after exit": nothing to wait for any more
                _videoEnded = false;
                _videoSdrRequested = false;
                _launchEnded = false;
                _externalEnded = false;
                if (!_hdrAll && canAttempt)
                {
                    string? game = _runningGame ?? (s.HdrGames.Contains(procName) && !_foregroundElsewhere ? procName : null);
                    TurnHdrOn(reason, Target(s));
                    if (_hdrAll && game != null && !launchHold)
                    {
                        // Crash hint: remember when HDR came on for this game
                        _hdrOnForGame = game;
                        _hdrOnForGameAtMs = now;
                    }
                    if (videoTarget != null && _activeVideoNudge && _hdrAny)
                        ScheduleBrowserNudgeAfterHdrOn(Target(s), s.VideoApps);
                }
            }
            else if (_hdrAny)
            {
                // Per-game option: keep HDR on for a while after the game closes (e.g. quick restart, no double black screen)
                if (owns && _activeHdrGameProcess != null && _runningGame == null &&
                    s.GameOptions.TryGetValue(_activeHdrGameProcess, out var exitOptions) && exitOptions.KeepHdrAfterExitSeconds > 0)
                {
                    Log("HDR", $"'{_activeHdrGameProcess}' closed – keeping HDR on for {exitOptions.KeepHdrAfterExitSeconds} s (game option).");
                    _holdHdrUntilMs = now + exitOptions.KeepHdrAfterExitSeconds * 1000L;
                    _activeHdrGameProcess = null;
                }

                // Hold HDR: keep-after-exit, or the "minimum time in HDR" setting. Colour-critical apps still switch at once.
                long holdUntil = Math.Max(_holdHdrUntilMs,
                    s.MinimumHdrMs > 0 && _hdrOnSinceMs > 0 ? _hdrOnSinceMs + s.MinimumHdrMs : 0);
                if (owns && !isForceSdr && now < holdUntil)
                {
                    _holdMsLeft = holdUntil - now;
                    // The "return to SDR after" delay already counts during the hold, so it is not added on top
                    if (_sdrFallbackAtMs < 0) _sdrFallbackAtMs = now + s.FallbackSeconds * 1000L;
                    return;
                }

                // Instant turn-off: a monitored HDR game was running and just closed
                if (_activeHdrGameProcess != null && _runningGame == null)
                {
                    // During a failure cooldown, keep the state and switch off as soon as the cooldown ends
                    if (now >= _cooldownUntilMs)
                    {
                        string closedGame = _activeHdrGameProcess;
                        _activeHdrGameProcess = null;
                        _sdrFallbackAtMs = -1;
                        if (owns) UndoOwnHdr($"HDR game closed ({closedGame})", s);
                        else Log("HDR", $"'{closedGame}' closed – HDR stays on (it was not switched on by this app).");
                    }
                }
                else if (isForceSdr && canAttempt)
                {
                    // An app you chose in Apps › Creative apps: SDR while it is in front. HDR that was not switched on by
                    // this app comes back on afterwards.
                    _sdrFallbackAtMs = -1;
                    _videoEnded = false;
                    _videoSdrRequested = false;
                    TurnHdrOff($"Color-critical app active ({procName})", Target(s), s.ReloadProfile);
                    if (!owns && !_hdrAny) RememberRestore(procName);
                }
                else if (_externalEnded)
                {
                    // The full-screen game that switched HDR on by itself has closed (some games leave HDR on)
                    if (canAttempt)
                    {
                        _externalEnded = false;
                        _sdrFallbackAtMs = -1;
                        TurnHdrOff($"'{_externalEndedWhat}' closed (it had switched HDR on itself)", Target(s), s.ReloadProfile);
                    }
                }
                else if (_launchEnded)
                {
                    if (canAttempt)
                    {
                        _launchEnded = false;
                        _sdrFallbackAtMs = -1;
                        if (owns) UndoOwnHdr("Launch with HDR: the game or player was closed", s);
                    }
                }
                else if (_videoEnded)
                {
                    // The video revert delay already ran: no extra "return to SDR after" wait on top
                    if (canAttempt)
                    {
                        bool requested = _videoSdrRequested;
                        _videoEnded = false;
                        _videoSdrRequested = false;
                        _sdrFallbackAtMs = -1;
                        if (requested && !owns) TurnHdrOff($"Video playback ended ({_videoEndedWhat})", Target(s), s.ReloadProfile);
                        else if (owns) UndoOwnHdr($"Video playback ended ({_videoEndedWhat})", s);
                    }
                }
                else if (!owns)
                {
                    _sdrFallbackAtMs = -1;   // HDR turned on by you or another program: left alone
                }
                else if (_sdrFallbackAtMs < 0)
                {
                    _sdrFallbackAtMs = now + s.FallbackSeconds * 1000L;
                }
                else if (now >= _sdrFallbackAtMs && canAttempt)
                {
                    _sdrFallbackAtMs = -1;
                    UndoOwnHdr("No HDR content active anymore (desktop mode)", s);
                }
            }
            else
            {
                _activeHdrGameProcess = null;
                _sdrFallbackAtMs = -1;
                _videoEnded = false;
                _videoSdrRequested = false;
                _launchEnded = false;
                _externalEnded = false;

                // HDR that you had on was switched off for an SDR app or a recording app: back on once it is gone
                if (_restoreHdr && !isForceSdr && canAttempt && now - _forceSdrSeenMs >= RestoreAfterMs)
                {
                    string what = _restoreFor;
                    _restoreHdr = false;
                    TurnHdrOn($"back to HDR – it was on before '{what}'", Target(s));
                    if (_hdrAny) _hdrOnByApp = false;   // yours again: never switched back by this app
                }
            }
        }

        /// <summary>
        /// HDR was changed by something else (you, a game, another program): the app keeps its hands off until what
        /// wants HDR changes – a new game or video starts, or the current one ends. Returns true while handed off.
        /// Remembers the demand of this tick (the starting point of the next hand-off).
        /// </summary>
        private bool HandedOff(Snapshot s, string demand)
        {
            _lastDemand = demand;
            if (_handOffDemand == null) return false;
            if (!s.KeepDesktopSdr && string.Equals(demand, _handOffDemand, StringComparison.OrdinalIgnoreCase)) return true;
            _handOffDemand = null;
            Log("HDR", $"Automatic switching takes over again ({(demand == "none" ? "nothing in your lists is active any more" : DescribeDemand(demand))}).");
            return false;
        }

        /// <summary>"game:witcher3" -> "'witcher3'", "video:…" -> "the video" – for the log.</summary>
        private static string DescribeDemand(string demand)
        {
            int colon = demand.IndexOf(':');
            string kind = colon < 0 ? demand : demand.Substring(0, colon);
            string what = colon < 0 ? string.Empty : demand.Substring(colon + 1);
            return kind switch
            {
                "game" or "ext" or "focus" => $"'{what}'",
                "video" => "the video",
                "launch" => "the game started with \"Launch with HDR\"",
                "follow" => $"the game started by '{what}'",
                "sdr" => $"'{what}' (SDR app)",
                "block" => $"'{what}' (recording app)",
                _ => "this"
            };
        }

        /// <summary>
        /// HDR that was not switched on by this app has just been switched off for an SDR app / recording app (Apps tab):
        /// it is switched back on once that app is gone.
        /// </summary>
        private void RememberRestore(string what)
        {
            _restoreHdr = true;
            _restoreFor = what;
            Log("SDR", $"HDR was on before (not switched on by this app) – it comes back on when '{what}' is no longer active.");
        }

        /// <summary>
        /// The reason this app switched HDR on has ended: back to SDR. Exception: HDR was on before (yours) and was only
        /// interrupted by an SDR / recording app – then it stays on and is yours again.
        /// </summary>
        private void UndoOwnHdr(string reason, Snapshot s)
        {
            if (_restoreHdr)
            {
                _restoreHdr = false;
                _hdrOnByApp = false;
                Log("HDR", $"{reason} – HDR stays on: it was on before '{_restoreFor}' (turned on by you or another program).");
                return;
            }
            TurnHdrOff(reason, Target(s), s.ReloadProfile);
        }

        // ------------------------------------------------------------------
        // Video playback
        // ------------------------------------------------------------------
        /// <summary>
        /// True while a video holds HDR: it has played for the activation delay, and stays true for the revert delay
        /// after it stops. <paramref name="target"/> is the single monitor the video is on.
        /// </summary>
        private bool EvaluateVideo(Snapshot s, long now, string procName, bool otherSource, out string reason, out MonitorFilter? target)
        {
            reason = string.Empty;
            target = null;

            if (otherSource)
            {
                // A game (or another HDR source) holds HDR: start fresh once it is done, no "video ended" switch-off
                ResetVideo();
                return false;
            }

            string? block = VideoBlockReason(s, now, procName);
            if (block != null)
            {
                if (_videoActive)
                {
                    _videoEnded = true;   // priority rule: e.g. OBS started while a video was in HDR -> back to SDR now
                    _videoEndedWhat = block;
                }
                if (block != _videoBlockLogged && (_videoActive || _pendingVideoKey != null))
                    Log("VIDEO", $"Video HDR paused – {block}.");
                _videoBlockLogged = block;
                _videoActive = false;
                _pendingVideoKey = null;
                _videoLostAtMs = -1;
                return false;
            }
            _videoBlockLogged = null;

            if (now >= _nextVideoCheckMs)
            {
                _nextVideoCheckMs = now + VideoCheckIntervalMs;
                DetectVideo(s, now);
            }

            var c = _videoCandidate;

            // "SDR for this video": only that video is ignored. It ends when the same window shows another title (next
            // video / next file), or when the video has been gone as long as "Back to SDR after the video stops".
            if (_skippedHwnd != IntPtr.Zero)
            {
                if (c != null && c.Hwnd == _skippedHwnd)
                {
                    string name = c.VideoName;
                    if (name.Length == 0 || _skippedName.Length == 0 || name == _skippedName)
                    {
                        _skipLostAtMs = -1;
                        c = null;   // still the skipped video
                    }
                    else
                    {
                        EndSkip($"next video started ({name})");
                    }
                }
                else
                {
                    if (_skipLostAtMs < 0) _skipLostAtMs = now;
                    if (now - _skipLostAtMs >= Math.Max(SkipEndsAfterMs, s.VideoRevertMs))
                        EndSkip("the video has stopped");
                }
            }

            if (c != null)
            {
                _videoLostAtMs = -1;
                _activeVideoHwnd = c.Hwnd;        // latest playing / starting video: what "SDR for this video" keeps in SDR
                _activeVideoName = c.VideoName;
                if (_videoActive)
                {
                    if (c.Key != _activeVideoKey)
                    {
                        _activeVideoKey = c.Key;
                        _activeVideoTarget = _checkedTarget;
                        _activeVideoText = c.Describe();
                        _activeVideoNudge = c.Kind == VideoKind.Sdr;
                        Log("VIDEO", $"Now playing: {_activeVideoText}");
                    }
                }
                else
                {
                    if (c.Key != _pendingVideoKey)
                    {
                        _pendingVideoKey = c.Key;
                        _pendingSinceMs = now;
                    }
                    // The script only marks a video that is really playing (no previews, no ads), so no waiting for it.
                    // The activation delay only filters short sounds for the audio-based detection.
                    if (c.Reported || now - _pendingSinceMs >= s.VideoActivationMs)
                    {
                        _videoActive = true;
                        _videoEnded = false;
                        _videoSdrRequested = false;
                        _pendingVideoKey = null;
                        _activeVideoKey = c.Key;
                        _activeVideoTarget = _checkedTarget;
                        _activeVideoText = c.Describe();
                        _activeVideoNudge = c.Kind == VideoKind.Sdr;
                        Log("VIDEO", $"Video playing: {_activeVideoText}");
                    }
                }
            }
            else
            {
                _pendingVideoKey = null;
                if (_videoActive)
                {
                    if (_videoLostAtMs < 0) _videoLostAtMs = now;
                    if (now - _videoLostAtMs >= s.VideoRevertMs)
                    {
                        _videoActive = false;
                        _videoLostAtMs = -1;
                        _videoEnded = true;
                        _videoEndedWhat = "video stopped or paused";
                    }
                    else
                    {
                        _holdMsLeft = Math.Max(_holdMsLeft, s.VideoRevertMs - (now - _videoLostAtMs));
                    }
                }
            }

            if (!_videoActive) return false;
            reason = _activeVideoText;
            target = _activeVideoTarget;
            return true;
        }

        private void DetectVideo(Snapshot s, long now)
        {
            // RTX Video HDR state (registry read; picks up a change in the NVIDIA App within a minute)
            var rtx = _rtx;
            if (rtx == null || now >= _nextRtxCheckMs)
            {
                var fresh = RtxVideoStatus.Query();
                if (rtx == null || fresh.Describe() != rtx.Describe()) Log("VIDEO", fresh.Describe());
                _rtx = rtx = fresh;
                _nextRtxCheckMs = now + RtxRecheckMs;
            }

            bool allowSdr = s.VideoMode == VideoHdrModes.Always || (s.VideoMode == VideoHdrModes.Smart && rtx.CanUpgradeSdr);
            var candidate = _video.Find(new VideoScanOptions
            {
                Browsers = s.Browsers,
                Players = s.LocalPlayers,
                IncludeBrowsers = s.VideoBrowsers,
                IncludePlayers = s.VideoPlayers,
                FullscreenOnly = s.VideoFullscreenOnly,
                AllowSdr = allowSdr,
                HdrOnly = s.HdrOnlyApps,
                HdrToken = s.BrowserToken,
                SdrToken = s.SdrToken,
                HdrKeywords = s.VideoKeywords
            });

            if (candidate != null)
            {
                // Which monitor? Only looked up again when the window, its monitor or the monitor settings change
                if (Interlocked.Exchange(ref _displaysChanged, 0) == 1 || candidate.Hwnd != _checkedHwnd ||
                    candidate.Monitor != _checkedMonitor || !ReferenceEquals(_checkedAllowed, s.Monitors))
                {
                    _checkedHwnd = candidate.Hwnd;
                    _checkedMonitor = candidate.Monitor;
                    _checkedAllowed = s.Monitors;
                    string? key = HdrController.ResolveVideoMonitor(candidate.Monitor, s.Monitors, out string problem);
                    _checkedTarget = key == null ? null : VideoFilter(key);
                    if (key == null && problem != _lastVideoProblem)
                        Log("VIDEO", $"{candidate.Describe()} – not switching: {problem}.");
                    _lastVideoProblem = key == null ? problem : null;
                }
                if (_checkedTarget == null) candidate = null;
            }
            _videoCandidate = candidate;
        }

        private string? VideoBlockReason(Snapshot s, long now, string procName)
        {
            if (s.VideoBattery)
            {
                if (now >= _nextBatteryCheckMs)
                {
                    _onBattery = PowerInfo.OnBattery();
                    _nextBatteryCheckMs = now + 10000;
                }
                if (_onBattery) return "running on battery";
            }
            if (_runningVideoBlocker != null) return $"'{_runningVideoBlocker}' is running";
            if (s.ForceSdr.Contains(procName)) return $"color-critical app '{procName}' is in front (Apps › Creative apps)";
            return null;
        }

        /// <summary>
        /// Chrome/Edge only notice the switch to HDR (and start RTX Video HDR on the playing video) when they re-check
        /// the display, which can take a moment. As soon as Windows reports HDR on, tell them the display changed
        /// (same nudge as after SDR; no move, resize or focus change).
        /// </summary>
        private void ScheduleBrowserNudgeAfterHdrOn(MonitorFilter monitors, HashSet<string> browsers)
        {
            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 30 && !_stop; i++)   // up to 3 s
                    {
                        if (HdrController.QueryHdrState(monitors, out _, out bool all) && all) break;
                        Thread.Sleep(100);
                    }
                    if (_stop) return;
                    Thread.Sleep(200);   // let DWM finish the mode change
                    WindowDetector.BroadcastDisplayChange();
                    WindowDetector.NudgeWindows(browsers);
                }
                catch { }
            });
        }

        /// <summary>
        /// "SDR for this video" (tray menu, or the "never HDR" shortcut while a browser / video player is in front):
        /// back to SDR now and keep this video in SDR until it stops; other and later videos switch as usual.
        /// Engine thread only. Returns false when no video holds HDR right now.
        /// </summary>
        private bool SkipVideoNow(string via)
        {
            string? key = _videoActive ? _activeVideoKey : _pendingVideoKey;
            if (key == null)
            {
                Log("VIDEO", $"SDR for this video ({via}): no video is using HDR right now.");
                return false;
            }

            if (_activeVideoHwnd == IntPtr.Zero)
            {
                Log("VIDEO", $"SDR for this video ({via}): the video window is not known.");
                return false;
            }

            string what = _videoActive ? _activeVideoText : "the video that is starting";
            bool wasActive = _videoActive;
            _skippedHwnd = _activeVideoHwnd;
            _skippedName = _activeVideoName;
            _skipLostAtMs = -1;
            _videoActive = false;
            _pendingVideoKey = null;
            _activeVideoKey = null;
            _videoLostAtMs = -1;
            if (wasActive)
            {
                _videoEnded = true;   // Decide switches back to SDR right away (no extra delay)
                _videoEndedWhat = "SDR for this video";
                _videoSdrRequested = true;   // you asked for SDR: also when HDR was turned on by you / the player
            }
            string title = _skippedName.Length > 0 ? $" ('{(_skippedName.Length > 60 ? _skippedName[..60] + "…" : _skippedName)}')" : string.Empty;
            Log("VIDEO", $"SDR for this video ({via}): {what}{title} stays in SDR until it stops – the next video switches as usual.");
            return true;
        }

        private void EndSkip(string why)
        {
            _skippedHwnd = IntPtr.Zero;
            _skippedName = string.Empty;
            _skipLostAtMs = -1;
            Log("VIDEO", $"SDR for this video ended – {why}. Videos switch HDR again.");
        }

        /// <summary>Tray menu "SDR for this video".</summary>
        public void SkipCurrentVideo() => Post(() => SkipVideoNow("tray menu"));

        /// <summary>Tray menu "HDR for videos again": ends "SDR for this video" early.</summary>
        public void ResumeVideoHdr() => Post(() =>
        {
            if (_skippedHwnd != IntPtr.Zero) EndSkip("turned back in the tray menu");
        });

        /// <summary>For the tray menu: 0 = no video uses HDR, 1 = a video uses HDR, 2 = "SDR for this video" is active.</summary>
        public int VideoMenuState => Volatile.Read(ref _videoMenuState);
        private int _videoMenuState;

        /// <summary>
        /// The "never HDR" shortcut (Win+Alt+I): for a browser or video player in front it means "SDR for this video" –
        /// excluding the browser itself would stop every video and YouTube HDR from working. Engine thread only.
        /// </summary>
        private void IgnoreOrSkipVideo(string procName)
        {
            var s = GetSnapshot();
            if (!string.IsNullOrEmpty(procName) && s.AllVideoApps.Contains(procName))
            {
                if (SkipVideoNow("shortcut"))
                    System.Media.SystemSounds.Hand.Play();
                else
                    Log("LIST", $"'{procName}' is a browser / video player, so it is not excluded. Here the shortcut keeps the " +
                                "current video in SDR, but no video is switched by Settings › Videos / RTX Video HDR right now. " +
                                "To stop HDR for a while, use the tray menu › HDR override.");
                return;
            }
            IgnoreGame(procName, fromHotkey: true);
        }

        private void ResetVideo()
        {
            _videoActive = false;
            _videoEnded = false;
            _videoSdrRequested = false;
            _pendingVideoKey = null;
            _activeVideoKey = null;
            _videoLostAtMs = -1;
            _videoCandidate = null;
            _nextVideoCheckMs = 0;
        }

        private MonitorFilter VideoFilter(string key)
        {
            if (!_videoFilters.TryGetValue(key, out var filter))
            {
                // OnlyPrimary = true: if that monitor is unplugged, the fallback is the primary one, never "all monitors"
                filter = new MonitorFilter(true, new[] { key });
                _videoFilters[key] = filter;
            }
            return filter;
        }

        private static bool SameTarget(MonitorFilter? a, MonitorFilter? b) =>
            ReferenceEquals(a, b) ||
            (a?.DevicePaths != null && b?.DevicePaths != null && a.DevicePaths.SetEquals(b.DevicePaths));

        /// <summary>
        /// Makes <paramref name="desired"/> (null = the Settings selection) the monitors that are switched.
        /// HDR off: just adopt it. HDR on and still wanted: switch off the monitors that are not part of the new
        /// target first (e.g. the video moved to the other screen, or a game takes over). HDR on but no longer wanted:
        /// keep the old target, so the switch back to SDR reaches the monitors that are actually in HDR.
        /// Returns true when this tick is used up by a switch.
        /// </summary>
        private bool SyncTarget(Snapshot s, long now, MonitorFilter? desired, bool wantOn, bool canAttempt)
        {
            if (SameTarget(desired, _videoTarget)) return false;

            // HDR off – or on, but not switched on by this app (2.6: then no monitor is switched off): just adopt it
            if (!_hdrAny || (!_hdrOnByApp && !s.KeepDesktopSdr))
            {
                _videoTarget = desired;
                Interlocked.Exchange(ref _forceHdrQuery, 1);
                RefreshHdrState(s, now);
                return false;
            }
            if (!wantOn) return false;
            if (!canAttempt) return true;

            var off = HdrController.ResolveTargetKeys(Target(s));
            off.ExceptWith(HdrController.ResolveTargetKeys(desired ?? s.Monitors));
            _videoTarget = desired;
            Interlocked.Exchange(ref _forceHdrQuery, 1);
            if (off.Count > 0)
            {
                TurnHdrOff("HDR moves to another monitor", new MonitorFilter(true, off), s.ReloadProfile, partial: true);
                return true;
            }
            RefreshHdrState(s, now);
            return false;
        }

        // ------------------------------------------------------------------
        // Detection
        // ------------------------------------------------------------------
        // Programs whose exe path was already looked up (once per program per session)
        private readonly HashSet<string> _pathLookupDone = new(StringComparer.OrdinalIgnoreCase);

        // Exe location check, by process id and name: true = runs from its game's folder (or the location is unknown)
        private readonly Dictionary<(int Pid, string Name), bool> _locationOk = new();
        private Snapshot? _locationSnapshot;
        private bool _foregroundElsewhere;   // the program in front has an HDR game's name but runs from another folder

        /// <summary>
        /// False when an HDR library game's process name runs from outside that game's install folder – another program
        /// with the same name. Unknown locations count as a match (as before): a game without a known exe, a game whose
        /// known exes are all gone (moved or reinstalled), or a process whose path cannot be read. Folder links and
        /// junctions are followed. Uses limited query access only, once per process (anti-cheat safe).
        /// </summary>
        private bool IsGameLocation(Snapshot s, string process, int pid)
        {
            if (!s.GameFolders.TryGetValue(process, out var known)) return true;
            if (!ReferenceEquals(s, _locationSnapshot) || _locationOk.Count > 256)
            {
                _locationOk.Clear();   // new configuration (folders may have changed) or a long session: check again
                _locationSnapshot = s;
            }
            var key = (pid, process.ToLowerInvariant());
            if (_locationOk.TryGetValue(key, out bool ok)) return ok;

            string? path = ProcessPaths.TryGetExePath(pid);
            // Xbox / Microsoft Store games run from a WindowsApps path that is not their XboxGames folder
            ok = path == null || path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
                 known.Folders.Any(f => path.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));
            // Slower checks only for a process outside the known folders (once per process)
            if (!ok) ok = !known.Exes.Any(File.Exists);
            if (!ok)
            {
                string real = RealPath(path!);
                ok = known.Folders.Any(f => real.StartsWith(RealPath(f) + "\\", StringComparison.OrdinalIgnoreCase)) ||
                     known.Exes.Any(e => Path.GetFileName(e).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) && SameFile(path!, e));
            }
            if (!ok) Log("HDR", $"'{process}' runs from {path} – not from the folder of the game in your list, so it is ignored.");
            _locationOk[key] = ok;
            return ok;
        }

        private string? FindRunningHdrGame(Snapshot s, out string? exePath, out string? videoBlocker, out string? allBlocker)
        {
            exePath = null;
            videoBlocker = null;
            allBlocker = null;
            bool lookForBlockers = (s.VideoAuto && s.VideoBlockers.Count > 0) || s.AllBlockers.Count > 0;
            if (s.HdrGames.Count == 0 && !lookForBlockers) return null;   // nothing to look for: skip the process list entirely

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return null; }

            string? found = null;
            foreach (var p in all)
            {
                try
                {
                    if (lookForBlockers)
                    {
                        string n = p.ProcessName;
                        if (videoBlocker == null && s.VideoBlockers.Contains(n)) videoBlocker = n.ToLowerInvariant();
                        if (allBlocker == null && s.AllBlockers.Contains(n)) allBlocker = n.ToLowerInvariant();
                    }

                    if (found == null)
                    {
                        string name = p.ProcessName;
                        if (s.HdrGames.Contains(name) && !s.Ignored.Contains(name) && IsGameLocation(s, name, p.Id))
                        {
                            found = name.ToLowerInvariant();
                            // First time this game is seen running: note its exe for the icon in the Games tab
                            if (_pathLookupDone.Add(found))
                            {
                                exePath = ProcessPaths.TryGetExePath(p.Id);   // limited access only, never reads game memory
                            }
                        }
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return found;
        }

        // ------------------------------------------------------------------
        // Launchers (2.5.2, GitHub issue #1)
        // ------------------------------------------------------------------
        /// <summary>
        /// Runs after every process scan. When a watched HDR game closes within <see cref="LauncherShortRunMs"/> of
        /// appearing, it was probably only a launcher (REDprelauncher, PlayGTAV, …). For up to
        /// <see cref="LauncherFollowMs"/> HDR stays on (if it was on) and the app looks for a program from the same
        /// game folder. If one runs, it is followed and remembered (LauncherGames), so next time HDR switches as soon as
        /// the real game starts. Only process names are compared – no handles are opened to the game (anti-cheat safe).
        /// </summary>
        private void TrackLauncherFollow(Snapshot s, long now)
        {
            string? game = _runningGame;
            if (game != null)
            {
                if (!string.Equals(game, _seenGame, StringComparison.OrdinalIgnoreCase))
                {
                    _seenGame = game;
                    // Already running when the app started: it has been running for a while, never a "short run"
                    _seenGameSinceMs = _launcherTrackingStarted ? now : now - LauncherShortRunMs;
                }
                _launcherTrackingStarted = true;
                if (_followLauncher != null) EndFollow();   // the launcher came back, or another HDR game took over
                // Crash hint: the same game started again right after it closed (restarted after a crash) still counts;
                // another game from the list taking over means the first one was a launcher
                if (_quickExitGame != null && string.Equals(game, _quickExitGame, StringComparison.OrdinalIgnoreCase))
                    ReportQuickExit();
                _quickExitGame = null;
                // Crash hint: a game that runs fine with HDR for a minute no longer gets the hint
                if (_hdrOnForGame != null && string.Equals(game, _hdrOnForGame, StringComparison.OrdinalIgnoreCase) &&
                    now - _hdrOnForGameAtMs > QuickExitClearMs)
                {
                    ClearQuickExit(game);
                    _hdrOnForGame = null;
                }
                return;
            }

            _launcherTrackingStarted = true;
            if (_seenGame != null)
            {
                string closed = _seenGame;
                long ran = now - _seenGameSinceMs;
                _seenGame = null;

                // Crash hint: closed within seconds after this app switched HDR on for it
                if (_hdrOnForGame != null && string.Equals(closed, _hdrOnForGame, StringComparison.OrdinalIgnoreCase) &&
                    now - _hdrOnForGameAtMs <= QuickExitMs && !IsLauncher(closed))
                {
                    _quickExitGame = closed;
                    _quickExitAfterMs = now - _hdrOnForGameAtMs;
                }
                _hdrOnForGame = null;

                if (_followLauncher == null && ran < LauncherShortRunMs && !Paused && OverrideMode == HdrOverride.None)
                    StartFollow(s, now, closed, ran);

                // A launcher hands over to the real game: only count it once the launcher check is over
                if (_quickExitGame != null && _followLauncher == null) ReportQuickExit();
            }

            if (_followLauncher == null) return;

            var names = _followNames;
            string? child = names == null ? null : FindRunningFromFolder(s, names, _followLauncher, _followNotBefore);
            if (child != null)
            {
                string launcher = _followLauncher;
                EndFollow();
                _quickExitGame = null;   // it was a launcher, not a crash
                LearnLauncherGame(launcher, child);
                // Take over right away (the next process scan finds it on its own): no SDR switch in between
                _runningGame = child;
                _activeHdrGameProcess = child;
                _seenGame = child;
                _seenGameSinceMs = now;
            }
            else if (now >= _followUntilMs)
            {
                Log("HDR", $"No game started by '{_followLauncher}' within {LauncherFollowMs / 1000} s – normal switching again.");
                EndFollow();
                if (_quickExitGame != null) ReportQuickExit();
            }
        }

        private void StartFollow(Snapshot s, long now, string closed, long ranMs)
        {
            // Only a game that really closed: not one that was just taken off the list or excluded while it runs
            // (its other exes would otherwise be "learned" and put back on the list)
            if (!s.HdrGames.Contains(closed) || s.Ignored.Contains(closed) || IsProcessRunning(closed)) return;

            string? exe = KnownExePath(closed);
            string? folder = exe == null ? null : HdrLauncher.GameFolder(exe);
            if (folder == null) return;   // unknown location (or a shared folder like "Games"): nothing to follow
            // Never a Windows folder: its programs (svchost, explorer, …) are always running and are no game
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 && folder.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return;

            _followLauncher = closed;
            _followUntilMs = now + LauncherFollowMs;
            // The game must have started after the launcher (not a program from that folder that was already running)
            _followNotBefore = DateTime.Now - TimeSpan.FromMilliseconds(Math.Max(0, ranMs)) - TimeSpan.FromSeconds(3);
            _followHoldsHdr = _hdrAny;
            _followNames = null;
            int generation = ++_followGeneration;

            Log("HDR", $"'{closed}' closed after {Math.Max(1, ranMs / 1000)} s – it may be a launcher. " +
                       (_hdrAny ? $"Keeping HDR on for up to {LauncherFollowMs / 1000} s while waiting for the game it starts."
                                : $"Watching for the game it starts for up to {LauncherFollowMs / 1000} s."));

            // The folder listing can take a moment on a hard disk: done in the background, HDR is held meanwhile
            var ignored = s.Ignored;
            Task.Run(() =>
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string n in HdrLauncher.GameExeNames(folder))
                    {
                        if (n.Equals(closed, StringComparison.OrdinalIgnoreCase) || ignored.Contains(n) ||
                            GameLibraryScanner.IsExcludedExeName(n)) continue;
                        names.Add(n);
                    }
                }
                catch { }
                if (names.Count > 150) names.Clear();   // a tools / system folder, not a game folder: follow nothing
                if (Volatile.Read(ref _followGeneration) == generation) _followNames = names;
            });
        }

        private void EndFollow()
        {
            _followLauncher = null;
            _followNames = null;
            _followHoldsHdr = false;
            Interlocked.Increment(ref _followGeneration);
        }

        // ------------------------------------------------------------------
        // Crash hint (2.6): a game closed within seconds after HDR was switched on for it
        // ------------------------------------------------------------------
        /// <summary>
        /// Some games crash when Windows switches to HDR while they start (seen with NVIDIA RTX HDR). The first time, a
        /// HINT goes to the log; from the second time on also a notification and a hint in the game's options.
        /// "Launch with HDR" (HDR on before the game starts) or a start delay avoids the switch during startup.
        /// </summary>
        private void ReportQuickExit()
        {
            string? game = _quickExitGame;
            _quickExitGame = null;
            if (game == null) return;
            int seconds = (int)Math.Max(1, Math.Round(_quickExitAfterMs / 1000.0));

            int count;
            lock (Sync)
            {
                Config.QuickExitCounts.TryGetValue(game, out count);
                count = Math.Min(99, count + 1);
                Config.QuickExitCounts[game] = count;
                Save();
            }

            Log("HINT", $"'{game}' closed {seconds} s after HDR was switched on for it. If it crashed: turn off NVIDIA RTX HDR " +
                        "(NVIDIA App › Graphics › Global Settings › RTX HDR), or use Games › Options › Launch with HDR.");
            if (count >= 2)
                _notifications.Enqueue(($"Did {game} crash?",
                    $"It closed {seconds} s after HDR came on ({count}×). Turn off RTX HDR in the NVIDIA App, or use Launch with HDR."));
        }

        /// <summary>Launchers (by name, or learned from a game they started) close quickly on purpose.</summary>
        private bool IsLauncher(string process)
        {
            if (GameLibraryScanner.LooksLikeLauncher(process)) return true;
            lock (Sync) return Config.LauncherGames.ContainsKey(process);
        }

        private void ClearQuickExit(string game)
        {
            lock (Sync)
            {
                if (Config.QuickExitCounts.Remove(game)) Save();
            }
        }

        /// <summary>How often one of these processes closed right after HDR came on (0 = never; game options hint).</summary>
        public int GetQuickExitCount(IEnumerable<string> processes)
        {
            lock (Sync)
            {
                int max = 0;
                foreach (string p in processes)
                    if (Config.QuickExitCounts.TryGetValue(p, out int n)) max = Math.Max(max, n);
                return max;
            }
        }

        /// <summary>Tray notifications from the engine (crash hint). Called by the tray timer on the UI thread.</summary>
        public bool TryTakeNotification(out string title, out string text)
        {
            if (_notifications.TryDequeue(out var n))
            {
                title = n.Title;
                text = n.Text;
                return true;
            }
            title = text = string.Empty;
            return false;
        }

        /// <summary>Exe path of a process name: from the game library, a scan, "Add program" or an earlier run.</summary>
        private string? KnownExePath(string process)
        {
            lock (Sync)
            {
                if (Config.ProgramPaths.TryGetValue(process, out var path) && File.Exists(path)) return path;
                foreach (var g in Config.LibraryGames)
                {
                    if (g.ExePath != null && Path.GetFileNameWithoutExtension(g.ExePath).Equals(process, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(g.ExePath))
                        return g.ExePath;
                }
                return null;
            }
        }

        private static string? FindRunningFromFolder(Snapshot s, HashSet<string> names, string launcher, DateTime notBefore)
        {
            if (names.Count == 0) return null;
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return null; }

            string? found = null;
            int own = Environment.ProcessId;
            foreach (var p in all)
            {
                try
                {
                    if (found == null && p.Id != own)
                    {
                        string name = p.ProcessName;
                        // StartTime needs only limited query access (like Task Manager); unreadable = not a candidate
                        if (names.Contains(name) && !name.Equals(launcher, StringComparison.OrdinalIgnoreCase) &&
                            !s.ForceSdr.Contains(name) && !s.AllVideoApps.Contains(name) && !s.Ignored.Contains(name) &&
                            p.StartTime >= notBefore)
                            found = name.ToLowerInvariant();
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            return found;
        }

        /// <summary>Remembers launcher -> game and turns the game on for HDR (same options as the launcher).</summary>
        private void LearnLauncherGame(string launcher, string game)
        {
            lock (Sync)
            {
                if (!Config.LauncherGames.TryGetValue(launcher, out var games))
                    Config.LauncherGames[launcher] = games = new List<string>();
                if (!games.Contains(game, Cmp)) games.Add(game);

                if (!Config.HdrGamesAndApps.Contains(game, Cmp))
                {
                    Config.HdrGamesAndApps.Add(game);
                    Config.HdrGamesAndApps.Sort(Cmp);
                }
                if (Config.PerGameOptions.TryGetValue(launcher, out var o) && o != null && !o.IsDefault && !Config.PerGameOptions.ContainsKey(game))
                    Config.PerGameOptions[game] = new GameOptions { HdrDelaySeconds = o.HdrDelaySeconds, KeepHdrAfterExitSeconds = o.KeepHdrAfterExitSeconds };

                // Same row in the Games tab as the launcher's library game
                foreach (var g in Config.LibraryGames)
                    if (g.Processes.Contains(launcher, Cmp) && !g.Processes.Contains(game, Cmp))
                        g.Processes.Add(game);

                InvalidateSnapshot();
                Save();
            }
            Log("HDR", $"Following '{game}' (started by '{launcher}'). Remembered: next time HDR stays on from the start of '{game}'.");
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }

        private static bool IsProcessRunning(string processName)
        {
            try
            {
                var list = Process.GetProcessesByName(processName);
                foreach (var p in list) p.Dispose();
                return list.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private void RememberProgramPath(string process, string path)
        {
            lock (Sync)
            {
                if (Config.ProgramPaths.TryGetValue(process, out var known) && File.Exists(known)) return;
                Config.ProgramPaths[process] = path;
                Save();
            }
        }

        /// <summary>
        /// A path with every folder link and junction in it resolved ("C:\Games\X" → "D:\Games\X" when C:\Games links to
        /// D:\Games). The path itself when it cannot be resolved.
        /// </summary>
        private static string RealPath(string path)
        {
            try
            {
                string full = Path.GetFullPath(path).TrimEnd('\\');
                string current = Path.GetPathRoot(full) ?? string.Empty;
                if (current.Length == 0) return full;
                foreach (string part in full.Substring(current.Length).Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, part);
                    var info = new DirectoryInfo(current);
                    if (info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                        current = target.FullName;
                }
                return current.TrimEnd('\\');
            }
            catch
            {
                return path;
            }
        }

        /// <summary>
        /// The same exe reached through another path, such as a mapped drive. Compared by size and time stamp;
        /// a file that cannot be read counts as the same (unknown = no change in behaviour).
        /// </summary>
        private static bool SameFile(string a, string b)
        {
            try
            {
                var fa = new FileInfo(a);
                var fb = new FileInfo(b);
                if (!fa.Exists || !fb.Exists) return !fa.Exists;
                return fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
            }
            catch
            {
                return true;
            }
        }

        /// <param name="otherProgram">The program in front only shares an HDR game's process name (see <see cref="IsGameLocation"/>).</param>
        private static bool EvaluateHdrRequirement(Snapshot s, string procName, string title, bool isFullscreen, bool otherProgram, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrEmpty(procName)) return false;

            if (s.ForceSdr.Contains(procName) || s.Ignored.Contains(procName))
                return false;

            // 1. Selected HDR games
            if (s.HdrGames.Contains(procName) && !otherProgram)
            {
                reason = $"HDR game/app in focus ({procName})";
                return true;
            }

            // 2. Web browsers: only react to the token injected by the Tampermonkey script
            if (s.Browsers.Contains(procName))
            {
                if (s.VideoAuto && s.VideoBrowsers) return false;   // handled by the video automation (EvaluateVideo)

                if (!isFullscreen && !s.BrowserWindowed)
                    return false;

                if (s.BrowserToken.Length > 0 && title.Contains(s.BrowserToken, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"True YouTube HDR stream detected ({procName})";
                    return true;
                }
                return false;
            }

            // 3. Local video players
            if (s.VideoAuto && s.VideoPlayers && s.LocalPlayers.Contains(procName))
                return false;   // handled by the video automation (EvaluateVideo)
            if (isFullscreen && s.LocalPlayers.Contains(procName))
            {
                if (!s.RequireKeyword)
                {
                    reason = $"Fullscreen video in local player ({procName})";
                    return true;
                }

                foreach (var keyword in s.VideoKeywords)
                {
                    if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        reason = $"Local HDR fullscreen video detected ({procName} – keyword: '{keyword}')";
                        return true;
                    }
                }
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Switching HDR / SDR (engine thread only)
        // ------------------------------------------------------------------
        private void TurnHdrOn(string reason, MonitorFilter monitors)
        {
            long now = Now;
            _lastAttemptMs = now;
            Log("HDR", $"Turning HDR ON ({monitors.Describe()}) – {reason}");

            CancelColorProfileReload();   // a reload must not run while we are going back into HDR
            if (!_hdrAny)
            {
                // Remember how the displays looked in SDR, to check (and repair) them after switching back
                try { _sdrBaseline = HdrController.CaptureColorState(monitors); }
                catch { _sdrBaseline = null; }
            }
            bool ok = HdrController.SetHdrState(true, monitors);
            if (ok)
            {
                if (!_hdrAny || _hdrOnSinceMs == 0) _hdrOnSinceMs = now;
                _hdrOnByApp = true;
                _hdrAny = true;
                _hdrAll = true;
                _verifyAllOn = true;
                _expectAny = true;          // until Windows reports it, an "off" reading is not a change by someone else
                _switchConfirmed = false;
            }
            else
            {
                Log("ERROR", HdrController.LastError);
            }
            AfterSwitch(ok, now);
        }

        /// <param name="partial">Only some monitors (HDR moves to another monitor): HDR stays on elsewhere and stays ours.</param>
        private void TurnHdrOff(string reason, MonitorFilter monitors, bool reloadProfile, bool partial = false)
        {
            long now = Now;
            _lastAttemptMs = now;
            Log("SDR", $"Switching to SDR ({monitors.Describe()}) – {reason}");

            bool ok = HdrController.SetHdrState(false, monitors);
            if (ok)
            {
                _hdrOnSinceMs = 0;
                if (!partial)
                {
                    _hdrOnByApp = false;
                    _expectAny = false;
                    _switchConfirmed = false;
                }
                _holdHdrUntilMs = 0;
                _hdrAny = false;
                _hdrAll = false;
                _verifyAllOn = false;
                var snapshot = GetSnapshot();
                var baseline = _sdrBaseline;
                if (reloadProfile || snapshot.RefreshApps || baseline != null)
                    SchedulePostSdrFixes(monitors, reloadProfile, snapshot.RefreshApps, snapshot.Browsers, baseline);
            }
            else
            {
                Log("ERROR", HdrController.LastError);
            }
            AfterSwitch(ok, now);
        }

        private void AfterSwitch(bool ok, long now)
        {
            // Re-read the real state once Windows has applied the change
            _nextHdrQueryMs = now + SwitchSpacingMs;

            if (ok)
            {
                if (!_verifyAllOn) _consecutiveFailures = 0;   // an ON is only "good" once verified
                return;
            }

            RegisterFailure(now);
        }

        private void RegisterFailure(long now)
        {
            if (++_consecutiveFailures >= FailuresBeforeCooldown)
            {
                _consecutiveFailures = 0;
                _cooldownUntilMs = now + FailureCooldownMs;
                Log("ERROR", $"HDR switching failed {FailuresBeforeCooldown} times in a row – pausing automatic attempts for {FailureCooldownMs / 1000} s.");
            }
        }

        private int _profileReloadGeneration;
        private volatile bool _calibrationLoaderDenied;   // the Windows task needs admin rights on some PCs

        /// <summary>
        /// Clean-up after leaving HDR, run in the background:
        /// 1. waits until Windows really reports SDR (up to 6 s), then lets the display settle (1.5 s);
        /// 2. re-applies the colour profile (ICC calibration / gamma ramp), if enabled;
        /// 3. tells open windows that the display changed and nudges browser windows to repaint, if enabled
        ///    (fixes Chrome & co. staying dimmed with their cached HDR brightness);
        /// 4. repeats steps 2-3 once a few seconds later, in case the driver reset things again.
        /// Cancels itself if HDR is switched back on in the meantime.
        /// </summary>
        private void SchedulePostSdrFixes(MonitorFilter monitors, bool reloadProfile, bool refreshApps, HashSet<string> browsers,
                                          List<DisplayColorState>? baseline)
        {
            int generation = Interlocked.Increment(ref _profileReloadGeneration);
            bool Cancelled() => Volatile.Read(ref _profileReloadGeneration) != generation || _stop;

            Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 24; i++)
                    {
                        if (Cancelled()) return;
                        if (HdrController.QueryHdrState(monitors, out bool any, out _) && !any) break;
                        Thread.Sleep(250);
                    }

                    Thread.Sleep(1500);
                    if (Cancelled()) return;

                    if (baseline != null) CheckColorsAfterSdr(baseline, monitors, report: true);

                    bool profileOk = reloadProfile && !_calibrationLoaderDenied;
                    if (profileOk)
                    {
                        profileOk = RunCalibrationLoader(out string error);
                        if (!profileOk)
                        {
                            if (error.Contains("denied", StringComparison.OrdinalIgnoreCase))
                            {
                                _calibrationLoaderDenied = true;   // don't try again until the app restarts
                                Log("INFO", "Color profile reload needs administrator rights on this PC and was skipped. " +
                                            "Turn off 'Reload color profile' unless you use a calibration profile and run the app as administrator.");
                            }
                            else
                            {
                                Log("ERROR", $"Color profile reload failed: {error}");
                            }
                        }
                    }

                    int nudged = 0;
                    if (refreshApps)
                    {
                        WindowDetector.BroadcastDisplayChange();
                        nudged = WindowDetector.NudgeWindows(browsers);
                    }

                    Thread.Sleep(4000);
                    if (Cancelled()) return;

                    // Some drivers reset the gamma curve a second time a few seconds after the switch
                    if (baseline != null) CheckColorsAfterSdr(baseline, monitors, report: false);

                    if (reloadProfile && profileOk) RunCalibrationLoader(out _);
                    if (refreshApps)
                    {
                        WindowDetector.BroadcastDisplayChange();
                        WindowDetector.NudgeWindows(browsers);
                    }

                    if (reloadProfile && profileOk) Log("SDR", "Color profile re-applied.");
                    if (refreshApps) Log("SDR", $"Open apps notified of the display change ({nudged} browser window(s) refreshed).");
                }
                catch (Exception ex)
                {
                    Log("ERROR", $"Post-SDR clean-up failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Compares every display with its state right before HDR was switched on and logs what changed.
        /// A gamma curve that was reset to neutral (lost calibration, night light, NVIDIA desktop colour settings)
        /// is put back. Colour format / bit depth / colour mode changes are logged only (they belong to the driver).
        /// </summary>
        private void CheckColorsAfterSdr(List<DisplayColorState> before, MonitorFilter monitors, bool report)
        {
            List<DisplayColorState> after;
            try { after = HdrController.CaptureColorState(monitors); }
            catch (Exception ex)
            {
                if (report) Log("COLOR", $"Colour check after SDR failed: {ex.Message}");
                return;
            }

            foreach (var b in before)
            {
                var a = after.FirstOrDefault(x => string.Equals(x.Key, b.Key, StringComparison.OrdinalIgnoreCase));
                if (a == null || a.HdrOn || b.HdrOn) continue;   // unplugged, back in HDR, or was not in SDR before

                var changes = new List<string>();
                if (a.Encoding >= 0 && b.Encoding >= 0 && a.Encoding != b.Encoding)
                    changes.Add($"colour format {DisplayColorState.EncodingName(a.Encoding)} (was {DisplayColorState.EncodingName(b.Encoding)}) – " +
                                "set it back in NVIDIA Control Panel › Change resolution › Output color format");
                if (a.Bits > 0 && b.Bits > 0 && a.Bits != b.Bits)
                    changes.Add($"{a.Bits}-bit (was {b.Bits}-bit)");
                if (a.Mode >= 0 && b.Mode >= 0 && a.Mode != b.Mode)
                    changes.Add($"{DisplayColorState.ModeName(a.Mode)} (was {DisplayColorState.ModeName(b.Mode)})");

                bool rampReset = b.Ramp != null && !b.RampIsIdentity && a.RampIsIdentity;
                if (rampReset)
                {
                    bool restored = HdrController.RestoreGammaRamp(b);
                    changes.Add(restored ? "gamma curve was reset to neutral – restored" : "gamma curve was reset to neutral – could not restore it");
                }
                else if (report && b.Ramp != null && a.Ramp != null && DisplayColorState.RampDistance(a.Ramp, b.Ramp) > 300)
                {
                    changes.Add("gamma curve changed (night light or colour settings?)");
                }

                if (report)
                    Log("COLOR", changes.Count == 0
                        ? $"{b.Name}: colours same as before HDR ({a.Describe()})."
                        : $"{b.Name} after SDR: {string.Join("; ", changes)}. Before HDR: {b.Describe()}.");
                else if (rampReset)
                    Log("COLOR", $"{b.Name}: gamma curve was reset again a few seconds later – {changes[^1].Split('–')[^1].Trim()}.");
            }
        }

        /// <summary>Cancels a pending colour profile reload (e.g. when HDR is switched on again).</summary>
        private void CancelColorProfileReload() => Interlocked.Increment(ref _profileReloadGeneration);

        private static bool RunCalibrationLoader(out string error)
        {
            error = string.Empty;
            var psi = new ProcessStartInfo("schtasks.exe",
                "/run /tn \"\\Microsoft\\Windows\\WindowsColorSystem\\Calibration Loader\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p == null)
            {
                error = "schtasks.exe could not be started";
                return false;
            }

            string stderr = p.StandardError.ReadToEnd();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000))
            {
                error = "schtasks.exe did not finish";
                return false;
            }
            if (p.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr) ? $"schtasks.exe exit code {p.ExitCode}" : stderr.Trim();
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------------
        // Public API for the UI (thread-safe)
        // ------------------------------------------------------------------
        public static string CleanExeInput(string rawInput)
        {
            string trimmed = rawInput.Trim().Trim('"', '\'');
            if (trimmed.Contains('\\') || trimmed.Contains('/') || trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                trimmed = Path.GetFileNameWithoutExtension(trimmed);
            return trimmed.ToLowerInvariant();
        }

        public bool IsScanning => Volatile.Read(ref _scanning) == 1;

        public string? GetInitialDirectory()
        {
            lock (Sync)
                return Config.CustomGameFolders.FirstOrDefault(Directory.Exists);
        }

        /// <summary>
        /// Rows for the Games tab: every library game (real name, source, icon), then programs that are not in a
        /// library: always the HDR-enabled ones, and the drive-scan results when includeOthers is set.
        /// includeExcluded adds the "Never HDR" games (marked Excluded), e.g. while searching.
        /// </summary>
        public List<GameRow> GetGameRows(bool includeOthers, bool includeExcluded = false)
        {
            lock (Sync)
            {
                var hdr = new HashSet<string>(Config.HdrGamesAndApps ?? new List<string>(), Cmp);
                var ignored = new HashSet<string>(Config.IgnoredGames ?? new List<string>(), Cmp);
                var options = Config.PerGameOptions ?? new Dictionary<string, GameOptions>();
                var paths = Config.ProgramPaths ?? new Dictionary<string, string>();
                bool HasOptions(IEnumerable<string> procs) =>
                    procs.Any(p => options.TryGetValue(p, out var o) && o != null && !o.IsDefault);

                var rows = new List<GameRow>();
                var covered = new HashSet<string>(Cmp);

                foreach (var game in Config.LibraryGames ?? new List<LibraryGame>())
                {
                    covered.UnionWith(game.Processes);
                    var procs = game.Processes.Where(p => !ignored.Contains(p)).ToList();
                    if (procs.Count == 0) continue;

                    rows.Add(new GameRow
                    {
                        Key = game.Id,
                        Name = game.Name,
                        Subtitle = procs.Count > 1 ? $"{game.Source}  ·  {procs[0]}.exe +{procs.Count - 1}" : $"{game.Source}  ·  {procs[0]}.exe",
                        Processes = procs,
                        // library path, or one learned from the drive scan / a running game (e.g. Xbox games)
                        ExePath = game.ExePath ?? procs.Select(p => paths.TryGetValue(p, out var known) ? known : null)
                                                      .FirstOrDefault(p => p != null),
                        Hdr = procs.Any(hdr.Contains),
                        HasOptions = HasOptions(procs)
                    });
                }

                var others = (Config.HdrGamesAndApps ?? new List<string>())
                    .Concat(includeOthers ? Config.DiscoveredGames ?? new List<string>() : new List<string>())
                    .Distinct(Cmp)
                    .Where(p => !covered.Contains(p) && !ignored.Contains(p));

                foreach (var proc in others)
                {
                    rows.Add(new GameRow
                    {
                        Key = "proc:" + proc,
                        Name = proc,
                        Subtitle = hdr.Contains(proc) ? $"Program  ·  {proc}.exe" : $"Found by drive scan  ·  {proc}.exe",
                        Processes = new List<string> { proc },
                        ExePath = paths.TryGetValue(proc, out var path) ? path : null,
                        Hdr = hdr.Contains(proc),
                        HasOptions = HasOptions(new[] { proc })
                    });
                }

                if (includeExcluded)
                    rows.AddRange(GetExcludedRows());

                return rows.OrderBy(r => r.Name, Cmp).ToList();
            }
        }

        /// <summary>
        /// The "Never HDR" games: library games under their real name, other programs by process name.
        /// Keys start with "never:" so they never clash with the normal row of the same game.
        /// </summary>
        public List<GameRow> GetExcludedRows()
        {
            lock (Sync)
            {
                var ignored = new HashSet<string>(Config.IgnoredGames ?? new List<string>(), Cmp);
                var paths = Config.ProgramPaths ?? new Dictionary<string, string>();
                var rows = new List<GameRow>();
                var covered = new HashSet<string>(Cmp);

                foreach (var game in Config.LibraryGames ?? new List<LibraryGame>())
                {
                    var procs = game.Processes.Where(ignored.Contains).ToList();
                    if (procs.Count == 0) continue;
                    covered.UnionWith(procs);
                    rows.Add(new GameRow
                    {
                        Key = "never:" + game.Id,
                        Name = game.Name,
                        Subtitle = procs.Count > 1 ? $"Never HDR  ·  {game.Source}  ·  {procs[0]}.exe +{procs.Count - 1}"
                                                   : $"Never HDR  ·  {game.Source}  ·  {procs[0]}.exe",
                        Processes = procs,
                        ExePath = game.ExePath ?? procs.Select(p => paths.TryGetValue(p, out var known) ? known : null)
                                                      .FirstOrDefault(p => p != null),
                        Excluded = true
                    });
                }

                foreach (var proc in ignored.Where(p => !covered.Contains(p)))
                {
                    rows.Add(new GameRow
                    {
                        Key = "never:proc:" + proc,
                        Name = proc,
                        Subtitle = $"Never HDR  ·  {proc}.exe",
                        Processes = new List<string> { proc },
                        ExePath = paths.TryGetValue(proc, out var path) ? path : null,
                        Excluded = true
                    });
                }

                return rows.OrderBy(r => r.Name, Cmp).ToList();
            }
        }

        /// <summary>Undoes "Never HDR": the game is handled like any other again (HDR stays off until turned on).</summary>
        public void RestoreProcesses(IEnumerable<string> processes, string displayName)
        {
            var list = processes.ToList();
            lock (Sync)
            {
                foreach (var name in list)
                    Config.IgnoredGames.RemoveAll(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
                InvalidateSnapshot();
                Save();
            }
            Log("LIST", $"+ '{displayName}' is no longer excluded (\"Never HDR\" undone)");
        }

        public void SetGameHdr(IEnumerable<string> processes, bool enabled, string displayName)
        {
            var list = processes.ToList();
            lock (Sync)
            {
                foreach (var name in list)
                {
                    if (enabled)
                    {
                        if (!Config.HdrGamesAndApps.Contains(name, Cmp))
                            Config.HdrGamesAndApps.Add(name);
                        // Turning HDR on also undoes "Never HDR" – otherwise the exclusion would silently win
                        Config.IgnoredGames.RemoveAll(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        Config.HdrGamesAndApps.RemoveAll(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
                    }
                }
                Config.HdrGamesAndApps.Sort(Cmp);
                InvalidateSnapshot();
                Save();
            }
            Log("LIST", enabled ? $"+ '{displayName}' will use HDR" : $"- '{displayName}' no longer uses HDR");
        }

        public void IgnoreProcesses(IEnumerable<string> processes, string displayName)
        {
            var list = processes.ToList();
            lock (Sync)
            {
                foreach (var name in list)
                {
                    Config.HdrGamesAndApps.RemoveAll(g => g.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (!Config.IgnoredGames.Contains(name, Cmp))
                        Config.IgnoredGames.Add(name);
                }
                InvalidateSnapshot();
                Save();
            }
            Log("LIST", $"- '{displayName}' will never use HDR (undo: Settings › Never HDR games, or search for it in Games)");
        }

        public GameOptions GetGameOptions(IEnumerable<string> processes)
        {
            lock (Sync)
            {
                foreach (var p in processes)
                    if (Config.PerGameOptions.TryGetValue(p, out var o) && o != null)
                        return new GameOptions { HdrDelaySeconds = o.HdrDelaySeconds, KeepHdrAfterExitSeconds = o.KeepHdrAfterExitSeconds };
                return new GameOptions();
            }
        }

        public void SetGameOptions(IEnumerable<string> processes, GameOptions options, string displayName)
        {
            var list = processes.ToList();
            lock (Sync)
            {
                foreach (var p in list)
                {
                    if (options.IsDefault)
                        Config.PerGameOptions.Remove(p);
                    else
                        Config.PerGameOptions[p] = new GameOptions
                        {
                            HdrDelaySeconds = options.HdrDelaySeconds,
                            KeepHdrAfterExitSeconds = options.KeepHdrAfterExitSeconds
                        };
                }
                InvalidateSnapshot();
                Save();
            }
            Log("LIST", options.IsDefault
                ? $"'{displayName}': game options reset."
                : $"'{displayName}': HDR delay {options.HdrDelaySeconds} s, keep HDR after exit {options.KeepHdrAfterExitSeconds} s.");
        }

        // ------------------------------------------------------------------
        // Apps tab (2.6)
        // ------------------------------------------------------------------
        /// <summary>Copies of the Apps tab rules (process name -> rule).</summary>
        public Dictionary<string, AppRule> GetAppRules()
        {
            lock (Sync)
                return Config.AppRules.ToDictionary(kv => kv.Key,
                    kv => new AppRule { Category = kv.Value.Category, Mode = kv.Value.Mode, ExePath = kv.Value.ExePath, AddedByUser = kv.Value.AddedByUser },
                    Cmp);
        }

        /// <summary>
        /// Installed programs found by the Apps tab that are not in the list yet: added with their suggested mode.
        /// A program that is already listed keeps its mode (also "don't touch").
        /// </summary>
        public void AddFoundApps(IEnumerable<(string Process, string Category, string Mode)> found)
        {
            var added = new List<string>();
            lock (Sync)
            {
                foreach (var (process, category, mode) in found)
                {
                    string key = (process ?? string.Empty).Trim().ToLowerInvariant();
                    string? cat = AppCategories.Normalize(category);
                    if (key.Length == 0 || cat == null || Config.AppRules.ContainsKey(key)) continue;
                    Config.AppRules[key] = new AppRule { Category = cat, Mode = AppModes.Normalize(cat, mode) };
                    added.Add(key);
                }
                if (added.Count > 0)
                {
                    InvalidateSnapshot();
                    Save();
                }
            }
            if (added.Count > 0) Log("APPS", $"Found and added to the Apps tab: {string.Join(", ", added)}.");
        }

        public void SetAppMode(string process, string mode, string displayName)
        {
            string? text = null;
            lock (Sync)
            {
                if (!Config.AppRules.TryGetValue(process, out var rule)) return;
                string normalized = AppModes.Normalize(rule.Category, mode);
                if (normalized == rule.Mode) return;
                rule.Mode = normalized;
                InvalidateSnapshot();
                Save();
                text = DescribeAppMode(rule.Category, normalized);
            }
            Log("APPS", $"'{displayName}': {text}.");
        }

        /// <summary>A program picked by hand in the Apps tab (Add …). Returns false if the name is not usable.</summary>
        public bool AddApp(string exePath, string category)
        {
            string key = AppCatalog.ProcessOf(exePath);
            string? cat = AppCategories.Normalize(category);
            if (key.Length == 0 || cat == null || key == "explorer" || key == "smarthdrmanager") return false;
            lock (Sync)
            {
                Config.AppRules.TryGetValue(key, out var existing);
                Config.AppRules[key] = new AppRule
                {
                    Category = cat,
                    // already listed in the same group: keep its mode; otherwise the group's "on" mode
                    Mode = existing != null && existing.Category == cat ? existing.Mode : AppModes.Normalize(cat, null),
                    ExePath = exePath,
                    // picked by hand: always listed and removable (a program that is also found automatically comes
                    // back with its suggested mode after "Remove")
                    AddedByUser = true
                };
                InvalidateSnapshot();
                Save();
            }
            Log("APPS", $"+ '{key}' added to the Apps tab ({cat}).");
            return true;
        }

        /// <summary>Removes a program that was added by hand (programs found automatically can only be set to "off").</summary>
        public void RemoveApp(string process)
        {
            bool removed;
            lock (Sync)
            {
                removed = false;
                if (Config.AppRules.TryGetValue(process, out var rule))
                {
                    if (rule.AddedByUser) removed = Config.AppRules.Remove(process);
                    else if (rule.ExePath != null) { rule.ExePath = null; removed = true; }   // a known program picked by hand
                }
                if (removed)
                {
                    InvalidateSnapshot();
                    Save();
                }
            }
            if (removed) Log("APPS", $"- '{process}' removed from the Apps tab.");
        }

        public static string DescribeAppMode(string category, string mode) => (category, mode) switch
        {
            (_, AppModes.Off) => "HDR not touched",
            (AppCategories.Creative, _) => "SDR while it is in front",
            (AppCategories.Recording, _) => "no HDR while it runs (games too)",
            (_, AppModes.HdrOnly) => "only HDR videos switch HDR on",
            _ => "videos as set in Settings › Videos"
        };

        /// <summary>fromHotkey = true must only be used on the engine thread (it switches HDR immediately).</summary>
        public bool AddGame(string procName, bool fromHotkey, string? exePath = null)
        {
            if (string.IsNullOrWhiteSpace(procName) || procName == "explorer" || procName == "smarthdrmanager")
            {
                Log("ERROR", "Invalid process name.");
                return false;
            }

            procName = procName.ToLowerInvariant();
            lock (Sync)
            {
                Config.IgnoredGames.RemoveAll(g => g.Equals(procName, StringComparison.OrdinalIgnoreCase));

                if (!Config.DiscoveredGames.Contains(procName, Cmp))
                {
                    Config.DiscoveredGames.Add(procName);
                    Config.DiscoveredGames.Sort(Cmp);
                }

                if (!Config.HdrGamesAndApps.Contains(procName, Cmp))
                {
                    Config.HdrGamesAndApps.Add(procName);
                    Config.HdrGamesAndApps.Sort(Cmp);
                    Log("LIST", $"+ '{procName}' added to the HDR list");
                }
                else
                {
                    Log("LIST", $"'{procName}' is already in the HDR list");
                }
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    Config.ProgramPaths[procName] = exePath;   // used for the icon in the Games tab
                InvalidateSnapshot();
                Save();
            }

            if (fromHotkey)
            {
                System.Media.SystemSounds.Asterisk.Play();
                TurnHdrOn($"'{procName}' added via hotkey", GetSnapshot().Monitors);
            }
            return true;
        }

        /// <summary>fromHotkey = true must only be used on the engine thread (it switches HDR immediately).</summary>
        public void IgnoreGame(string procName, bool fromHotkey)
        {
            if (string.IsNullOrWhiteSpace(procName) || procName == "explorer") return;
            procName = procName.ToLowerInvariant();

            bool reload;
            lock (Sync)
            {
                Config.HdrGamesAndApps.RemoveAll(g => g.Equals(procName, StringComparison.OrdinalIgnoreCase));
                if (!Config.IgnoredGames.Contains(procName, Cmp))
                    Config.IgnoredGames.Add(procName);
                InvalidateSnapshot();
                Save();
                reload = Config.ReloadColorProfileAfterSdr;
            }
            Log("LIST", $"- '{procName}' will never use HDR (undo: Settings › Never HDR games, or search for it in Games)");

            if (fromHotkey)
            {
                System.Media.SystemSounds.Hand.Play();
                TurnHdrOff($"'{procName}' ignored via hotkey", GetSnapshot().Monitors, reload);
            }
        }

        /// <summary>True while a known HDR game runs (or HDR is still held for one) or any app is full-screen in front.</summary>
        private bool IsGameOrFullscreenActive(out string what)
        {
            string? game = Volatile.Read(ref _runningGame) ?? Volatile.Read(ref _activeHdrGameProcess);
            if (game != null) { what = $"'{game}'"; return true; }
            var status = Status;
            if (status.IsFullscreen)
            {
                what = string.IsNullOrEmpty(status.ActiveProcess) ? "a full-screen app" : $"'{status.ActiveProcess}' (full screen)";
                return true;
            }
            what = string.Empty;
            return false;
        }

        /// <summary>
        /// Scans all fixed drives for games. Works on a copy of the lists so the engine is never blocked.
        /// <paramref name="automatic"/> scans (at startup) are skipped while a game or full-screen app is running;
        /// the "Scan games" button always scans.
        /// </summary>
        public int ScanDrives(bool automatic = false)
        {
            if (automatic && IsGameOrFullscreenActive(out string what))
            {
                Log("SCAN", $"Automatic drive scan skipped – {what} is running.");
                return 0;
            }

            if (Interlocked.Exchange(ref _scanning, 1) == 1)
            {
                Log("SCAN", "A drive scan is already running.");
                return 0;
            }

            try
            {
                RefreshLibrary(fullRescan: true);

                Log("SCAN", "Scanning drives for other programs ...");
                AppConfig temp;
                lock (Sync)
                {
                    temp = new AppConfig
                    {
                        CustomGameFolders = new List<string>(Config.CustomGameFolders),
                        IgnoredGames = new List<string>(Config.IgnoredGames),
                        DiscoveredGames = new List<string>(Config.DiscoveredGames)
                    };
                }

                var foundPaths = new Dictionary<string, string>(Cmp);
                int added = GameScanner.ScanAndAddGames(temp, foundPaths);

                lock (Sync)
                {
                    // Exe locations for icons; paths picked by hand ("Add program") are kept
                    foreach (var kv in foundPaths)
                        if (!Config.ProgramPaths.TryGetValue(kv.Key, out var known) || !File.Exists(known))
                            Config.ProgramPaths[kv.Key] = kv.Value;

                    foreach (var g in temp.DiscoveredGames)
                        if (!Config.DiscoveredGames.Contains(g, Cmp))
                            Config.DiscoveredGames.Add(g);
                    Config.DiscoveredGames.Sort(Cmp);
                    Config.LastDriveScanUtc = DateTime.UtcNow;
                    Save();
                }
                Log("SCAN", $"{added} new programs found by the drive scan.");
                return added;
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Drive scan failed: {ex.Message}");
                return 0;
            }
            finally
            {
                Volatile.Write(ref _scanning, 0);
            }
        }

        public T Read<T>(Func<AppConfig, T> getter)
        {
            lock (Sync) return getter(Config);
        }

        public void Update(Action<AppConfig> change)
        {
            lock (Sync)
            {
                change(Config);
                InvalidateSnapshot();
                Save();
            }
        }

        /// <summary>Re-reads config.json (e.g. after the user edited it in a text editor).</summary>
        public bool ReloadConfig()
        {
            try
            {
                var fresh = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath));
                if (fresh == null) return false;
                Normalize(fresh);
                lock (Sync)
                {
                    foreach (var prop in typeof(AppConfig).GetProperties())
                        if (prop.CanWrite) prop.SetValue(Config, prop.GetValue(fresh));
                    InvalidateSnapshot();
                }
                Log("CONFIG", "config.json reloaded.");
                return true;
            }
            catch (Exception ex)
            {
                Log("ERROR", $"config.json could not be reloaded: {ex.Message}");
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Config load / save (same JSON format as before)
        // ------------------------------------------------------------------
        private AppConfig LoadOrCreateConfig()
        {
            if (!File.Exists(ConfigPath))
                return Normalize(new AppConfig());

            try
            {
                string json = File.ReadAllText(ConfigPath);
                return Normalize(JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig());
            }
            catch (Exception ex)
            {
                try { File.Copy(ConfigPath, ConfigPath + ".bak", true); } catch { }
                Log("ERROR", $"config.json unreadable ({ex.Message}) – backup saved as config.json.bak, using default values.");
                return Normalize(new AppConfig());
            }
        }

        /// <summary>Lists / dictionaries that are missing or null in a hand-edited config.json become empty.</summary>
        private static AppConfig Normalize(AppConfig c)
        {
            c.CustomGameFolders ??= new List<string>();
            c.IgnoredGames ??= new List<string>();
            c.HdrGamesAndApps ??= new List<string>();
            c.DiscoveredGames ??= new List<string>();
            c.LocalVideoPlayers ??= new List<string>();
            c.Browsers ??= new List<string>();
            c.HdrVideoKeywords ??= new List<string>();
            c.ForceSdrApps ??= new List<string>();
            c.SelectedMonitors ??= new List<string>();
            c.LibraryGames ??= new List<LibraryGame>();
            c.HdrPlayerPaths ??= new List<string>();

            // Case-insensitive keys (process names), also after loading from JSON
            var options = new Dictionary<string, GameOptions>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in c.PerGameOptions ?? new Dictionary<string, GameOptions>())
                if (kv.Value != null) options[kv.Key] = kv.Value;
            c.PerGameOptions = options;

            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in c.ProgramPaths ?? new Dictionary<string, string>())
                if (!string.IsNullOrEmpty(kv.Value)) paths[kv.Key] = kv.Value;
            c.ProgramPaths = paths;

            c.HotkeyModifier = HotkeySettings.Normalize(c.HotkeyModifier);

            // Same ranges as the controls in Settings
            c.MinimumHdrSeconds = Math.Clamp(c.MinimumHdrSeconds, 0, 120);
            c.VideoHdrBlockingApps ??= new List<string>();
            c.VideoHdrMode = VideoHdrModes.Normalize(c.VideoHdrMode);
            c.VideoHdrTrigger = VideoTriggers.Normalize(c.VideoHdrTrigger);
            c.VideoHdrRevertSeconds = Math.Clamp(c.VideoHdrRevertSeconds, 0, MaxVideoRevertSeconds);
            c.VideoHdrActivationSeconds = Math.Clamp(c.VideoHdrActivationSeconds, 0, 30);
            c.BrowserSdrVideoToken ??= string.Empty;
            c.SdrFallbackDelaySeconds = Math.Clamp(c.SdrFallbackDelaySeconds, 0, 30);
            foreach (var g in c.LibraryGames) g.Processes ??= new List<string>();

            // Learned launchers: case-insensitive keys, lower-case names, no empty entries
            var launchers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in c.LauncherGames ?? new Dictionary<string, List<string>>())
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                var games = kv.Value.Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                                    .Select(n => n.Trim().ToLowerInvariant()).Distinct().ToList();
                if (games.Count > 0) launchers[kv.Key.Trim().ToLowerInvariant()] = games;
            }
            c.LauncherGames = launchers;

            // Apps tab (2.6): case-insensitive process names, known categories, valid modes
            var rules = new Dictionary<string, AppRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in c.AppRules ?? new Dictionary<string, AppRule>())
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                string? category = AppCategories.Normalize(kv.Value.Category);
                if (category == null) continue;
                rules[kv.Key.Trim().ToLowerInvariant()] = new AppRule
                {
                    Category = category,
                    Mode = AppModes.Normalize(category, kv.Value.Mode),
                    ExePath = string.IsNullOrWhiteSpace(kv.Value.ExePath) ? null : kv.Value.ExePath.Trim(),
                    AddedByUser = kv.Value.AddedByUser
                };
            }
            c.AppRules = rules;
            c.DriveScanIntervalDays = c.DriveScanIntervalDays < 0 ? -1 : Math.Min(c.DriveScanIntervalDays, 365);

            var quick = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in c.QuickExitCounts ?? new Dictionary<string, int>())
                if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0) quick[kv.Key.Trim().ToLowerInvariant()] = Math.Min(kv.Value, 99);
            c.QuickExitCounts = quick;
            return c;
        }

        /// <summary>Caller must hold <see cref="Sync"/> (or be in the constructor).</summary>
        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                // The 2.5 lists and switch are no longer read, but kept in step: an older version started later still works
                AppCatalog.WriteLegacyLists(Config);
                Config.AutoScanDrivesOnStartup = Config.DriveScanIntervalDays >= 0;
                var options = new JsonSerializerOptions { WriteIndented = true };
                string tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Config, options));
                File.Move(tmp, ConfigPath, overwrite: true);   // atomic replace: never leaves a half-written config.json
            }
            catch (Exception ex)
            {
                Log("ERROR", $"config.json could not be saved: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Logging (memory ring buffer for the Log tab + %LocalAppData%\SmartHdrManager\log.txt)
        // ------------------------------------------------------------------
        public void Log(string tag, string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {tag,-6} {message}";
            lock (_logLock)
            {
                _log.Add(line);
                if (_log.Count > 500)
                {
                    _log.RemoveRange(0, 100);
                    _logBase += 100;
                }
            }
            WriteFileLog(line);
        }

        private static int _fileLogWrites;
        private const long MaxLogBytes = 2_000_000;

        public static void WriteFileLog(string line)
        {
            try
            {
                lock (FileLogLock)
                {
                    // The app runs for weeks in the tray: keep the log small (old part goes to log.old.txt)
                    if (++_fileLogWrites % 200 == 0 && File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                        File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.txt"), overwrite: true);
                    File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
                }
            }
            catch { }
        }

        /// <summary>Returns log lines added since <paramref name="cursor"/> and advances the cursor.</summary>
        public string[] ReadLog(ref int cursor)
        {
            lock (_logLock)
            {
                int start = Math.Max(cursor - _logBase, 0);
                var lines = _log.Skip(start).ToArray();
                cursor = _logBase + _log.Count;
                return lines;
            }
        }
    }
}
