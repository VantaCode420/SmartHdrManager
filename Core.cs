using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace SmartHdrManager
{
    // =========================================================================
    // 1. CONFIGURATION
    // =========================================================================
    public class AppConfig
    {
        // Format version of this file: lets newer versions fix default lists in existing config.json files once
        // (missing in files from 2.5.0 and older = 0).
        public int ConfigVersion { get; set; } = 0;

        // 2.5: on/off for the weekly scan. 2.6 uses DriveScanIntervalDays (this one is kept in step for older versions).
        public bool AutoScanDrivesOnStartup { get; set; } = true;

        // Only toggle HDR on the primary monitor (main display at desktop coordinates 0,0)
        public bool OnlyPrimaryMonitor { get; set; } = true;

        // Extra folders to search for games (the common Steam / Epic / GOG / Xbox / "Games" folders are found automatically)
        public List<string> CustomGameFolders { get; set; } = new();

        // "Never HDR" (Games tab). Empty by default since 2.6.1 (2.6.0 and older shipped cs2 and valorant here).
        public List<string> IgnoredGames { get; set; } = new();

        // Active HDR list: Only games in this list will automatically trigger HDR (empty: the user picks the games)
        public List<string> HdrGamesAndApps { get; set; } = new();

        // Catalog of all games found on drives (selectable via menu [G] before starting a game)
        public List<string> DiscoveredGames { get; set; } = new();

        // Dedicated local media players (use HdrVideoKeywords)
        public List<string> LocalVideoPlayers { get; set; } = new()
        {
            "vlc",
            "mpc-hc64",
            "potplayermini64",
            "mpv",
            "mpvnet",      // mpv.net
            "mpc-be64",
            "kodi"         // window title has no file name: only "Videos & RTX Video HDR" (Always) can use it
        };

        // Web browsers (never use generic keywords; only react to BrowserRealHdrToken)
        public List<string> Browsers { get; set; } = new()
        {
            "chrome",
            "msedge",
            "firefox",
            "brave"
        };

        // Keywords used ONLY for local video players (not browsers)
        public List<string> HdrVideoKeywords { get; set; } = new()
        {
            "hdr",
            "dolby vision",
            "bt2020",
            "dovi"
            // not "10bit": many SDR encodes (e.g. anime) are 10-bit, that says nothing about HDR
        };

        // Unique token injected by the Tampermonkey script when a real HDR video stream is loaded
        public string BrowserRealHdrToken { get; set; } = "[real_hdr_active]";

        // If false, browser HDR only activates in fullscreen. If true, also in windowed mode.
        public bool EnableHdrForBrowserInWindowedMode { get; set; } = true;

        // Keep HDR on while a browser window still shows the HDR token, even if another window has focus
        // (browser pop-ups, menus, a second monitor). Only keeps HDR on; it never switches HDR on by itself.
        public bool KeepBrowserHdrWhenNotFocused { get; set; } = true;

        // After leaving HDR, tell open windows that the display changed so apps like Chrome stop rendering
        // with their cached HDR brightness (which makes them look dimmed until minimized/restored).
        public bool RefreshAppsAfterSdr { get; set; } = true;

        // When the drives were last scanned for games. The automatic scan at startup runs at most once a week.
        public DateTime? LastDriveScanUtc { get; set; }

        // ---- v2.3 ----
        // Once HDR is on, keep it on at least this long (0 = off). Colour-critical apps and manual SDR still switch at once.
        public int MinimumHdrSeconds { get; set; } = 0;

        // Monitors to switch, by Windows device path. Empty = use OnlyPrimaryMonitor (primary only / all monitors).
        public List<string> SelectedMonitors { get; set; } = new();

        // Per-game options, keyed by process name (lower case)
        public Dictionary<string, GameOptions> PerGameOptions { get; set; } = new();

        // Games found in the Steam / Epic / GOG / Xbox libraries (refreshed at startup and with "Scan drives")
        public List<LibraryGame> LibraryGames { get; set; } = new();

        // Games tab: also list programs from the drive scan that are not in a game library
        public bool ShowOtherPrograms { get; set; } = false;

        // Keyboard shortcuts: "WinAlt" (default, never collides with AltGr typing), "CtrlAlt" or "Off"
        public string HotkeyModifier { get; set; } = HotkeySettings.WinAlt;

        // ---- v2.5: video playback (RTX Video HDR) ----
        // Switch Windows to HDR while a video plays (browsers via the userscript, video players via their audio).
        public bool VideoHdrAutomation { get; set; } = false;
        // "Smart" (HDR videos + SDR videos when RTX Video HDR is on), "NativeOnly" (HDR videos only), "Always" (every video)
        public string VideoHdrMode { get; set; } = VideoHdrModes.Smart;
        // "Fullscreen" (recommended) or "Any" (windowed and full screen)
        public string VideoHdrTrigger { get; set; } = VideoTriggers.Fullscreen;
        public bool VideoHdrInBrowsers { get; set; } = true;
        public bool VideoHdrInPlayers { get; set; } = true;
        // A video must play this long before HDR is switched on (ignores previews and short sounds)
        public int VideoHdrActivationSeconds { get; set; } = 2;
        // After the video stops / pauses: wait this long before going back to SDR (0 s – 5 min, 30 s max before 2.5.2)
        public int VideoHdrRevertSeconds { get; set; } = 5;
        public bool VideoHdrDisableOnBattery { get; set; } = true;
        // While one of these programs runs, videos never switch HDR (a switch would disturb a recording/stream)
        public List<string> VideoHdrBlockingApps { get; set; } = new() { "obs64", "obs32" };
        // ---- v2.5.1 ----
        // While one of the VideoHdrBlockingApps (OBS) runs, never switch HDR on automatically – games included – and go
        // back to SDR when it starts, so a recording is never made in HDR by mistake. Manual overrides still work.
        public bool NoHdrWhileObsRunning { get; set; } = false;
        // When a browser tab with the userscript marker was first seen (Settings shows "Browser script: detected")
        public DateTime? BrowserScriptSeenUtc { get; set; }
        // Videos tab: video players added by hand (full exe paths); installed ones are found automatically
        public List<string> HdrPlayerPaths { get; set; } = new();
        // Marker the userscript adds to the tab title while an SDR video plays
        public string BrowserSdrVideoToken { get; set; } = "[sdr_video_active]";
        // ---- v2.5.2 ----
        // Learned launchers: launcher process -> the game process(es) it started (e.g. "redprelauncher" -> "witcher3").
        // Filled automatically when a watched game closes within seconds and a program from the same folder takes over.
        public Dictionary<string, List<string>> LauncherGames { get; set; } = new();

        // ---- v2.6 ----
        // Apps tab: every program that is not a game but that the app reacts to – process name -> category and mode.
        // Replaces Browsers, LocalVideoPlayers, ForceSdrApps, VideoHdrBlockingApps and NoHdrWhileObsRunning, which are
        // still written (for older versions) but no longer read.
        public Dictionary<string, AppRule> AppRules { get; set; } = AppCatalog.DefaultRules();
        // Off (default): the app only switches back HDR that it turned on itself; HDR turned on by you or another
        // program is left alone. On: the 2.5 behaviour – back to SDR whenever nothing in the lists needs HDR.
        public bool KeepDesktopSdr { get; set; } = false;
        // Automatic drive scan for newly installed games: -1 = off, 0 = at every start, N = every N days (default weekly)
        public int DriveScanIntervalDays { get; set; } = 7;
        // Look for a new version on GitHub once a week. Off by default: no internet connection unless asked for.
        public bool CheckForUpdatesAutomatically { get; set; } = false;
        public DateTime? LastUpdateCheckUtc { get; set; }
        public string? LastNotifiedVersion { get; set; }          // the automatic check mentions each version only once
        // Games that closed within seconds after HDR was switched on for them (possible crash, e.g. with NVIDIA RTX HDR)
        public Dictionary<string, int> QuickExitCounts { get; set; } = new();

        // Full exe paths of programs added by hand or by hotkey (for their icons in the Games tab)
        public Dictionary<string, string> ProgramPaths { get; set; } = new();

        public List<string> ForceSdrApps { get; set; } = new()
        {
            "photoshop",
            "lightroom",
            "resolve",
            "premiere",
            "adobe premiere pro",   // the real process name of Premiere Pro
            "afterfx"               // After Effects
        };

        public bool EnableHdrOnProcessStart { get; set; } = true;
        public int ProcessScanIntervalMs { get; set; } = 100;
        // Off by default: the Windows task it uses needs administrator rights on most PCs
        public bool ReloadColorProfileAfterSdr { get; set; } = false;
        public bool RequireKeywordForFullscreenVideo { get; set; } = true;
        public int SdrFallbackDelaySeconds { get; set; } = 3;
    }

    public sealed class GameOptions
    {
        public int HdrDelaySeconds { get; set; }          // wait this long after the game starts before switching HDR on
        public int KeepHdrAfterExitSeconds { get; set; }  // keep HDR on this long after the game closes

        [JsonIgnore]
        public bool IsDefault => HdrDelaySeconds <= 0 && KeepHdrAfterExitSeconds <= 0;
    }

    public sealed class LibraryGame
    {
        public string Id { get; set; } = string.Empty;          // e.g. "steam:1091500"
        public string Name { get; set; } = string.Empty;        // display name, e.g. "Cyberpunk 2077"
        public string Source { get; set; } = string.Empty;      // Steam / Epic / GOG / Xbox / Ubisoft / EA app / Rockstar / Battle.net / Amazon Games / itch.io / Riot Games / Heroic (…) / Game Bar
        public List<string> Processes { get; set; } = new();    // process names (exe without .exe, lower case)
        public string? ExePath { get; set; }                    // main exe, used for the icon
        public int ScanVersion { get; set; }                    // GameLibraryScanner.CurrentScanVersion when it was detected
    }

    /// <summary>
    /// Keyboard shortcut settings. Ctrl+Alt is the same as AltGr on many keyboard layouts (e.g. AltGr+O types "ó" on a
    /// Polish keyboard), so global Ctrl+Alt shortcuts can block typing; Win+Alt never does and is the default.
    /// </summary>
    public static class HotkeySettings
    {
        public const string WinAlt = "WinAlt";
        public const string CtrlAlt = "CtrlAlt";
        public const string Off = "Off";

        public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_WIN = 0x0008;

        public static string Normalize(string? mode) =>
            string.Equals(mode, CtrlAlt, StringComparison.OrdinalIgnoreCase) ? CtrlAlt
            : string.Equals(mode, Off, StringComparison.OrdinalIgnoreCase) ? Off
            : WinAlt;

        public static uint ModifierFlags(string mode) =>
            Normalize(mode) == CtrlAlt ? MOD_CONTROL | MOD_ALT : MOD_WIN | MOD_ALT;

        /// <summary>"Win+Alt+O", "Ctrl+Alt+O", or "" when shortcuts are off.</summary>
        public static string Describe(string mode, char key) => Normalize(mode) switch
        {
            CtrlAlt => $"Ctrl+Alt+{key}",
            Off => string.Empty,
            _ => $"Win+Alt+{key}"
        };
    }

    /// <summary>
    /// Exe path of another process using only PROCESS_QUERY_LIMITED_INFORMATION (the same access Task Manager uses).
    /// Never reads the process's memory (Process.MainModule does), which matters for games protected by kernel
    /// anti-cheat systems: they watch for handles with memory access to the game.
    /// </summary>
    public static class ProcessPaths
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static string? TryGetExePath(int processId)
        {
            if (processId <= 0) return null;
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                uint size = (uint)buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }
    }

    /// <summary>The real Windows version for the log, e.g. "Windows 11 25H2 (build 26200.9711)".</summary>
    public static class WindowsVersion
    {
        private static string? _cached;

        public static string Describe()
        {
            if (_cached != null) return _cached;
            string result;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                string display = (key?.GetValue("DisplayVersion") as string ?? string.Empty).Trim();   // "25H2"
                string buildText = (key?.GetValue("CurrentBuild") as string ?? string.Empty).Trim();   // "26200"
                object? ubr = key?.GetValue("UBR");                                                        // 9711
                int build = int.TryParse(buildText, out int b) ? b : Environment.OSVersion.Version.Build;
                // "ProductName" still says "Windows 10" on Windows 11, so the build number decides
                string name = build >= 22000 ? "Windows 11" : "Windows 10";
                string fullBuild = ubr is int u ? $"{build}.{u}" : build.ToString();
                result = display.Length > 0 ? $"{name} {display} (build {fullBuild})" : $"{name} (build {fullBuild})";
            }
            catch
            {
                result = $"Windows (build {Environment.OSVersion.Version.Build})";
            }
            return _cached = result;
        }
    }

    /// <summary>Which monitors to switch: a set of device paths, or "primary only" / "all".</summary>
    public sealed class MonitorFilter
    {
        public MonitorFilter(bool onlyPrimary, IEnumerable<string>? devicePaths)
        {
            OnlyPrimary = onlyPrimary;
            var paths = devicePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            DevicePaths = paths != null && paths.Count > 0 ? new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase) : null;
        }

        public bool OnlyPrimary { get; }
        public HashSet<string>? DevicePaths { get; }   // null = not customised

        public string Describe() =>
            DevicePaths != null
                ? (DevicePaths.Count == 1 ? "1 selected monitor" : $"{DevicePaths.Count} selected monitors")
                : OnlyPrimary ? "primary monitor" : "all monitors";
    }

    /// <summary>A display's colour output at one moment (for the "after SDR" check).</summary>
    public sealed class DisplayColorState
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string GdiName { get; init; } = string.Empty;
        public bool HdrOn { get; init; }
        public int Encoding { get; init; } = -1;   // 0 RGB, 1 YCbCr 4:4:4, 2 YCbCr 4:2:2, 3 YCbCr 4:2:0
        public uint Bits { get; init; }
        public int Mode { get; init; } = -1;       // 24H2+: 0 SDR, 1 WCG (Auto Color Management), 2 HDR; -1 unknown
        public ushort[]? Ramp { get; init; }        // gamma curve: 256 red, 256 green, 256 blue

        public bool RampIsIdentity => Ramp != null && RampDistance(Ramp, null) <= 300;

        public static string EncodingName(int e) => e switch
        {
            0 => "RGB", 1 => "YCbCr 4:4:4", 2 => "YCbCr 4:2:2", 3 => "YCbCr 4:2:0", 4 => "Intensity", _ => "unknown format"
        };

        public static string ModeName(int m) => m switch
        {
            0 => "SDR", 1 => "WCG / Auto Color Management", 2 => "HDR", _ => "unknown mode"
        };

        public string Describe() =>
            $"{EncodingName(Encoding)}, {(Bits > 0 ? $"{Bits}-bit" : "? bit")}" + (Mode >= 0 ? $", {ModeName(Mode)}" : "") +
            (Ramp == null ? ", gamma curve unreadable" : RampIsIdentity ? ", neutral gamma curve" : ", custom gamma curve");

        /// <summary>Largest difference between two curves (null = the neutral curve).</summary>
        public static int RampDistance(ushort[] a, ushort[]? b)
        {
            int max = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int other = b != null ? b[i] : (i % 256) * 257;
                max = Math.Max(max, Math.Abs(a[i] - other));
            }
            return max;
        }
    }

    public sealed class MonitorInfo
    {
        public string DevicePath { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsPrimary { get; init; }
        public bool HdrSupported { get; init; }
        public bool HdrEnabled { get; init; }
    }

    // =========================================================================
    // 3. AUTOMATIC GAME SCANNER FOR ALL DRIVES
    // =========================================================================
    public static class GameScanner
    {
        private static readonly string[] BlacklistSubstrings = new[]
        {
            "unins", "setup", "redist", "vcredist", "dxsetup", "directx",
            "crash", "report", "handler", "launcher", "eac", "easyanticheat",
            "battleye", "beservice", "beclient", "cefprocess", "webhelper",
            "benchmark", "config", "updater", "patcher", "cleanup", "touchup",
            "activation", "installer", "dotnet", "physx", "ue4prereq", "ueprereq",
            "steamworks", "eosoverlay", "gamingservices", "unitycrashhandler"
        };

        /// <summary>
        /// Scans all drives and adds found executables to DiscoveredGames
        /// so the user can conveniently select which ones should use HDR via menu [G].
        /// </summary>
        public static int ScanAndAddGames(AppConfig config, Dictionary<string, string>? foundPaths = null)
        {
            var searchRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in config.CustomGameFolders)
            {
                if (Directory.Exists(folder)) searchRoots.Add(folder);
            }

            foreach (var steamLib in GetSteamLibraryDirectories())
            {
                searchRoots.Add(steamLib);
            }

            string[] commonFolderNames = new[]
            {
                @"SteamLibrary\steamapps\common",
                @"Program Files (x86)\Steam\steamapps\common",
                @"Program Files\Epic Games",
                @"Epic Games",
                @"Games",
                @"Spiele",
                @"XboxGames",
                @"GOG Games",
                @"Program Files (x86)\GOG Galaxy\Games",
                @"Program Files\EA Games",
                @"EA Games",
                @"Ubisoft Game Launcher\games",
                @"Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games",
                @"Battle.net"
            };

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                foreach (var relPath in commonFolderNames)
                {
                    string fullPath = Path.Combine(drive.RootDirectory.FullName, relPath);
                    if (Directory.Exists(fullPath))
                    {
                        searchRoots.Add(fullPath);
                    }
                }
            }

            var discoveredSet = new HashSet<string>(config.DiscoveredGames, StringComparer.OrdinalIgnoreCase);
            var ignoredSet = new HashSet<string>(config.IgnoredGames, StringComparer.OrdinalIgnoreCase);
            int newlyDiscoveredCount = 0;

            foreach (var root in searchRoots)
            {
                try
                {
                    foreach (var gameDir in Directory.EnumerateDirectories(root))
                    {
                        string dirName = Path.GetFileName(gameDir);
                        if (dirName.Contains("Steamworks", StringComparison.OrdinalIgnoreCase) ||
                            dirName.Contains("Redist", StringComparison.OrdinalIgnoreCase))
                            continue;

                        foreach (var exePath in EnumerateExeFilesSafe(gameDir, maxDepth: 4))
                        {
                            string exeName = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
                            if (!IsValidGameExecutable(exePath, exeName)) continue;

                            // Remember where each program lives (for its icon in the Games tab)
                            if (foundPaths != null && !foundPaths.ContainsKey(exeName))
                                foundPaths[exeName] = exePath;

                            if (!discoveredSet.Contains(exeName) && !ignoredSet.Contains(exeName))
                            {
                                config.DiscoveredGames.Add(exeName);
                                discoveredSet.Add(exeName);
                                newlyDiscoveredCount++;
                            }
                        }
                    }
                }
                catch { }
            }

            if (newlyDiscoveredCount > 0)
            {
                config.DiscoveredGames.Sort();
            }

            return newlyDiscoveredCount;
        }

        private static IEnumerable<string> GetSteamLibraryDirectories()
        {
            var results = new List<string>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                string? steamPath = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrEmpty(steamPath))
                {
                    steamPath = steamPath.Replace('/', '\\');
                    string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdfPath))
                    {
                        string content = File.ReadAllText(vdfPath);
                        var matches = Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\"");
                        foreach (Match match in matches)
                        {
                            string libPath = match.Groups[1].Value.Replace("\\\\", "\\");
                            string commonPath = Path.Combine(libPath, "steamapps", "common");
                            if (Directory.Exists(commonPath))
                            {
                                results.Add(commonPath);
                            }
                        }
                    }
                }
            }
            catch { }
            return results;
        }

        private static IEnumerable<string> EnumerateExeFilesSafe(string rootPath, int maxDepth)
        {
            var results = new List<string>();
            var dirs = new Stack<(string Path, int Depth)>();
            dirs.Push((rootPath, 0));

            while (dirs.Count > 0)
            {
                var (currentDir, depth) = dirs.Pop();
                try
                {
                    results.AddRange(Directory.GetFiles(currentDir, "*.exe"));
                    if (depth < maxDepth)
                    {
                        foreach (var subDir in Directory.GetDirectories(currentDir))
                        {
                            dirs.Push((subDir, depth + 1));
                        }
                    }
                }
                catch { }
            }
            return results;
        }

        private static bool IsValidGameExecutable(string fullPath, string exeName)
        {
            if (exeName.Length < 2) return false;
            foreach (var badWord in BlacklistSubstrings)
            {
                if (exeName.Contains(badWord)) return false;
            }

            try
            {
                var info = new FileInfo(fullPath);
                if (info.Length < 300 * 1024) return false;
            }
            catch { return false; }

            return true;
        }
    }

    // =========================================================================
    // 4. WINDOWS DISPLAYCONFIG API (native HDR control + Primary Monitor Filter)
    // =========================================================================
    public static class HdrController
    {
        private const int QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const int ERROR_SUCCESS = 0;
        private const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
        private const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xffffffff;

        private enum DISPLAYCONFIG_DEVICE_INFO_TYPE : int
        {
            GET_SOURCE_NAME = 1,
            GET_TARGET_NAME = 2,
            GET_ADVANCED_COLOR_INFO = 9,
            GET_ADVANCED_COLOR_INFO_2 = 15,   // Windows 11 24H2+
            SET_HDR_STATE = 16,               // Windows 11 24H2+
            SET_ADVANCED_COLOR_STATE = 10
        }

        [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public DISPLAYCONFIG_DEVICE_INFO_TYPE type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;
            public int colorEncoding;
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint enableAdvancedColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
            public uint rotation; public uint scaling;
            public uint refreshRateNumerator; public uint refreshRateDenominator;
            public uint scanLineOrdering;
            public int targetAvailable; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType; public uint id; public LUID adapterId;
            public ulong u0, u1, u2, u3, u4, u5;
        }

        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
        [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray, ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public uint outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        /// <summary>Friendly name (e.g. "LG ULTRAGEAR") and stable device path of a display target.</summary>
        private static (string Name, string DevicePath) GetTargetName(LUID adapterId, uint targetId)
        {
            var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                monitorFriendlyDeviceName = string.Empty,
                monitorDevicePath = string.Empty
            };
            request.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_TARGET_NAME;
            request.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            request.header.adapterId = adapterId;
            request.header.id = targetId;

            if (DisplayConfigGetDeviceInfo(ref request) != ERROR_SUCCESS)
                return (string.Empty, string.Empty);
            return (request.monitorFriendlyDeviceName ?? string.Empty, request.monitorDevicePath ?? string.Empty);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;   // e.g. "\\.\DISPLAY1"
        }

        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public int monLeft, monTop, monRight, monBottom, workLeft, workTop, workRight, workBottom;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        private static string GetSourceGdiName(DISPLAYCONFIG_PATH_INFO path)
        {
            var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME { viewGdiDeviceName = string.Empty };
            request.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_SOURCE_NAME;
            request.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
            request.header.adapterId = path.sourceInfo.adapterId;
            request.header.id = path.sourceInfo.id;
            return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS ? request.viewGdiDeviceName ?? string.Empty : string.Empty;
        }

        /// <summary>Keys of the displays a filter switches right now (same rules as switching).</summary>
        public static HashSet<string> ResolveTargetKeys(MonitorFilter filter)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (QueryActivePaths(out var paths, out var modes) != ERROR_SUCCESS) return keys;
            var matches = BuildMatcher(paths, modes, filter);
            foreach (var path in paths)
                if (matches(path)) keys.Add(MonitorKey(path));
            return keys;
        }

        /// <summary>
        /// For video HDR: the display a window is on (by its HMONITOR). Returns its key if that display is
        /// HDR-capable and one of the monitors allowed in Settings; otherwise null with the reason.
        /// </summary>
        public static string? ResolveVideoMonitor(IntPtr hMonitor, MonitorFilter allowed, out string problem)
        {
            problem = string.Empty;
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
            if (hMonitor == IntPtr.Zero || !GetMonitorInfo(hMonitor, ref info) || string.IsNullOrEmpty(info.szDevice))
            {
                problem = "the monitor of the video window could not be determined";
                return null;
            }
            if (QueryActivePaths(out var paths, out var modes) != ERROR_SUCCESS)
            {
                problem = "the display configuration could not be read";
                return null;
            }

            var matches = BuildMatcher(paths, modes, allowed);
            foreach (var path in paths)
            {
                if (!string.Equals(GetSourceGdiName(path), info.szDevice, StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool supported, out _) || !supported)
                    continue;   // cloned displays share a source: keep looking for an HDR-capable one
                if (!matches(path))
                {
                    problem = "the video is on a monitor that is not switched on under Settings › Monitors";
                    return null;
                }
                return MonitorKey(path);
            }
            if (problem.Length == 0) problem = "the video is on a monitor without HDR";
            return null;
        }

        /// <summary>Stable key of a display: its device path, or adapter+target id when Windows reports none.</summary>
        private static string MonitorKey(DISPLAYCONFIG_PATH_INFO path)
        {
            string devicePath = GetTargetName(path.targetInfo.adapterId, path.targetInfo.id).DevicePath;
            return devicePath.Length > 0 ? devicePath : $"{path.targetInfo.adapterId.LowPart}:{path.targetInfo.id}";
        }

        /// <summary>
        /// Decides which active display paths are switched. With a monitor selection: the selected ones; if none of the
        /// selected monitors is connected right now, it falls back to "primary only" / "all" instead of switching nothing.
        /// </summary>
        private static Func<DISPLAYCONFIG_PATH_INFO, bool> BuildMatcher(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, MonitorFilter filter)
        {
            if (filter.DevicePaths != null)
            {
                var keys = paths.Select(MonitorKey).ToArray();
                if (keys.Any(filter.DevicePaths.Contains))
                {
                    var selected = new HashSet<string>(keys.Where(filter.DevicePaths.Contains), StringComparer.OrdinalIgnoreCase);
                    return p => selected.Contains(MonitorKey(p));
                }
            }
            return p => !filter.OnlyPrimary || IsPrimaryDisplayPath(p, modes);
        }

        /// <summary>All active displays (one entry per monitor), for the monitor list in Settings.</summary>
        public static List<MonitorInfo> ListMonitors()
        {
            var result = new List<MonitorInfo>();
            if (QueryActivePaths(out var paths, out var modes) != ERROR_SUCCESS) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var found = new List<(string Key, string Name, bool Primary, bool Supported, bool Enabled, int X, int Y)>();
            int number = 0;
            foreach (var path in paths)
            {
                number++;
                var (name, _) = GetTargetName(path.targetInfo.adapterId, path.targetInfo.id);
                string key = MonitorKey(path);
                if (!seen.Add(key)) continue;   // cloned displays appear once
                bool ok = TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool supported, out bool enabled);
                TryGetSourcePosition(path, modes, out int x, out int y);
                found.Add((key, string.IsNullOrWhiteSpace(name) ? $"Display {number}" : name.Trim(),
                           IsPrimaryDisplayPath(path, modes), ok && supported, ok && enabled, x, y));
            }

            // Two identical monitors have the same friendly name: tell them apart by where they sit on the desktop.
            // Only the label changes – the HDR switches are keyed by the device path.
            var labels = found.Select(f => f.Name).ToArray();
            foreach (var group in found.Select((f, i) => (f, i)).GroupBy(t => t.f.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var members = group.OrderBy(t => t.f.X).ThenBy(t => t.f.Y).ToList();
                bool distinctX = members.Select(t => t.f.X).Distinct().Count() == members.Count;
                bool distinctY = members.Select(t => t.f.Y).Distinct().Count() == members.Count;
                if (members.Count == 2 && distinctX)
                {
                    labels[members[0].i] += " (left)";
                    labels[members[1].i] += " (right)";
                }
                else if (members.Count == 2 && distinctY)
                {
                    members = members.OrderBy(t => t.f.Y).ToList();
                    labels[members[0].i] += " (top)";
                    labels[members[1].i] += " (bottom)";
                }
                else
                {
                    for (int n = 0; n < members.Count; n++)
                        labels[members[n].i] += $" #{n + 1}";   // numbered left to right
                }
            }

            for (int i = 0; i < found.Count; i++)
            {
                var f = found[i];
                result.Add(new MonitorInfo
                {
                    DevicePath = f.Key,
                    Name = labels[i],
                    IsPrimary = f.Primary,
                    HdrSupported = f.Supported,
                    HdrEnabled = f.Enabled
                });
            }
            return result;
        }
        [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE setPacket);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);
        [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_HDR_STATE setPacket);

        // Windows 11 24H2 separated "HDR" from "advanced colour" (Auto Color Management, ACM). With the old call, an SDR
        // monitor that only supports ACM looks HDR-capable and switching it toggles ACM. The new calls report and switch
        // HDR itself. They are used when Windows supports them; older Windows versions use the old calls.
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;               // bit 4: highDynamicRangeSupported, bit 5: highDynamicRangeUserEnabled
            public int colorEncoding;
            public uint bitsPerColorChannel;
            public int activeColorMode;      // 0 = SDR, 1 = WCG, 2 = HDR
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_HDR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value;               // bit 0: enableHdr
        }

        private const int ERROR_NOT_SUPPORTED = 50;
        private const int ERROR_INVALID_PARAMETER = 87;
        private static volatile int _hdrApi = -1;   // -1 unknown, 0 legacy (before 24H2), 1 new HDR API

        /// <summary>Which Windows HDR API is used (for the log).</summary>
        public static string HdrApiName => _hdrApi switch
        {
            1 => "modern HDR API (Windows 11 24H2 or newer)",
            0 => "classic advanced-colour API",
            _ => "not determined yet"
        };

        /// <summary>HDR support / state of one display, preferring the 24H2+ API.</summary>
        private static bool TryGetHdrState(LUID adapterId, uint targetId, out bool supported, out bool enabled) =>
            TryGetHdrState(adapterId, targetId, out supported, out enabled, out _);

        /// <param name="usedNewApi">which API answered, so the switch uses the same one</param>
        private static bool TryGetHdrState(LUID adapterId, uint targetId, out bool supported, out bool enabled, out bool usedNewApi)
        {
            supported = false;
            enabled = false;
            usedNewApi = false;

            if (_hdrApi != 0)
            {
                var info2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
                info2.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADVANCED_COLOR_INFO_2;
                info2.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>();
                info2.header.adapterId = adapterId;
                info2.header.id = targetId;

                int rc = DisplayConfigGetDeviceInfo(ref info2);
                if (rc == ERROR_SUCCESS)
                {
                    _hdrApi = 1;
                    usedNewApi = true;
                    supported = (info2.value & 0x10) != 0;
                    enabled = supported && ((info2.value & 0x20) != 0 || info2.activeColorMode == 2);
                    return true;
                }

                // Only decide "old Windows" before the new API ever worked. Once it has worked, an error is transient
                // (e.g. a monitor disappearing during hot-plug or resume) and must not switch APIs for the session.
                if (_hdrApi == -1 && (rc == ERROR_INVALID_PARAMETER || rc == ERROR_NOT_SUPPORTED) && !IsWindows24H2OrNewer)
                    _hdrApi = 0;
                if (_hdrApi == 1)
                    return false;   // new API known to exist: don't mix in the classic one for this display
            }

            var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            info.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADVANCED_COLOR_INFO;
            info.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
            info.header.adapterId = adapterId;
            info.header.id = targetId;
            if (DisplayConfigGetDeviceInfo(ref info) != ERROR_SUCCESS) return false;

            supported = (info.value & 0x1) != 0;
            enabled = supported && (info.value & 0x2) != 0;
            return true;
        }

        // ------------------------------------------------------------------
        // Colour state diagnostics (what changed between "before HDR" and "after SDR")
        // ------------------------------------------------------------------
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr initData);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] ramp);
        [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

        /// <summary>Colour format, colour mode and gamma curve of every display a filter switches.</summary>
        public static List<DisplayColorState> CaptureColorState(MonitorFilter filter)
        {
            var result = new List<DisplayColorState>();
            if (QueryActivePaths(out var paths, out var modes) != ERROR_SUCCESS) return result;
            var matches = BuildMatcher(paths, modes, filter);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (!matches(path)) continue;
                string key = MonitorKey(path);
                if (!seen.Add(key)) continue;
                if (!TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool supported, out bool hdrOn) || !supported) continue;

                ReadColorInfo(path.targetInfo.adapterId, path.targetInfo.id, out int encoding, out uint bits, out int mode);
                string gdi = GetSourceGdiName(path);
                var (name, _) = GetTargetName(path.targetInfo.adapterId, path.targetInfo.id);
                result.Add(new DisplayColorState
                {
                    Key = key,
                    Name = string.IsNullOrWhiteSpace(name) ? gdi : name.Trim(),
                    GdiName = gdi,
                    HdrOn = hdrOn,
                    Encoding = encoding,
                    Bits = bits,
                    Mode = mode,
                    Ramp = ReadGammaRamp(gdi)
                });
            }
            return result;
        }

        private static void ReadColorInfo(LUID adapterId, uint targetId, out int encoding, out uint bits, out int mode)
        {
            encoding = -1; bits = 0; mode = -1;
            if (_hdrApi == 1)
            {
                var info2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
                info2.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADVANCED_COLOR_INFO_2;
                info2.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>();
                info2.header.adapterId = adapterId;
                info2.header.id = targetId;
                if (DisplayConfigGetDeviceInfo(ref info2) == ERROR_SUCCESS)
                {
                    encoding = info2.colorEncoding; bits = info2.bitsPerColorChannel; mode = info2.activeColorMode;
                    return;
                }
            }
            var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
            info.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.GET_ADVANCED_COLOR_INFO;
            info.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
            info.header.adapterId = adapterId;
            info.header.id = targetId;
            if (DisplayConfigGetDeviceInfo(ref info) == ERROR_SUCCESS)
            {
                encoding = info.colorEncoding; bits = info.bitsPerColorChannel;
            }
        }

        private static ushort[]? ReadGammaRamp(string gdiName)
        {
            if (string.IsNullOrEmpty(gdiName)) return null;
            IntPtr hdc = CreateDC("DISPLAY", gdiName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return null;
            try
            {
                var ramp = new ushort[3 * 256];
                return GetDeviceGammaRamp(hdc, ramp) ? ramp : null;
            }
            finally
            {
                DeleteDC(hdc);
            }
        }

        /// <summary>Puts a display's gamma curve (calibration, night light, NVIDIA colour settings) back. No admin rights needed.</summary>
        public static bool RestoreGammaRamp(DisplayColorState state)
        {
            if (state.Ramp == null || string.IsNullOrEmpty(state.GdiName)) return false;
            IntPtr hdc = CreateDC("DISPLAY", state.GdiName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;
            try { return SetDeviceGammaRamp(hdc, state.Ramp); }
            finally { DeleteDC(hdc); }
        }

        /// <summary>Windows 11 24H2 is build 26100; the new HDR requests exist from there on.</summary>
        private static readonly bool IsWindows24H2OrNewer = Environment.OSVersion.Version.Build >= 26100;

        /// <summary>Switches HDR on one display with the same API that reported its state.</summary>
        private static int SetHdrOnDisplay(LUID adapterId, uint targetId, bool enable, bool useNewApi)
        {
            if (useNewApi)
            {
                var set = new DISPLAYCONFIG_SET_HDR_STATE();
                set.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.SET_HDR_STATE;
                set.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_HDR_STATE>();
                set.header.adapterId = adapterId;
                set.header.id = targetId;
                set.value = enable ? 1u : 0u;
                return DisplayConfigSetDeviceInfo(ref set);
            }

            var legacy = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE();
            legacy.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.SET_ADVANCED_COLOR_STATE;
            legacy.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>();
            legacy.header.adapterId = adapterId;
            legacy.header.id = targetId;
            legacy.enableAdvancedColor = enable ? 1u : 0u;
            return DisplayConfigSetDeviceInfo(ref legacy);
        }

        public static string LastError { get; private set; } = string.Empty;

        /// <summary>
        /// Checks whether the display path belongs to the Windows primary monitor (position 0,0).
        /// </summary>
        private static bool IsPrimaryDisplayPath(DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes)
            => TryGetSourcePosition(path, modes, out int posX, out int posY) && posX == 0 && posY == 0;

        /// <summary>Desktop position (top-left corner) of the display's source mode.</summary>
        private static bool TryGetSourcePosition(DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes, out int posX, out int posY)
        {
            posX = posY = 0;
            uint idx = path.sourceInfo.modeInfoIdx;
            if (idx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID || idx >= modes.Length)
                return false;

            var mode = modes[idx];
            if (mode.infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                return false;

            // DISPLAYCONFIG_SOURCE_MODE layout inside union (u0, u1, u2):
            // u0: width (32 bit), height (32 bit)
            // u1: pixelFormat (32 bit), position.x (32 bit signed)
            // u2: position.y (32 bit signed)
            posX = (int)(mode.u1 >> 32);
            posY = (int)(mode.u2 & 0xFFFFFFFF);
            return true;
        }

        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        /// <summary>
        /// Reads the active display paths. Retries when the configuration changes between the two calls
        /// (monitor hot-plug, mode change), which Windows reports as ERROR_INSUFFICIENT_BUFFER.
        /// </summary>
        private static int QueryActivePaths(out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
        {
            paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>();
            modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
            int rc = ERROR_SUCCESS;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                rc = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
                if (rc != ERROR_SUCCESS) return rc;

                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

                rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
                if (rc == ERROR_SUCCESS)
                {
                    if (pathCount < paths.Length) Array.Resize(ref paths, (int)pathCount);
                    if (modeCount < modes.Length) Array.Resize(ref modes, (int)modeCount);
                    return rc;
                }
                if (rc != ERROR_INSUFFICIENT_BUFFER) return rc;
            }
            return rc;
        }

        public static bool SetHdrState(bool enableHdr, bool onlyPrimaryMonitor) =>
            SetHdrState(enableHdr, new MonitorFilter(onlyPrimaryMonitor, null));

        public static bool SetHdrState(bool enableHdr, MonitorFilter filter)
        {
            LastError = string.Empty;

            int rc = QueryActivePaths(out var paths, out var modes);
            if (rc != ERROR_SUCCESS)
            {
                LastError = $"Reading the display configuration failed (code {rc})";
                return false;
            }

            bool success = false;
            bool anyFailed = false;   // one display refusing counts as failure, so the engine's retry cooldown applies
            int supportedDisplays = 0;

            var matches = BuildMatcher(paths, modes, filter);
            foreach (var path in paths)
            {
                if (!matches(path))
                    continue;

                if (TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool isHdrSupported, out bool isHdrEnabled, out bool newApi))
                {
                    if (isHdrSupported)
                    {
                        supportedDisplays++;

                        if (isHdrEnabled == enableHdr)
                        {
                            success = true;
                            continue;
                        }

                        int res = SetHdrOnDisplay(path.targetInfo.adapterId, path.targetInfo.id, enableHdr, newApi);
                        if (res == ERROR_SUCCESS)
                        {
                            success = true;
                        }
                        else
                        {
                            anyFailed = true;
                            LastError = $"DisplayConfigSetDeviceInfo failed (code {res})";
                        }
                    }
                }
            }

            if (!success && string.IsNullOrEmpty(LastError))
            {
                LastError = supportedDisplays == 0
                    ? "No active HDR-capable display found among the monitors selected for switching"
                    : "HDR could not be switched";
            }
            return success && !anyFailed;
        }

        public static bool IsHdrCurrentlyEnabled(bool onlyPrimaryMonitor)
        {
            QueryHdrState(new MonitorFilter(onlyPrimaryMonitor, null), out bool anyEnabled, out _);
            return anyEnabled;
        }

        /// <summary>
        /// anyEnabled: at least one HDR-capable (primary) display has HDR on.
        /// allEnabled: every HDR-capable (primary) display has HDR on. Used so that in all-monitors mode a second
        /// monitor that is still in SDR also gets switched on. Returns false if the configuration could not be read.
        /// </summary>
        public static bool QueryHdrState(MonitorFilter filter, out bool anyEnabled, out bool allEnabled)
        {
            anyEnabled = false;
            allEnabled = false;

            if (QueryActivePaths(out var paths, out var modes) != ERROR_SUCCESS)
                return false;

            int supported = 0;
            int enabled = 0;

            var matches = BuildMatcher(paths, modes, filter);
            foreach (var path in paths)
            {
                if (!matches(path))
                    continue;

                if (TryGetHdrState(path.targetInfo.adapterId, path.targetInfo.id, out bool isHdrSupported, out bool isHdrEnabled) &&
                    isHdrSupported)
                {
                    supported++;
                    if (isHdrEnabled) enabled++;
                }
            }

            anyEnabled = enabled > 0;
            allEnabled = supported > 0 && enabled == supported;
            return true;
        }
    }

    // =========================================================================
    // 5. WINDOW, FULLSCREEN & HOTKEY DETECTOR
    // =========================================================================
    public static class WindowDetector
    {
        /// <summary>The monitor (HMONITOR) of the window in front.</summary>
        public static IntPtr ForegroundMonitor() => MonitorFromWindow(GetForegroundWindow(), MONITOR_DEFAULTTONEAREST);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder strText, int maxCount);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12; // ALT key
        private const int VK_H = 0x48;
        private const int VK_I = 0x49;

        private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

        private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>Polling fallback (only used if RegisterHotKey failed): modifier combination + key held down.</summary>
        public static bool IsHotkeyPressed(string mode, int vk)
        {
            switch (HotkeySettings.Normalize(mode))
            {
                case HotkeySettings.Off:
                    return false;
                case HotkeySettings.CtrlAlt:
                    return Down(VK_CONTROL) && Down(VK_MENU) && Down(vk);
                default:
                    return (Down(VK_LWIN) || Down(VK_RWIN)) && Down(VK_MENU) && Down(vk);
            }
        }

        [ThreadStatic] private static StringBuilder? _titleBuffer;
        private static readonly object _pidLock = new();
        private static IntPtr _cachedHwnd;
        private static uint _cachedPid;
        private static string _cachedName = string.Empty;

        /// <summary>
        /// Process.GetProcessById opens a process handle and builds a Process object. The foreground process rarely
        /// changes, so the name is only looked up again when the foreground window or its PID changes
        /// (keying on the window as well avoids returning a stale name if Windows reuses a PID).
        /// </summary>
        private static string GetProcessNameCached(IntPtr hWnd, uint pid)
        {
            if (pid == 0) return string.Empty;

            lock (_pidLock)
            {
                if (hWnd == _cachedHwnd && pid == _cachedPid && _cachedName.Length > 0)
                    return _cachedName;
            }

            string name = ResolveProcessName(pid);

            if (name.Length > 0)
            {
                lock (_pidLock)
                {
                    _cachedHwnd = hWnd;
                    _cachedPid = pid;
                    _cachedName = name;
                }
            }
            return name;
        }

        /// <summary>Full path of the foreground window's executable, or null (e.g. for elevated processes).</summary>
        public static string? GetForegroundExePath()
        {
            try
            {
                IntPtr hWnd = GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return null;
                GetWindowThreadProcessId(hWnd, out uint pid);
                return ProcessPaths.TryGetExePath((int)pid);   // limited access only (anti-cheat safe)
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveProcessName(uint pid)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                return proc.ProcessName.ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        public static (string ProcessName, string WindowTitle, bool IsFullscreen) GetActiveWindowState()
        {
            var (processName, title, isFullscreen, _) = GetActiveWindowInfo();
            return (processName, title, isFullscreen);
        }

        /// <summary>Like <see cref="GetActiveWindowState"/>, plus the process id of the same window (0 = no window).</summary>
        public static (string ProcessName, string WindowTitle, bool IsFullscreen, int ProcessId) GetActiveWindowInfo()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero) return (string.Empty, string.Empty, false, 0);

            // One reusable buffer per thread instead of a new StringBuilder 10x per second
            var sb = _titleBuffer ??= new StringBuilder(512);
            sb.Clear();
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            GetWindowThreadProcessId(hWnd, out uint pid);
            string processName = GetProcessNameCached(hWnd, pid);

            bool isFullscreen = processName != "explorer" && processName != "searchhost" && IsWindowFullscreen(hWnd);

            return (processName, title, isFullscreen, (int)pid);
        }

        private static bool IsWindowFullscreen(IntPtr hWnd)
        {
            if (!GetWindowRect(hWnd, out RECT winRect)) return false;

            IntPtr hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            var monInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMonitor, ref monInfo)) return false;

            int winWidth = winRect.Right - winRect.Left;
            int winHeight = winRect.Bottom - winRect.Top;
            int monWidth = monInfo.rcMonitor.Right - monInfo.rcMonitor.Left;
            int monHeight = monInfo.rcMonitor.Bottom - monInfo.rcMonitor.Top;
            return winWidth >= monWidth && winHeight >= monHeight;
        }

        private const int WM_DISPLAYCHANGE = 0x007E;
        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
                           SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200, SWP_ASYNCWINDOWPOS = 0x4000;
        private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_FRAME = 0x0400;

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

        /// <summary>
        /// Posts WM_DISPLAYCHANGE to all top-level windows. Switching HDR off does not change the resolution, so Windows
        /// often does not send it; Chromium-based apps (Chrome, Edge, Discord, VS Code, ...) re-read the display's colour
        /// space and SDR brightness when they receive it. Non-blocking (PostMessage), so hung windows cannot stall us.
        /// </summary>
        public static void BroadcastDisplayChange()
        {
            int width = GetSystemMetrics(0);    // SM_CXSCREEN
            int height = GetSystemMetrics(1);   // SM_CYSCREEN
            var lParam = new IntPtr((height << 16) | (width & 0xFFFF));
            PostMessage(HWND_BROADCAST, WM_DISPLAYCHANGE, new IntPtr(32), lParam);
        }

        /// <summary>
        /// Forces visible, non-minimized windows of the given processes to recalculate their frame and repaint
        /// (no move, resize or focus change). Returns the number of windows nudged.
        /// </summary>
        public static int NudgeWindows(ICollection<string> processNames)
        {
            if (processNames.Count == 0) return 0;
            int count = 0;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (!processNames.Contains(ResolveProcessName(pid))) return true;

                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED |
                    SWP_ASYNCWINDOWPOS);   // async: a hung window cannot block us
                RedrawWindow(hWnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN);
                count++;
                return true;
            }, IntPtr.Zero);
            return count;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

        /// <summary>
        /// Looks through all visible, non-minimized top-level windows for a browser window whose title contains the
        /// token (i.e. a tab with a playing HDR video is the active tab of that window). Returns the browser's
        /// process name, or null. Only called while HDR is on and the focused window is not HDR content.
        /// </summary>
        public static string? FindBrowserWindowWithToken(ICollection<string> browserNames, string token, bool requireFullscreen)
        {
            if (string.IsNullOrEmpty(token) || browserNames.Count == 0) return null;

            string? found = null;
            var sb = _titleBuffer ??= new StringBuilder(512);

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;

                sb.Clear();
                if (GetWindowText(hWnd, sb, sb.Capacity) < token.Length) return true;
                if (sb.ToString().IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) return true;
                if (requireFullscreen && !IsWindowFullscreen(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                string name = ResolveProcessName(pid);   // not the foreground cache, so that one stays valid
                if (browserNames.Contains(name))
                {
                    found = name;
                    return false;   // stop enumerating
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }
    }

}
