using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SmartHdrManager
{
    /// <summary>A video player that can be started with "Launch with HDR".</summary>
    public sealed class PlayerInfo
    {
        public string Name { get; init; } = string.Empty;
        public string ExePath { get; init; } = string.Empty;
        public bool Custom { get; init; }   // added by hand (can be removed again)
    }

    /// <summary>
    /// "Videos" tab: finds installed video players and creates "HDR on first, then the player" entry points –
    /// a desktop shortcut and a right-click "Play with HDR" entry for video files. Both run
    /// SmartHdrManager.exe --launch-video "player.exe" "video file", so Windows is already in HDR when the player's
    /// renderer starts, and NVIDIA RTX Video HDR can convert SDR videos from the first frame.
    /// Optionally also an "Open with" app "<Player> (with HDR)" that the user can make the default for videos.
    /// Everything is per user (HKCU, desktop); no administrator rights; the default app is only ever chosen by the user.
    /// </summary>
    public static class VideoPlayers
    {
        // Known players: display name, exe names (App Paths), typical install locations
        private static readonly (string Name, string[] Exes, string[] Paths)[] Known =
        {
            ("MPC-HC", new[] { "mpc-hc64.exe", "mpc-hc.exe" }, new[]
            {
                @"%ProgramFiles%\MPC-HC\mpc-hc64.exe",
                @"%ProgramFiles(x86)%\K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe",
                @"%ProgramFiles%\K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe",
                @"%ProgramFiles(x86)%\MPC-HC\mpc-hc.exe"
            }),
            ("MPC-BE", new[] { "mpc-be64.exe", "mpc-be.exe" }, new[]
            {
                @"%ProgramFiles%\MPC-BE x64\mpc-be64.exe",
                @"%ProgramFiles%\MPC-BE\mpc-be64.exe"
            }),
            ("VLC", new[] { "vlc.exe" }, new[]
            {
                @"%ProgramFiles%\VideoLAN\VLC\vlc.exe",
                @"%ProgramFiles(x86)%\VideoLAN\VLC\vlc.exe"
            }),
            ("PotPlayer", new[] { "PotPlayerMini64.exe", "PotPlayerMini.exe" }, new[]
            {
                @"%ProgramFiles%\DAUM\PotPlayer\PotPlayerMini64.exe",
                @"%ProgramFiles(x86)%\DAUM\PotPlayer\PotPlayerMini.exe"
            }),
            ("mpv.net", new[] { "mpvnet.exe" }, new[]
            {
                @"%ProgramFiles%\mpv.net\mpvnet.exe",
                @"%LocalAppData%\Programs\mpv.net\mpvnet.exe"
            }),
            ("mpv", new[] { "mpv.exe" }, Array.Empty<string>()),
            ("Kodi", new[] { "kodi.exe" }, new[] { @"%ProgramFiles%\Kodi\kodi.exe" })
        };

        // Right-click entries are registered per file extension (SystemFileAssociations\.mkv\shell ...). That works no
        // matter which program the files are associated with. The "video" perceived type is not used: it is missing
        // for .mkv and others on many PCs, and using both would show the entry twice.
        private static readonly string[] VideoExtensions =
        {
            ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".webm", ".ts", ".m2ts", ".mts",
            ".flv", ".mpg", ".mpeg", ".vob", ".3gp", ".ogv", ".hevc"
        };
        private const string LegacyMenuRoot = @"Software\Classes\SystemFileAssociations\video\shell";   // test builds
        private const string MenuPrefix = "SmartHdrManager.";

        private static IEnumerable<string> MenuRoots() =>
            VideoExtensions.Select(ext => $@"Software\Classes\SystemFileAssociations\{ext}\shell");

        /// <summary>Installed players (found automatically) plus the ones added by hand. One entry per exe.</summary>
        public static List<PlayerInfo> Find(IEnumerable<string> customPaths)
        {
            var result = new List<PlayerInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string name, string? path, bool custom)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                try
                {
                    path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
                    if (!File.Exists(path) || !seen.Add(path)) return;
                    result.Add(new PlayerInfo { Name = name, ExePath = path, Custom = custom });
                }
                catch { }
            }

            var uninstall = UninstallLocations();
            foreach (var (name, exes, paths) in Known)
            {
                foreach (string exe in exes)
                {
                    Add(name, AppPath(exe), false);
                    Add(name, OpenCommandPath(exe), false);              // "Open with" registration
                    foreach (string dir in uninstall) Add(name, Path.Combine(dir, exe), false);
                }
                foreach (string p in paths) Add(name, p, false);
            }
            Add("MPC-HC", RegistryString(Registry.CurrentUser, @"Software\MPC-HC\MPC-HC", "ExePath"), false);
            Add("MPC-BE", RegistryString(Registry.CurrentUser, @"Software\MPC-BE", "ExePath"), false);
            Add("VLC", RegistryString(Registry.LocalMachine, @"SOFTWARE\VideoLAN\VLC", null), false);
            Add("PotPlayer", RegistryString(Registry.CurrentUser, @"Software\DAUM\PotPlayer64", "ProgramPath"), false);

            // A known player that is running right now (portable copies, unusual folders)
            foreach (var proc in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    string file = proc.ProcessName + ".exe";
                    foreach (var (name, exes, _) in Known)
                        if (exes.Contains(file, StringComparer.OrdinalIgnoreCase))
                            Add(name, ProcessPaths.TryGetExePath(proc.Id), false);
                }
                catch { }
                finally { proc.Dispose(); }
            }
            foreach (string p in customPaths ?? Enumerable.Empty<string>())
                Add(DisplayNameFor(p), p, true);

            return result.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Name for a player added by hand: a known player's name, otherwise the file's description or name.</summary>
        public static string DisplayNameFor(string exePath)
        {
            string file = Path.GetFileName(exePath);
            foreach (var (name, exes, _) in Known)
                if (exes.Contains(file, StringComparer.OrdinalIgnoreCase)) return name;
            try
            {
                string? description = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).FileDescription;
                if (!string.IsNullOrWhiteSpace(description) && description.Length <= 40) return description.Trim();
            }
            catch { }
            return Path.GetFileNameWithoutExtension(exePath);
        }

        private static string? RegistryString(RegistryKey root, string subKey, string? value)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                return key?.GetValue(value) as string;
            }
            catch
            {
                return null;
            }
        }

        // HKCR\Applications\<exe>\shell\open\command – written when a program registers for "Open with"
        private static string? OpenCommandPath(string exe)
        {
            string? cmd = RegistryString(Registry.ClassesRoot, $@"Applications\{exe}\shell\open\command", null);
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                return end > 1 ? cmd.Substring(1, end - 1) : null;
            }
            int exeEnd = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exeEnd > 0 ? cmd.Substring(0, exeEnd + 4) : null;
        }

        // Install folders of programs listed in "Apps & features" whose name looks like a video player
        private static List<string> UninstallLocations()
        {
            var dirs = new List<string>();
            string[] hints = { "mpc-hc", "mpc-be", "vlc", "potplayer", "mpv", "kodi", "k-lite" };
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (string path in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                {
                    try
                    {
                        using var key = root.OpenSubKey(path);
                        if (key == null) continue;
                        foreach (string sub in key.GetSubKeyNames())
                        {
                            using var entry = key.OpenSubKey(sub);
                            string name = entry?.GetValue("DisplayName") as string ?? string.Empty;
                            string location = (entry?.GetValue("InstallLocation") as string ?? string.Empty).Trim().Trim('"');
                            if (location.Length == 0 || !hints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase))) continue;
                            dirs.Add(location);
                            // K-Lite keeps MPC-HC in a subfolder
                            dirs.Add(Path.Combine(location, "MPC-HC64"));
                            dirs.Add(Path.Combine(location, "MPC-HC"));
                        }
                    }
                    catch { }
                }
            }
            return dirs;
        }

        // "App Paths": where installers register their exe (per user first, then for all users)
        private static string? AppPath(string exe)
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                    if (key?.GetValue(null) is string path && path.Length > 0) return path;
                }
                catch { }
            }
            return null;
        }

        // ---- right-click "Play with HDR" (video files, per user) ----
        private static string MenuKey(string exePath) =>
            MenuPrefix + Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();

        public static bool IsInContextMenu(string exePath)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{MenuRoots().First()}\{MenuKey(exePath)}\command");
                return key?.GetValue(null) is string cmd && cmd.Contains(exePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Adds "Play with HDR (player)" to the right-click menu of video files (Windows 11: "Show more options").</summary>
        public static bool AddToContextMenu(PlayerInfo player, string launcherExe, out string error)
        {
            error = string.Empty;
            try
            {
                RemoveLegacy(player.ExePath);
                foreach (string root in MenuRoots())
                {
                    using var key = Registry.CurrentUser.CreateSubKey($@"{root}\{MenuKey(player.ExePath)}");
                    key.SetValue(null, $"Play with HDR ({player.Name})");
                    key.SetValue("Icon", $"\"{player.ExePath}\",0");
                    using var command = key.CreateSubKey("command");
                    command.SetValue(null, $"\"{launcherExe}\" --launch-video \"{player.ExePath}\" \"%1\"");
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void RemoveFromContextMenu(string exePath)
        {
            RemoveLegacy(exePath);
            foreach (string root in MenuRoots())
            {
                try { Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{MenuKey(exePath)}", false); } catch { }
            }
        }

        private static void RemoveLegacy(string exePath)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{LegacyMenuRoot}\{MenuKey(exePath)}", false); } catch { }
        }

        /// <summary>
        /// The app was moved or updated into another folder: point existing "Play with HDR" entries at this exe.
        /// Returns how many entries were updated.
        /// </summary>
        public static int RefreshContextMenu(string launcherExe)
        {
            int changed = 0;
            foreach (string rootPath in MenuRoots())
            {
                try
                {
                    using var root = Registry.CurrentUser.OpenSubKey(rootPath, writable: false);
                    if (root == null) continue;
                    foreach (string sub in root.GetSubKeyNames().Where(n => n.StartsWith(MenuPrefix, StringComparison.OrdinalIgnoreCase)))
                    {
                        using var command = Registry.CurrentUser.OpenSubKey($@"{rootPath}\{sub}\command", writable: true);
                        if (command?.GetValue(null) is not string cmd) continue;
                        // "<launcher>" --launch "<player>" "%1"
                        int end = cmd.StartsWith("\"") ? cmd.IndexOf('"', 1) : -1;
                        if (end <= 1) continue;
                        string updated = UpdateCommand(cmd, end, launcherExe);
                        if (updated == cmd) continue;
                        command.SetValue(null, updated);
                        changed++;
                    }
                }
                catch { }
            }
            return changed;
        }

        // ---- "Open with" / default app: "<Player> (with HDR)" ----
        // Registers a per-user app for video files (ProgID + Capabilities + RegisteredApplications). It appears in
        // "Open with" and in Settings › Apps › Default apps. Windows does not let programs make themselves the
        // default (UserChoice is protected), so the user picks it there once; after that a normal double-click on a
        // video starts the player with HDR already on.
        private const string ProgIdPrefix = "SmartHdrManager.HdrPlay.";
        private const string PlayersRoot = @"Software\SmartHdrManager\Players";

        private static string PlayerKey(string exePath) => Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        private static string ProgId(string exePath) => ProgIdPrefix + PlayerKey(exePath);
        private static string RegisteredName(string exePath) => "SmartHdrManager." + PlayerKey(exePath);
        private static string CapabilitiesPath(string exePath) => $@"{PlayersRoot}\{PlayerKey(exePath)}\Capabilities";
        public static string AppName(PlayerInfo player) => $"{player.Name} (with HDR)";

        public static bool IsRegisteredAsApp(string exePath)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId(exePath)}\shell\open\command");
                return key?.GetValue(null) is string cmd && cmd.Contains(exePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool RegisterAsApp(PlayerInfo player, string launcherExe, out string error)
        {
            error = string.Empty;
            try
            {
                string progId = ProgId(player.ExePath);
                string name = AppName(player);
                string icon = $"\"{player.ExePath}\",0";
                string command = $"\"{launcherExe}\" --launch-video \"{player.ExePath}\" \"%1\"";

                using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}"))
                {
                    key.SetValue(null, $"Video ({name})");
                    using (var defaultIcon = key.CreateSubKey("DefaultIcon")) defaultIcon.SetValue(null, icon);
                    using (var app = key.CreateSubKey("Application"))   // name shown in "Open with"
                    {
                        app.SetValue("ApplicationName", name);
                        app.SetValue("ApplicationIcon", icon);
                        app.SetValue("ApplicationDescription", $"Plays videos in {player.Name} with Windows HDR switched on first (Smart HDR Manager).");
                        app.SetValue("ApplicationCompany", "Smart HDR Manager");
                    }
                    using (var open = key.CreateSubKey(@"shell\open"))
                    {
                        open.SetValue("FriendlyAppName", name);
                        using var cmd = open.CreateSubKey("command");
                        cmd.SetValue(null, command);
                    }
                }

                using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesPath(player.ExePath)))
                {
                    caps.SetValue("ApplicationName", name);
                    caps.SetValue("ApplicationDescription", $"Plays videos in {player.Name} with Windows HDR switched on first.");
                    caps.SetValue("ApplicationIcon", icon);
                    using var assoc = caps.CreateSubKey("FileAssociations");
                    foreach (string ext in VideoExtensions) assoc.SetValue(ext, progId);
                }

                foreach (string ext in VideoExtensions)
                {
                    using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
                    openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
                }

                using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                    registered.SetValue(RegisteredName(player.ExePath), CapabilitiesPath(player.ExePath));

                NotifyAssociationsChanged();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void UnregisterApp(string exePath)
        {
            string progId = ProgId(exePath);
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{progId}", false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{PlayersRoot}\{PlayerKey(exePath)}", false); } catch { }
            try
            {
                using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
                registered?.DeleteValue(RegisteredName(exePath), false);
            }
            catch { }
            foreach (string ext in VideoExtensions)
            {
                try
                {
                    using var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
                    openWith?.DeleteValue(progId, false);
                }
                catch { }
            }
            NotifyAssociationsChanged();
        }

        /// <summary>Opens Settings › Default apps on this app's page (Windows 11), or the general page.</summary>
        public static void OpenDefaultAppsSettings(string exePath)
        {
            foreach (string uri in new[] { $"ms-settings:defaultapps?registeredAppUser={Uri.EscapeDataString(RegisteredName(exePath))}", "ms-settings:defaultapps" })
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
                    return;
                }
                catch { }
            }
        }

        /// <summary>
        /// Uninstall: removes every "Play with HDR" right-click entry and every "(with HDR)" app registration, for all
        /// players, plus the app's own key (HKCU\Software\SmartHdrManager).
        /// </summary>
        public static void RemoveAll()
        {
            foreach (string rootPath in MenuRoots().Append(LegacyMenuRoot))
            {
                try
                {
                    using var root = Registry.CurrentUser.OpenSubKey(rootPath, writable: true);
                    if (root == null) continue;
                    foreach (string sub in root.GetSubKeyNames().Where(n => n.StartsWith(MenuPrefix, StringComparison.OrdinalIgnoreCase)))
                        root.DeleteSubKeyTree(sub, false);
                }
                catch { }
            }

            string[] players = Array.Empty<string>();
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PlayersRoot);
                if (key != null) players = key.GetSubKeyNames();
            }
            catch { }
            foreach (string player in players) UnregisterApp(player + ".exe");   // the key is the player's exe name

            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\SmartHdrManager", false); } catch { }
            NotifyAssociationsChanged();
        }

        /// <summary>The app was moved: point the registered "(with HDR)" apps at this exe. Returns how many were updated.</summary>
        public static int RefreshAppRegistrations(string launcherExe)
        {
            int changed = 0;
            try
            {
                using var players = Registry.CurrentUser.OpenSubKey(PlayersRoot);
                if (players == null) return 0;
                foreach (string player in players.GetSubKeyNames())
                {
                    using var command = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgIdPrefix}{player}\shell\open\command", writable: true);
                    if (command?.GetValue(null) is not string cmd) continue;
                    int end = cmd.StartsWith("\"") ? cmd.IndexOf('"', 1) : -1;
                    if (end <= 1) continue;
                    string updated = UpdateCommand(cmd, end, launcherExe);
                    if (updated == cmd) continue;
                    command.SetValue(null, updated);
                    changed++;
                }
            }
            catch { }
            if (changed > 0) NotifyAssociationsChanged();
            return changed;
        }

        /// <summary>
        /// "&lt;launcher&gt;" --launch "&lt;player&gt;" "%1": points it at this exe if the old one is gone, and uses --launch-video
        /// (entries from test builds used --launch, which waits for the player's process instead of its window).
        /// </summary>
        private static string UpdateCommand(string cmd, int launcherEnd, string launcherExe)
        {
            string current = cmd.Substring(1, launcherEnd - 1);
            string rest = cmd.Substring(launcherEnd + 1);
            if (rest.StartsWith(" --launch ", StringComparison.OrdinalIgnoreCase))
                rest = " --launch-video " + rest.Substring(" --launch ".Length);
            string launcher = string.Equals(current, launcherExe, StringComparison.OrdinalIgnoreCase) || File.Exists(current) ? current : launcherExe;
            return $"\"{launcher}\"" + rest;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        private static void NotifyAssociationsChanged()
        {
            try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero); } catch { }
        }
    }
}
