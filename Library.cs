using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SmartHdrManager
{
    /// <summary>
    /// Finds installed games in the Steam, Epic Games, GOG, Xbox (PC Game Pass), Ubisoft Connect, EA app, Rockstar and
    /// Battle.net, Amazon Games, itch.io and Riot Games libraries and the games installed with Heroic Games Launcher, plus
    /// games that only the Xbox Game Bar knows about, with their real names
    /// and the executable(s) that run while playing. Read-only: only manifest files and the registry are read.
    /// </summary>
    public static class GameLibraryScanner
    {
        // Steam entries that are tools / runtimes, not games
        private static readonly HashSet<string> SteamNonGameAppIds = new()
        {
            "228980",   // Steamworks Common Redistributables
            "250820",   // SteamVR
            "431960",   // Wallpaper Engine
            "1070560",  // Steam Linux Runtime
            "1391110",  // Steam Linux Runtime - Soldier
            "1628350",  // Steam Linux Runtime - Sniper
            "1493710",  // Proton Experimental
        };

        // Steam app types (appinfo.vdf › common › type) that are not games
        private static readonly HashSet<string> SteamNonGameTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "application", "tool", "video", "music", "config", "dlc", "series", "episode", "hardware", "media", "comic"
        };

        private static readonly string[] NonGameNameParts =
        {
            "redistributable", "proton", "steam linux runtime", "dedicated server", "soundtrack", " sdk", "benchmark tool"
        };

        // Executables that are never the game itself
        private static readonly string[] ExcludedExeParts =
        {
            "unins", "uninst", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup", "directx", "dotnet",
            "crash", "report", "bugsplat", "sentry", "easyanticheat", "eac_", "battleye", "beservice", "anticheat",
            "prereq", "cleanup", "touchup", "helper", "updater", "update", "cefprocess", "webhelper", "quicksfv",
            "dxwebsetup", "physx", "ue4prereq", "ueprereq", "unitycrashhandler", "crs-handler", "cmd", "python", "netfx",
            "companion", "supporttool", "watchdog", "pbsvc", "punkbuster", "javaw"
        };

        // Folders inside a game folder that only hold installers and runtimes ("_CommonRedist", "Support\dotNetFramework")
        private static readonly HashSet<string> InstallerFolders = new(StringComparer.OrdinalIgnoreCase)
        {
            "_commonredist", "commonredist", "redist", "redistributables", "dotnetframework", "dotnetcore", "directx",
            "vcredist", "prerequisites", "prereqs", "__installer", "installers", "support"
        };

        // Launcher programs that only start the real game and then close (or wait in the background). A library entry
        // that points at one of these would make the app watch the launcher instead of the game (GitHub issue #1:
        // The Witcher 3 from GOG registers REDprelauncher.exe). Compared without spaces, dashes or dots.
        private static readonly HashSet<string> KnownLaunchers = new(StringComparer.OrdinalIgnoreCase)
        {
            "redprelauncher", "redlauncher", "gtavlauncher", "playgtav", "playrdr2", "rdr2launcher", "launcher",
            "startprotectedgame", "launcherpatcher", "gamelauncher", "bootstrapper", "dowser", "playlan", "ffxivboot", "ffxivboot64", "edlaunch", "runme"
        };

        // Extra executables of the same game (2.5.2): never added just because they score close to the main exe
        private static readonly string[] NotAGameExeParts =
        {
            "launcher", "config", "settings", "setting", "benchmark", "editor", "tool", "server", "modmanager",
            "mod_manager", "uploader", "patch", "selector", "inst", "map", "sdk", "p4v", "vcs", "support", "console",
            "creation kit"
        };

        // Bumped when the exe detection changes, so cached library entries are detected again once after an update
        // (2: launcher check + several exes per game, 3: extra exes must belong to the game by name – 2.5.2,
        // 4: Steam exes from the launch options in appinfo.vdf).
        public const int CurrentScanVersion = 4;

        // Executables found earlier, by game id: reused when the main exe still exists, so a quick refresh
        // does not have to search every install folder again (a full rescan passes null).
        [ThreadStatic] private static IReadOnlyDictionary<string, LibraryGame>? _previous;
        [ThreadStatic] private static Action<string>? _log;

        /// <param name="notGames">Programs that are known not to be games (the Apps tab): never suggested from the Game Bar list.</param>
        public static List<LibraryGame> Scan(IReadOnlyDictionary<string, LibraryGame>? previous, Action<string>? log = null,
                                             ISet<string>? notGames = null)
        {
            _previous = previous;
            _log = log;
            var games = new List<LibraryGame>();
            Try(() => games.AddRange(ScanSteam()), "Steam", log);
            Try(() => games.AddRange(ScanEpic()), "Epic Games", log);
            Try(() => games.AddRange(ScanGog()), "GOG", log);
            Try(() => games.AddRange(ScanXbox()), "Xbox", log);

            // Launchers added in 2.5.1. Some games register with these AND with Steam (e.g. Rockstar or Ubisoft games
            // bought on Steam): skip an entry whose executable is already known from an earlier source.
            var extra = new List<LibraryGame>();
            Try(() => extra.AddRange(ScanUbisoft()), "Ubisoft Connect", log);
            Try(() => extra.AddRange(ScanEaApp()), "EA app", log);
            Try(() => extra.AddRange(ScanRockstar()), "Rockstar", log);
            Try(() => extra.AddRange(ScanBattleNet()), "Battle.net", log);
            Try(() => extra.AddRange(ScanAmazon()), "Amazon Games", log);
            Try(() => extra.AddRange(ScanItch()), "itch.io", log);
            Try(() => extra.AddRange(ScanRiot()), "Riot Games", log);
            Try(() => extra.AddRange(ScanHeroic()), "Heroic", log);
            var knownProcesses = new HashSet<string>(games.SelectMany(g => g.Processes), StringComparer.OrdinalIgnoreCase);
            foreach (var g in extra)
            {
                if (g.Processes.Count == 0 || g.Processes.Any(knownProcesses.Contains)) continue;
                games.Add(g);
                foreach (var p in g.Processes) knownProcesses.Add(p);
            }

            // Last: games Windows recognised while they ran that no launcher lists (DRM-free downloads, emulators, …)
            Try(() => games.AddRange(ScanGameBar(games, knownProcesses, notGames).ToList()), "Game Bar", log);

            _previous = null;
            _log = null;

            // One entry per id, games without a detectable executable are dropped
            return games
                .Where(g => g.Processes.Count > 0)
                .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void Try(Action action, string source, Action<string>? log)
        {
            try { action(); }
            catch (Exception ex) { log?.Invoke($"{source} library could not be read: {ex.Message}"); }
        }

        // ------------------------------------------------------------------
        // Steam: libraryfolders.vdf -> steamapps\appmanifest_*.acf -> steamapps\common\<installdir>;
        // the exe from the game's launch options in appcache\appinfo.vdf, else searched in the install folder
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanSteam()
        {
            string? steamPath =
                Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ??
                Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (string.IsNullOrWhiteSpace(steamPath)) yield break;
            steamPath = steamPath.Replace('/', '\\');

            var libraries = new List<string> { steamPath };
            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                    libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }

            var installed = new List<(uint AppId, string Name, string InstallDir, string Folder)>();
            foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;

                foreach (string acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                {
                    string text;
                    try { text = File.ReadAllText(acf); } catch { continue; }

                    string appId = AcfValue(text, "appid");
                    string name = AcfValue(text, "name");
                    string installDir = AcfValue(text, "installdir");
                    if (!uint.TryParse(appId, out uint id) || name.Length == 0 || installDir.Length == 0) continue;
                    if (SteamNonGameAppIds.Contains(appId) || IsNonGameName(name)) continue;

                    string folder = Path.Combine(steamapps, "common", installDir);
                    if (Directory.Exists(folder)) installed.Add((id, name, installDir, folder));
                }
            }

            // Launch options of the games that are not cached yet (read once, only for those apps)
            Dictionary<uint, SteamAppInfo.App>? launch = null;
            var toDetect = installed.Where(g => !CanReuse($"steam:{g.AppId}")).Select(g => g.AppId).ToHashSet();
            if (toDetect.Count > 0)
            {
                string appinfo = Path.Combine(steamPath, "appcache", "appinfo.vdf");
                try
                {
                    if (File.Exists(appinfo)) launch = SteamAppInfo.ReadApps(appinfo, toDetect);
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Steam: appinfo.vdf could not be read ({ex.Message}) – searching the install folders instead.");
                }
            }

            foreach (var (appId, name, installDir, folder) in installed)
            {
                string id = $"steam:{appId}";
                if (TryReuse(id, name, "Steam", out var cached))
                {
                    yield return cached;
                    continue;
                }

                SteamAppInfo.App? app = null;
                launch?.TryGetValue(appId, out app);
                // Software, videos, soundtracks … that Steam installs like games (e.g. a budget app, a trailer)
                if (app != null && SteamNonGameTypes.Contains(app.Type)) continue;

                var exes = app != null && app.Executables.Count > 0
                    ? SteamLaunchExes(id, name, folder, installDir, app.Executables)
                    : FindGameExecutables(folder, name, installDir);
                yield return Make(id, name, "Steam", exes);
            }
        }

        /// <summary>
        /// Executables from a Steam game's launch options (up to 3, e.g. DX11 / DX12 versions). Launchers, tools and
        /// files that do not exist are skipped; when only a launcher is left, the install folder is searched instead.
        /// </summary>
        private static List<string> SteamLaunchExes(string id, string name, string folder, string installDir, List<string> relative)
        {
            var existing = new List<string>();
            foreach (string r in relative)
            {
                string full;
                try { full = Path.GetFullPath(Path.Combine(folder, r)); } catch { continue; }   // "bin\.\game.exe" → "bin\game.exe"
                if (File.Exists(full) && !IsExcludedExe(Path.GetFileNameWithoutExtension(full)) &&
                    !existing.Contains(full, StringComparer.OrdinalIgnoreCase)) existing.Add(full);
            }
            if (existing.Count == 0) return FindGameExecutables(folder, name, installDir);

            // A launcher as the default option (Paradox's dowser.exe, PlayLAN.exe …): the next option, or the folder search
            var games = existing.Where(p => !LooksLikeLauncher(p)).ToList();
            if (games.Count == 0) return RegisteredOrFound(id, name, "Steam", existing[0], folder, installDir);
            // A settings tool as the default option (JadeEmpireConfig.exe): the game's own option comes first
            MoveGameFirst(games);

            // Further options only when they clearly belong to the game (not an editor, a comic or a tool it also ships):
            // Steam already lists them, so a shorter shared start is enough ("iw3mp" next to "iw3sp")
            string nameKey = Normalize(name), dirKey = Normalize(installDir);
            var exes = new List<string> { games[0] };
            string mainKey = Normalize(Path.GetFileNameWithoutExtension(games[0]));
            foreach (string p in games.Skip(1))
            {
                string file = Path.GetFileNameWithoutExtension(p);
                if (exes.Count < 3 && !NotAGameExeParts.Any(file.ToLowerInvariant().Contains) &&
                    BelongsToGame(Normalize(file), mainKey, nameKey, dirKey, looseStem: true)) exes.Add(p);
            }

            // Other exes of the game in its folder, read once for both checks
            List<string>? folderExes = null;
            List<string> FolderExes() => folderExes ??= ExesIn(folder);
            AddShippingCounterpart(exes, folder);
            AddUnrealShipping(exes, FolderExes);
            AddSiblingExes(exes, FolderExes, nameKey, dirKey);

            // Only a settings tool in the launch options: the game exe found next to it comes first, and the tool is dropped
            MoveGameFirst(exes);
            if (!IsToolLike(exes[0])) exes.RemoveAll(IsToolLike);
            return exes;
        }

        private static bool IsToolLike(string exe) => NotAGameExeParts.Any(Path.GetFileNameWithoutExtension(exe).ToLowerInvariant().Contains);

        /// <summary>Moves the first exe that is not named like a tool (config, settings, editor, …) to the front.</summary>
        private static void MoveGameFirst(List<string> exes)
        {
            int game = exes.FindIndex(p => !IsToolLike(p));
            if (game <= 0) return;
            string first = exes[game];
            exes.RemoveAt(game);
            exes.Insert(0, first);
        }

        /// <summary>Every exe in a game folder and its subfolders (4 levels), or none when the folder cannot be read.</summary>
        private static List<string> ExesIn(string folder)
        {
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
                return Directory.EnumerateFiles(folder, "*.exe", options).Where(e => !InInstallerFolder(e, folder)).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Unreal games whose starter has another name than the real process ("TheOuterWorlds.exe" starts
        /// "Indiana-Win64-Shipping.exe"): when the main exe is a small starter and no shipping exe is watched yet, the
        /// largest *-Win64-Shipping / *-WinGDK-Shipping exe in the folder is added.
        /// </summary>
        private static void AddUnrealShipping(List<string> exes, Func<List<string>> folderExes)
        {
            try
            {
                if (exes.Any(e => Path.GetFileNameWithoutExtension(e).EndsWith("-Shipping", StringComparison.OrdinalIgnoreCase))) return;
                if (new FileInfo(exes[0]).Length > 5 * 1024 * 1024) return;   // a real game exe, not a starter

                string? shipping = folderExes()
                    .Where(e => Path.GetFileNameWithoutExtension(e).EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
                                Path.GetFileNameWithoutExtension(e).EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase))
                    .Where(e => !IsExcludedExe(Path.GetFileNameWithoutExtension(e)))
                    .OrderByDescending(e => new FileInfo(e).Length)
                    .FirstOrDefault();
                if (shipping != null && exes.Count < 4) exes.Add(shipping);
            }
            catch { }
        }

        /// <summary>
        /// Other exes of the same game next to the one from the launch options, e.g. the multiplayer exe "AC4BFMP.exe"
        /// next to "AC4BFSP.exe": same rules as the extra exes of <see cref="FindGameExecutables"/> (named like the game
        /// or like the main exe), at most 3 names in total.
        /// </summary>
        private static void AddSiblingExes(List<string> exes, Func<List<string>> folderExes, string nameKey, string dirKey)
        {
            try
            {
                var names = new HashSet<string>(exes.Select(Path.GetFileNameWithoutExtension)!, StringComparer.OrdinalIgnoreCase);
                if (names.Count >= 3) return;
                string mainKey = Normalize(Path.GetFileNameWithoutExtension(exes[0]));
                foreach (string exe in folderExes())
                {
                    string file = Path.GetFileNameWithoutExtension(exe);
                    string lower = file.ToLowerInvariant();
                    if (names.Contains(file) || IsExcludedExe(file) || LooksLikeLauncher(exe) || NotAGameExeParts.Any(lower.Contains)) continue;
                    if (!BelongsToGame(Normalize(file), mainKey, nameKey, dirKey, requireStem: true)) continue;
                    if (new FileInfo(exe).Length < 300 * 1024) continue;
                    exes.Add(exe);
                    names.Add(file);
                    if (names.Count >= 3) break;
                }
            }
            catch { }
        }

        // What follows the shared start of a tool's name and the game's name
        private static readonly HashSet<string> ToolEndings = new(StringComparer.Ordinal)
        {
            "ds", "sc", "mod", "dev", "server", "editor", "console", "config", "tool", "tools", "debug"
        };

        /// <summary>
        /// True when an exe (normalized name <paramref name="key"/>) belongs to the same game as the main exe: named like
        /// the game or its folder, or starting like the main exe ("Game_DX12" next to "Game"). With
        /// <paramref name="requireStem"/>, only the main-exe rule counts. With <paramref name="looseStem"/>, sharing the
        /// first half of the name is enough ("iw3mp" / "iw3sp", "CivilizationBE_Mantle" / "CivilizationBE_DX11").
        /// </summary>
        private static bool BelongsToGame(string key, string mainKey, string nameKey, string dirKey, bool requireStem = false, bool looseStem = false)
        {
            bool namedLikeGame = key.Length >= 3 && (key == nameKey || key == dirKey || nameKey.Contains(key) || dirKey.Contains(key));
            int shorter = Math.Min(key.Length, mainKey.Length), common = CommonPrefixLength(key, mainKey);
            bool sameStem = key.Length >= 4 && mainKey.Length >= 4 && (key.StartsWith(mainKey) || mainKey.StartsWith(key) ||
                            common >= Math.Max(4, shorter - 2));
            if (looseStem) sameStem |= (common >= 3 && common * 2 >= shorter) || (key.Length >= 2 && mainKey.StartsWith(key));   // "tf" / "tf_win64"
            // The main name plus a tool ending: "ContagionDS" (dedicated server), "PlagueIncSC" (scenario creator)
            if (sameStem && ToolEndings.Contains(key.Substring(common))) return false;
            return sameStem || (!requireStem && namedLikeGame);
        }

        private static string AcfValue(string text, string key)
        {
            var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"(.*?)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\").Trim() : string.Empty;
        }

        // ------------------------------------------------------------------
        // Epic Games: ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item (JSON, includes the launch exe)
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanEpic()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(dir)) yield break;

            foreach (string file in Directory.EnumerateFiles(dir, "*.item"))
            {
                LibraryGame? game = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    string name = Str(root, "DisplayName");
                    string location = Str(root, "InstallLocation");
                    string launch = Str(root, "LaunchExecutable");
                    string appName = Str(root, "AppName");
                    string mainApp = Str(root, "MainGameAppName");
                    bool incomplete = root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True;

                    // Skip DLC / add-ons (they point at the main game) and unfinished installs
                    bool isDlc = mainApp.Length > 0 && appName.Length > 0 && !mainApp.Equals(appName, StringComparison.OrdinalIgnoreCase);
                    if (!incomplete && !isDlc && name.Length > 0 && location.Length > 0 && !IsNonGameName(name))
                    {
                        string id = $"epic:{(appName.Length > 0 ? appName : name)}";
                        string? registered = launch.Length > 0 ? Path.Combine(location, launch.Replace('/', '\\')) : null;
                        var exes = RegisteredOrFound(id, name, "Epic Games", registered, location, Path.GetFileName(location.TrimEnd('\\')));
                        game = Make(id, name, "Epic Games", exes);
                    }
                }
                catch { }

                if (game != null) yield return game;
            }
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        // ------------------------------------------------------------------
        // GOG Galaxy: HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\<id> (gameName, exe, path, dependsOn)
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanGog()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (key == null) yield break;

            foreach (string id in key.GetSubKeyNames())
            {
                using var game = key.OpenSubKey(id);
                if (game == null) continue;

                string name = game.GetValue("gameName") as string ?? string.Empty;
                string exe = game.GetValue("exe") as string ?? string.Empty;
                string path = game.GetValue("path") as string ?? string.Empty;
                string dependsOn = game.GetValue("dependsOn") as string ?? string.Empty;
                if (name.Length == 0 || dependsOn.Length > 0 || IsNonGameName(name)) continue;   // dependsOn = DLC

                // The registered exe can be a launcher (The Witcher 3 / Cyberpunk 2077: REDprelauncher.exe, GitHub issue #1)
                string libId = $"gog:{id}";
                var exes = RegisteredOrFound(libId, name, "GOG", exe.Length > 0 ? exe : null, path, Path.GetFileName(path.TrimEnd('\\')));
                yield return Make(libId, name, "GOG", exes);
            }
        }

        // ------------------------------------------------------------------
        // Ubisoft Connect: HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\<id>\InstallDir
        // (display name from the "Uplay Install <id>" uninstall entry, else the folder name)
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanUbisoft()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
            if (key == null) yield break;

            foreach (string id in key.GetSubKeyNames())
            {
                using var game = key.OpenSubKey(id);
                string dir = (game?.GetValue("InstallDir") as string ?? string.Empty).Replace('/', '\\').TrimEnd('\\');
                if (dir.Length == 0 || !Directory.Exists(dir)) continue;

                string folderName = Path.GetFileName(dir);
                string name = UninstallDisplayName($"Uplay Install {id}") ?? folderName;
                if (IsNonGameName(name)) continue;

                string libId = $"ubisoft:{id}";
                if (TryReuse(libId, name, "Ubisoft", out var cached))
                {
                    yield return cached;
                    continue;
                }
                yield return Make(libId, name, "Ubisoft", FindGameExecutables(dir, name, folderName));
            }
        }

        // ------------------------------------------------------------------
        // EA app (and old Origin installs): uninstall entries whose uninstaller lives in "EAInstaller"
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanEaApp()
        {
            foreach (var (subKey, name, location, uninstall) in UninstallEntries())
            {
                if (uninstall.IndexOf("EAInstaller", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string dir = location.Trim().Trim('"').TrimEnd('\\');
                if (name.Length == 0 || dir.Length == 0 || !Directory.Exists(dir) || IsNonGameName(name)) continue;

                string libId = $"ea:{subKey}";
                if (TryReuse(libId, name, "EA app", out var cached))
                {
                    yield return cached;
                    continue;
                }
                yield return Make(libId, name, "EA app", FindGameExecutables(dir, name, Path.GetFileName(dir)));
            }
        }

        // ------------------------------------------------------------------
        // Rockstar Games Launcher: HKLM\SOFTWARE\WOW6432Node\Rockstar Games\<game>\InstallFolder
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanRockstar()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Rockstar Games");
            if (key == null) yield break;

            foreach (string sub in key.GetSubKeyNames())
            {
                // Launcher, Social Club and similar entries are not games
                if (sub.IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sub.IndexOf("social club", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                using var game = key.OpenSubKey(sub);
                string dir = (game?.GetValue("InstallFolder") as string ?? string.Empty).Trim().Trim('"').TrimEnd('\\');
                if (dir.Length == 0 || !Directory.Exists(dir) || IsNonGameName(sub)) continue;

                string libId = $"rockstar:{sub}";
                if (TryReuse(libId, sub, "Rockstar", out var cached))
                {
                    yield return cached;
                    continue;
                }
                yield return Make(libId, sub, "Rockstar", FindGameExecutables(dir, sub, Path.GetFileName(dir)));
            }
        }

        // ------------------------------------------------------------------
        // Battle.net: uninstall entries whose uninstaller is "Battle.net … --uid=<product>" (DisplayName = game name)
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanBattleNet()
        {
            foreach (var (_, name, location, uninstall) in UninstallEntries())
            {
                var uid = Regex.Match(uninstall, @"Battle\.net.*--uid=([^\s""]+)", RegexOptions.IgnoreCase);
                if (!uid.Success) continue;
                // Public test realms and betas are separate installs of the same game
                if (name.EndsWith("Test", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Beta", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("PTR", StringComparison.Ordinal)) continue;
                string dir = location.Trim().Trim('"').TrimEnd('\\');
                if (name.Length == 0 || dir.Length == 0 || !Directory.Exists(dir) || IsNonGameName(name)) continue;
                // The Battle.net app registers itself the same way
                if (name.Equals("Battle.net", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(dir).Equals("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;

                string libId = $"battlenet:{uid.Groups[1].Value.ToLowerInvariant()}";
                if (TryReuse(libId, name, "Battle.net", out var cached))
                {
                    yield return cached;
                    continue;
                }
                yield return Make(libId, name, "Battle.net", FindGameExecutables(dir, name, Path.GetFileName(dir)));
            }
        }

        // ------------------------------------------------------------------
        // Amazon Games: %LocalAppData%\Amazon Games\Data\Games\Sql\GameInstallInfo.sqlite (table DbSet),
        // the launch exe from the game's fuel.json
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanAmazon()
        {
            string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Amazon Games", "Data", "Games", "Sql", "GameInstallInfo.sqlite");
            if (!File.Exists(db)) yield break;

            foreach (var row in ReadOnlySqlite.Query(db, "SELECT Id, ProductTitle, InstallDirectory FROM DbSet WHERE Installed = 1"))
            {
                string id = row[0] ?? string.Empty;
                string name = CleanName(row[1]);
                string dir = (row[2] ?? string.Empty).Replace('/', '\\').TrimEnd('\\');
                if (id.Length == 0 || name.Length == 0 || dir.Length == 0 || !Directory.Exists(dir) || IsNonGameName(name)) continue;

                string libId = $"amazon:{id}";
                if (TryReuse(libId, name, "Amazon Games", out var cached))
                {
                    yield return cached;
                    continue;
                }
                var exes = RegisteredOrFound(libId, name, "Amazon Games", FuelCommand(dir), dir, Path.GetFileName(dir));
                yield return Make(libId, name, "Amazon Games", exes);
            }
        }

        /// <summary>The exe in an Amazon game's fuel.json ("Main" › "Command"), or null.</summary>
        private static string? FuelCommand(string gameDir)
        {
            try
            {
                string fuel = Path.Combine(gameDir, "fuel.json");
                if (!File.Exists(fuel)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(fuel));
                if (!doc.RootElement.TryGetProperty("Main", out var main)) return null;
                string command = Str(main, "Command");
                return command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.Combine(gameDir, command.Replace('/', '\\')) : null;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // itch.io app: %AppData%\itch\db\butler.db – one "cave" per install, its "verdict" (JSON) lists the
        // install folder and the executables the app would launch
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanItch()
        {
            string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch", "db", "butler.db");
            if (!File.Exists(db)) yield break;

            const string sql = "SELECT games.id, games.title, games.classification, caves.verdict FROM caves JOIN games ON games.id = caves.game_id";
            foreach (var row in ReadOnlySqlite.Query(db, sql))
            {
                string id = row[0] ?? string.Empty;
                string name = CleanName(row[1]);
                // Tools, comics, soundtracks, assets … can be installed too
                if (id.Length == 0 || name.Length == 0 || !string.Equals(row[2], "game", StringComparison.OrdinalIgnoreCase) || IsNonGameName(name)) continue;

                string libId = $"itch:{id}";
                if (TryReuse(libId, name, "itch.io", out var cached))
                {
                    yield return cached;
                    continue;
                }

                var (dir, candidates) = ItchVerdict(row[3]);
                if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                var exes = candidates.Count > 0
                    ? RegisteredOrFound(libId, name, "itch.io", candidates[0], dir, Path.GetFileName(dir))
                    : FindGameExecutables(dir, name, Path.GetFileName(dir));
                // Further native Windows candidates (e.g. a DX11 and a DX12 exe) are watched as well
                foreach (string extraExe in candidates.Skip(1))
                    if (exes.Count < 3 && !exes.Contains(extraExe, StringComparer.OrdinalIgnoreCase) && !LooksLikeLauncher(extraExe)) exes.Add(extraExe);
                yield return Make(libId, name, "itch.io", exes);
            }
        }

        /// <summary>Install folder and native Windows executables (full paths that exist) of an itch.io install's verdict.</summary>
        private static (string Dir, List<string> Exes) ItchVerdict(string? json)
        {
            var exes = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return (string.Empty, exes);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string dir = Str(root, "basePath").Replace('/', '\\').TrimEnd('\\');
                if (dir.Length == 0) return (string.Empty, exes);
                if (root.TryGetProperty("candidates", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in list.EnumerateArray())
                    {
                        string path = Str(c, "path").Replace('/', '\\');
                        // "helper" marks runtime plumbing next to the game (crash handlers, bundled Java / Python …)
                        if (Str(c, "flavor") != "windows" || Str(c, "helper").Length > 0 ||
                            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsExcludedExe(Path.GetFileNameWithoutExtension(path))) continue;
                        string full = Path.Combine(dir, path);
                        if (File.Exists(full)) exes.Add(full);
                    }
                }
                return (dir, exes);
            }
            catch
            {
                return (string.Empty, exes);
            }
        }

        // ------------------------------------------------------------------
        // Riot Client: per-user uninstall entries "Riot Game <product>.<patchline>" (e.g. "Riot Game valorant.live")
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanRiot()
        {
            const string prefix = "Riot Game ";
            foreach (var (subKey, name, location, _) in UninstallEntries())
            {
                if (!subKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string product = subKey.Substring(prefix.Length).Trim().ToLowerInvariant();
                // The Riot Client itself, and test patchlines (PBE) next to the live game
                if (product.StartsWith("riot_client", StringComparison.Ordinal)) continue;
                int dot = product.LastIndexOf('.');
                if (dot > 0 && product.Substring(dot + 1) != "live") continue;

                string dir = location.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
                if (name.Length == 0 || dir.Length == 0 || !Directory.Exists(dir) || IsNonGameName(name)) continue;

                string libId = $"riot:{product}";
                if (TryReuse(libId, name, "Riot Games", out var cached))
                {
                    yield return cached;
                    continue;
                }
                yield return Make(libId, name, "Riot Games", FindGameExecutables(dir, name, Path.GetFileName(dir)));
            }
        }

        // ------------------------------------------------------------------
        // Heroic Games Launcher (%AppData%\heroic): its own install lists for Epic (legendary), GOG and Amazon (nile).
        // Games installed this way are not in the Epic / GOG / Amazon launchers' lists.
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanHeroic()
        {
            string heroic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "heroic");
            if (!Directory.Exists(heroic)) return Array.Empty<LibraryGame>();

            var games = new List<LibraryGame>();
            Try(() => games.AddRange(ScanHeroicEpic(Path.Combine(heroic, "legendaryConfig", "legendary", "installed.json"))), "Heroic (Epic)", _log);
            Try(() => games.AddRange(ScanHeroicGog(Path.Combine(heroic, "gog_store", "installed.json"))), "Heroic (GOG)", _log);
            Try(() => games.AddRange(ScanHeroicAmazon(Path.Combine(heroic, "nile_config", "nile", "installed.json"))), "Heroic (Amazon)", _log);
            return games;
        }

        /// <summary>legendary's installed.json: { "&lt;app name&gt;": { title, install_path, executable, is_dlc, platform } }.</summary>
        private static IEnumerable<LibraryGame> ScanHeroicEpic(string file)
        {
            if (!File.Exists(file)) yield break;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) yield break;

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var g = entry.Value;
                if (g.ValueKind != JsonValueKind.Object) continue;
                bool dlc = g.TryGetProperty("is_dlc", out var d) && d.ValueKind == JsonValueKind.True;
                string platform = Str(g, "platform");
                string name = CleanName(Str(g, "title"));
                string dir = Str(g, "install_path").Replace('/', '\\').TrimEnd('\\');
                if (dlc || (platform.Length > 0 && !platform.StartsWith("Win", StringComparison.OrdinalIgnoreCase)) ||
                    name.Length == 0 || dir.Length == 0 || !Directory.Exists(dir) || IsNonGameName(name)) continue;

                string libId = $"heroic-epic:{entry.Name}";
                if (TryReuse(libId, name, "Heroic (Epic)", out var cached))
                {
                    yield return cached;
                    continue;
                }
                string exe = Str(g, "executable");
                string? registered = exe.Length > 0 ? Path.Combine(dir, exe.Replace('/', '\\')) : null;
                yield return Make(libId, name, "Heroic (Epic)", RegisteredOrFound(libId, name, "Heroic (Epic)", registered, dir, Path.GetFileName(dir)));
            }
        }

        /// <summary>
        /// Heroic's gog_store\installed.json: { "installed": [ { appName, install_path, platform, is_dlc } ] }. Name and
        /// main exe come from the goggame-&lt;id&gt;.info file that GOG puts in every install folder.
        /// </summary>
        private static IEnumerable<LibraryGame> ScanHeroicGog(string file)
        {
            if (!File.Exists(file)) yield break;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("installed", out var list) || list.ValueKind != JsonValueKind.Array) yield break;

            foreach (var g in list.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                bool dlc = g.TryGetProperty("is_dlc", out var d) && d.ValueKind == JsonValueKind.True;
                string id = Str(g, "appName");
                string platform = Str(g, "platform");
                string dir = Str(g, "install_path").Replace('/', '\\').TrimEnd('\\');
                if (dlc || id.Length == 0 || (platform.Length > 0 && !platform.Equals("windows", StringComparison.OrdinalIgnoreCase)) ||
                    dir.Length == 0 || !Directory.Exists(dir)) continue;

                var (infoName, primaryExe) = GogInfo(dir, id);
                string name = CleanName(infoName ?? Path.GetFileName(dir));
                if (name.Length == 0 || IsNonGameName(name)) continue;

                string libId = $"heroic-gog:{id}";
                if (TryReuse(libId, name, "Heroic (GOG)", out var cached))
                {
                    yield return cached;
                    continue;
                }
                yield return Make(libId, name, "Heroic (GOG)", RegisteredOrFound(libId, name, "Heroic (GOG)", primaryExe, dir, Path.GetFileName(dir)));
            }
        }

        /// <summary>Game name and primary play task exe from goggame-&lt;id&gt;.info (null when the file is missing).</summary>
        private static (string? Name, string? Exe) GogInfo(string dir, string id)
        {
            try
            {
                string info = Path.Combine(dir, $"goggame-{id}.info");
                if (!File.Exists(info)) return (null, null);
                using var doc = JsonDocument.Parse(File.ReadAllText(info));
                var root = doc.RootElement;
                string name = Str(root, "name");
                string? exe = null;
                if (root.TryGetProperty("playTasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tasks.EnumerateArray())
                    {
                        bool primary = t.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True;
                        string path = Str(t, "path");
                        if (!primary || Str(t, "type") != "FileTask" || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        exe = Path.Combine(dir, path.Replace('/', '\\'));
                        break;
                    }
                }
                return (name.Length > 0 ? name : null, exe);
            }
            catch
            {
                return (null, null);
            }
        }

        /// <summary>nile's installed.json: [ { id, version, path, size } ]; the exe comes from the game's fuel.json.</summary>
        private static IEnumerable<LibraryGame> ScanHeroicAmazon(string file)
        {
            if (!File.Exists(file)) yield break;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) yield break;

            foreach (var g in doc.RootElement.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                string id = Str(g, "id");
                string dir = Str(g, "path").Replace('/', '\\').TrimEnd('\\');
                if (id.Length == 0 || dir.Length == 0 || !Directory.Exists(dir)) continue;
                // nile keeps no title here; Heroic names the install folder after the game
                string name = CleanName(Path.GetFileName(dir));
                if (name.Length == 0 || IsNonGameName(name)) continue;

                string libId = $"heroic-amazon:{id}";
                if (TryReuse(libId, name, "Heroic (Amazon)", out var cached))
                {
                    yield return cached;
                    continue;
                }
                yield return Make(libId, name, "Heroic (Amazon)", RegisteredOrFound(libId, name, "Heroic (Amazon)", FuelCommand(dir), dir, Path.GetFileName(dir)));
            }
        }

        // ------------------------------------------------------------------
        // Xbox Game Bar: games Windows recognised while they ran (HKCU\System\GameConfigStore\Children). Only programs
        // that no launcher lists; named after their game folder. An exe inside the folder of a library game is another
        // process of that game (the real game behind a starter) and is added to it instead.
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanGameBar(List<LibraryGame> library, ISet<string> knownProcesses, ISet<string>? notGames)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var libraryFolders = new List<(string Folder, LibraryGame Game)>();
            foreach (var g in library)
                if (!string.IsNullOrEmpty(g.ExePath) && HdrLauncher.GameFolder(g.ExePath) is { } f) libraryFolders.Add((f.TrimEnd('\\') + "\\", g));

            foreach (string exe in GameBarExePaths())
            {
                // Only .exe files: the process of "Aion.bin" is not called "aion"
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                string process = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (process.Length == 0 || seen.Contains(process) || knownProcesses.Contains(process) || notGames?.Contains(process) == true) continue;
                if (IsExcludedExe(process) || LooksLikeLauncher(exe) || AppCatalog.IsKnown(process) || LauncherClients.Contains(process)) continue;
                if (windows.Length > 0 && exe.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (!File.Exists(exe) || new FileInfo(exe).Length < 300 * 1024) continue;
                }
                catch
                {
                    continue;
                }
                seen.Add(process);   // only now: an old entry whose exe is gone must not hide a later one with the same name

                var owner = libraryFolders.FirstOrDefault(l => exe.StartsWith(l.Folder, StringComparison.OrdinalIgnoreCase)).Game;
                if (owner != null)
                {
                    // A tool of that game (editor, config, server …) is not added: the game's HDR setting would carry over to it
                    if (owner.Processes.Count < 4 && !IsToolLike(exe)) owner.Processes.Add(process);
                    knownProcesses.Add(process);
                    continue;
                }

                string name = GameBarName(exe, process);
                if (name.Length == 0 || IsNonGameName(name)) continue;
                yield return Make($"gamebar:{process}", name, "Game Bar", new List<string> { exe });
            }
        }

        // Game launcher apps themselves, which the Game Bar can record too
        private static readonly HashSet<string> LauncherClients = new(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamwebhelper", "galaxyclient", "upc", "ubisoftconnect", "eadesktop", "origin", "battle.net",
            "riotclientservices", "riotclientux", "amazon games ui", "itch", "heroic", "xboxpcapp", "playnite.desktopapp",
            "playnite.fullscreenapp"
        };

        // Folders inside a game folder that do not name the game ("Versions\Base95817", "_retail_", "RetailClient")
        private static readonly Regex GenericGameSubfolder = new(
            @"^(_.*|base\d+|bin\w*|binaries|client|game|live|release|retail|retailclient|shipping|system|versions|win32|win64|x64|x86)$",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// Name of a Game Bar game: its game folder, skipping folders inside it that do not name the game
        /// ("World of Warcraft\_retail_\Wow.exe" → "World of Warcraft"), or the exe's product name when the folder is
        /// only named like the exe; else the exe's description.
        /// </summary>
        private static string GameBarName(string exe, string process)
        {
            string? folder = HdrLauncher.GameFolder(exe);
            // GameFolder of a folder is its parent, with the same checks (never a drive root or "Games")
            for (int i = 0; i < 3 && folder != null && GenericGameSubfolder.IsMatch(Path.GetFileName(folder)); i++)
                folder = HdrLauncher.GameFolder(folder);
            if (folder == null || GenericGameSubfolder.IsMatch(Path.GetFileName(folder))) return AppCatalog.NameFor(process, exe);

            // A folder named like the exe ("swtor\retailclient\swtor.exe"): the product name is the better title
            string name = Path.GetFileName(folder);
            if (Normalize(name) == Normalize(process))
            {
                try
                {
                    string product = CleanName(System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductName);
                    if (product.Length >= 3 && product.Length <= 40 && product.Any(char.IsLetter)) return product;
                }
                catch { }
            }
            return name;
        }

        /// <summary>Exe paths in the Game Bar's list of played games ("MatchedExeFullPath" of each entry).</summary>
        private static List<string> GameBarExePaths()
        {
            var paths = new List<string>();
            try
            {
                using var store = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
                if (store == null) return paths;
                foreach (string sub in store.GetSubKeyNames())
                {
                    using var game = store.OpenSubKey(sub);
                    if (game?.GetValue("MatchedExeFullPath") is string path && path.Trim().Length > 0)
                        paths.Add(path.Trim().Trim('"'));
                }
            }
            catch { }
            return paths;
        }

        /// <summary>A store title without trademark signs ("Game™" → "Game").</summary>
        private static string CleanName(string? name) =>
            (name ?? string.Empty).Replace("™", string.Empty).Replace("®", string.Empty).Replace("©", string.Empty).Trim();

        private static string? UninstallDisplayName(string subKey)
        {
            foreach (string root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(root + "\\" + subKey);
                    if (key?.GetValue("DisplayName") is string name && name.Trim().Length > 0) return name.Trim();
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// (subkey, DisplayName, InstallLocation, UninstallString) of installed programs: machine-wide (32 + 64 bit) and
        /// per user (Riot Games registers its games for the current user only).
        /// </summary>
        private static IEnumerable<(string SubKey, string Name, string Location, string Uninstall)> UninstallEntries()
        {
            foreach (var (hive, root) in new[]
            {
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
            })
            {
                using var key = hive.OpenSubKey(root);
                if (key == null) continue;
                foreach (string sub in key.GetSubKeyNames())
                {
                    string name = string.Empty, location = string.Empty, uninstall = string.Empty;
                    try
                    {
                        using var entry = key.OpenSubKey(sub);
                        if (entry == null) continue;
                        name = (entry.GetValue("DisplayName") as string ?? string.Empty).Trim();
                        location = entry.GetValue("InstallLocation") as string ?? string.Empty;
                        uninstall = entry.GetValue("UninstallString") as string ?? string.Empty;
                    }
                    catch { continue; }
                    yield return (sub, name, location, uninstall);
                }
            }
        }

        // ------------------------------------------------------------------
        // Xbox app / PC Game Pass: <drive>:\<install folder>\<game>\Content\MicrosoftGame.config. The install folder is
        // "XboxGames" unless another one was chosen in the Xbox app (read from the drive's .GamingRoot file).
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanXbox()
        {
            var roots = new List<string>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                string driveRoot = drive.RootDirectory.FullName;
                var candidates = new List<string> { Path.Combine(driveRoot, "XboxGames") };
                try
                {
                    // A few hundred bytes from the Xbox app; anything large is not that file
                    var gamingRoot = new FileInfo(Path.Combine(driveRoot, ".GamingRoot"));
                    if (gamingRoot.Exists && gamingRoot.Length <= 64 * 1024)
                        candidates.AddRange(ParseGamingRoot(File.ReadAllBytes(gamingRoot.FullName))
                            .Select(f => Path.GetFullPath(Path.Combine(driveRoot, f)))
                            .Where(f => f.StartsWith(driveRoot, StringComparison.OrdinalIgnoreCase)));
                }
                catch { }

                foreach (string folder in candidates)
                    if (Directory.Exists(folder) && !roots.Contains(folder.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase))
                        roots.Add(folder.TrimEnd('\\'));
            }

            foreach (string root in roots)
            {
                // A folder that cannot be read only skips that folder, not the other drives
                List<string> gameDirs;
                try { gameDirs = Directory.EnumerateDirectories(root).ToList(); }
                catch { continue; }

                foreach (string gameDir in gameDirs)
                {
                    LibraryGame? game = null;
                    try
                    {
                        string content = Path.Combine(gameDir, "Content");
                        string config = Path.Combine(content, "MicrosoftGame.config");
                        if (!File.Exists(config)) continue;

                        var doc = XDocument.Load(config);
                        string folderName = Path.GetFileName(gameDir);
                        string? display = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals")
                            ?.Attribute("DefaultDisplayName")?.Value;
                        string name = !string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)
                            ? display
                            : folderName;

                        var exes = doc.Descendants()
                            .Where(e => e.Name.LocalName == "Executable")
                            .Select(e => e.Attribute("Name")?.Value)
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => Path.Combine(content, v!.Replace('/', '\\')))
                            .Where(p => !IsExcludedExe(Path.GetFileNameWithoutExtension(p)))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(3)
                            .ToList();

                        if (!IsNonGameName(name))
                            game = Make($"xbox:{folderName}", name, "Xbox", exes);
                    }
                    catch { }

                    if (game != null) yield return game;
                }
            }
        }

        /// <summary>
        /// Install folders listed in a drive's hidden ".GamingRoot" file, which the Xbox app writes to the root of every
        /// drive it installs games to: the bytes "RGBX", a 32-bit count, then UTF-16 folder paths relative to the drive
        /// root, each ending in a null character (e.g. "XboxGames"). Returns nothing for a file in another format.
        /// </summary>
        internal static List<string> ParseGamingRoot(byte[] data)
        {
            var folders = new List<string>();
            if (data.Length < 10 || data[0] != 'R' || data[1] != 'G' || data[2] != 'B' || data[3] != 'X') return folders;

            string text = System.Text.Encoding.Unicode.GetString(data, 8, (data.Length - 8) & ~1);
            foreach (string part in text.Split('\0'))
            {
                string folder = part.Trim().Replace('/', '\\').Trim('\\');
                // Relative to the drive root only: never another drive, a network path (\\server, //server) or a parent folder
                if (folder.Length == 0 || folder.Contains(':') || folder.Contains("..") || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
                    Path.IsPathRooted(folder)) continue;
                if (!folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) folders.Add(folder);
            }
            return folders;
        }

        // ------------------------------------------------------------------
        // Exe locations Windows already knows about (no disk search, only registry reads):
        //  - Game Bar's list of games you have played (GameConfigStore, "MatchedExeFullPath")
        //  - the Explorer program-name cache (MuiCache, value names are full exe paths)
        //  - the Program Compatibility Assistant list of programs that were started
        //  - "DisplayIcon" of installed programs (Apps & features)
        // Returns process name (lower case, no .exe) -> full path, only for files that still exist.
        // ------------------------------------------------------------------
        public static Dictionary<string, string> FindKnownExePaths()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                path = path.Trim().Trim('"');
                int comma = path.LastIndexOf(".exe,", StringComparison.OrdinalIgnoreCase);   // "C:\x\game.exe,0"
                if (comma >= 0) path = path.Substring(0, comma + 4);
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
                string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (name.Length == 0 || result.ContainsKey(name)) return;
                try { if (File.Exists(path)) result[name] = path; } catch { }
            }

            // Game Bar / Game Mode: one subkey per game that was played
            foreach (string path in GameBarExePaths()) Add(path);

            // Explorer's MuiCache: value names like "C:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe.FriendlyAppName"
            try
            {
                using var mui = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
                if (mui != null)
                {
                    foreach (string valueName in mui.GetValueNames())
                    {
                        int dot = valueName.LastIndexOf(".exe.", StringComparison.OrdinalIgnoreCase);
                        if (dot > 0) Add(valueName.Substring(0, dot + 4));
                    }
                }
            }
            catch { }

            // Program Compatibility Assistant: value names are full paths of started programs
            try
            {
                using var pca = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store");
                if (pca != null)
                    foreach (string valueName in pca.GetValueNames()) Add(valueName);
            }
            catch { }

            // Installed programs (machine-wide and per user, 64- and 32-bit)
            foreach (var (hive, keyPath) in new[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
            })
            {
                try
                {
                    using var uninstall = hive.OpenSubKey(keyPath);
                    if (uninstall == null) continue;
                    foreach (string sub in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(sub);
                        Add(app?.GetValue("DisplayIcon") as string);
                    }
                }
                catch { }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        /// <summary>
        /// True when this game was detected before and its main exe still exists, so the cached entry can be reused.
        /// Entries found by an older version are detected once more.
        /// </summary>
        private static bool CanReuse(string id)
        {
            var prev = _previous;
            return prev != null && prev.TryGetValue(id, out var old) && old.Processes.Count > 0 && !string.IsNullOrEmpty(old.ExePath) &&
                   File.Exists(old.ExePath) && old.ScanVersion >= CurrentScanVersion;
        }

        private static bool TryReuse(string id, string name, string source, out LibraryGame game)
        {
            game = null!;
            if (!CanReuse(id)) return false;
            var old = _previous![id];

            game = new LibraryGame
            {
                Id = id,
                Name = name.Trim(),
                Source = source,
                Processes = new List<string>(old.Processes),
                ExePath = old.ExePath,
                ScanVersion = old.ScanVersion
            };
            return true;
        }

        private static LibraryGame Make(string id, string name, string source, List<string> exePaths)
        {
            return new LibraryGame
            {
                Id = id,
                Name = name.Trim(),
                Source = source,
                Processes = exePaths
                    .Select(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant())
                    .Where(p => p.Length > 0)
                    .Distinct()
                    .ToList(),
                ExePath = exePaths.FirstOrDefault(File.Exists),
                ScanVersion = CurrentScanVersion
            };
        }

        /// <summary>
        /// True for programs that only start the real game: names containing "launcher", a few known stubs
        /// (REDprelauncher, PlayGTAV, start_protected_game, Paradox's dowser, …) and exes in a launcher folder.
        /// </summary>
        public static bool LooksLikeLauncher(string exePathOrName)
        {
            string key = Normalize(Path.GetFileNameWithoutExtension(exePathOrName));
            if (key.Contains("launcher") || KnownLaunchers.Contains(key)) return true;
            // An exe in a launcher folder ("Launcher\eg2.exe", "ParadoxLauncher\dowser.exe")
            string? dir = Path.GetDirectoryName(exePathOrName);
            return !string.IsNullOrEmpty(dir) && Normalize(Path.GetFileName(dir)).Contains("launcher");
        }

        /// <summary>
        /// Executables of a library game whose launcher registers an exe (GOG, Epic). If that exe is a launcher, the
        /// install folder is searched for the real game instead, so the app watches the program that actually runs
        /// while playing. The launcher is only kept when nothing else is found.
        /// </summary>
        private static List<string> RegisteredOrFound(string id, string name, string source, string? registeredExe, string folder, string installDir)
        {
            bool folderOk = folder.Length > 0 && Directory.Exists(folder);
            var exes = new List<string>();

            if (!string.IsNullOrEmpty(registeredExe) && File.Exists(registeredExe))
            {
                if (folderOk && LooksLikeLauncher(registeredExe))
                {
                    var found = FindGameExecutables(folder, name, installDir, skipLaunchers: true);
                    if (found.Count > 0)
                    {
                        // Logged once, not at every start (unchanged result = same processes as last time)
                        var names = found.Select(e => Path.GetFileNameWithoutExtension(e).ToLowerInvariant()).Distinct().ToList();
                        bool known = _previous != null && _previous.TryGetValue(id, out var old) &&
                                     names.All(n => old.Processes.Contains(n, StringComparer.OrdinalIgnoreCase));
                        if (!known)
                            _log?.Invoke($"{source}: '{name}' registers {Path.GetFileName(registeredExe)}, which is a launcher – " +
                                         $"watching {string.Join(", ", names.Select(n => n + ".exe"))} instead.");
                        return found;
                    }
                }
                exes.Add(registeredExe);
                if (folderOk) AddShippingCounterpart(exes, folder);
                return exes;
            }

            return folderOk ? FindGameExecutables(folder, name, installDir) : exes;
        }

        public static bool IsExcludedExeName(string fileNameWithoutExt) => IsExcludedExe(fileNameWithoutExt);

        private static bool IsNonGameName(string name)
        {
            string n = " " + name.ToLowerInvariant();
            return NonGameNameParts.Any(n.Contains);
        }

        private static bool IsExcludedExe(string fileNameWithoutExt)
        {
            string n = fileNameWithoutExt.ToLowerInvariant();
            return ExcludedExeParts.Any(n.Contains);
        }

        /// <summary>
        /// True for an exe below <paramref name="folder"/> in an installer folder (<see cref="InstallerFolders"/>) or in a
        /// bundled Java runtime ("jre", "jdk-17", "java_vm"), whose tools are never the game.
        /// </summary>
        private static bool InInstallerFolder(string exe, string folder)
        {
            string root = folder.TrimEnd('\\');
            for (string? dir = Path.GetDirectoryName(exe); dir != null && dir.Length > root.Length; dir = Path.GetDirectoryName(dir))
            {
                string name = Path.GetFileName(dir);
                if (InstallerFolders.Contains(name) || name.Equals("java_vm", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("jre", StringComparison.OrdinalIgnoreCase) || name.StartsWith("jdk", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>
        /// Picks the most likely game executable(s) in an install folder: names similar to the game,
        /// Unreal "-Win64-Shipping" binaries (the process that actually runs), and large files score higher;
        /// launchers, installers, crash reporters etc. are ignored. With <paramref name="skipLaunchers"/>, exes that look
        /// like launchers are left out completely (the search behind a registered launcher).
        /// </summary>
        public static List<string> FindGameExecutables(string folder, string gameName, string installDir, bool skipLaunchers = false)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            string nameKey = Normalize(gameName);
            string dirKey = Normalize(installDir);
            string initials = Initials(gameName);

            // Exes under 300 KB only count when named like the game, or when the folder has no larger exe: some games
            // keep their engine in a DLL behind a small exe (Far Cry 3 Blood Dragon, Bannerlord)
            var candidates = new List<(string Path, double Score)>();
            var small = new List<(string Path, double Score)>();
            foreach (string exe in Directory.EnumerateFiles(folder, "*.exe", options))
            {
                string file = Path.GetFileNameWithoutExtension(exe);
                if (IsExcludedExe(file) || InInstallerFolder(exe, folder)) continue;
                // Looking past a launcher: it may be named like the game ("Launcher\eg2.exe" for Evil Genius 2)
                if (skipLaunchers && LooksLikeLauncher(exe)) continue;

                long size;
                try { size = new FileInfo(exe).Length; } catch { continue; }
                if (size < 64 * 1024) continue;

                string key = Normalize(file);
                double score = Math.Log10(size);   // ~4.8 (64 KB) .. ~8.5 (300 MB)

                bool namedLikeGame = true;
                if (key.Length > 0 && (key == nameKey || key == dirKey)) score += 6;
                else if (key.Length >= 3 && key == initials) score += 6;   // "AOW4.exe" for Age of Wonders 4
                else if (key.Length >= 3 && (nameKey.Contains(key) || dirKey.Contains(key) || key.Contains(dirKey))) score += 3;
                else namedLikeGame = false;

                if (file.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase)) score += 5;
                if (key.Contains("launcher")) score -= 4;
                if (key.Contains("server") || key.Contains("editor") || key.Contains("tool")) score -= 3;

                int depth = exe.Substring(folder.Length).Count(c => c == '\\');
                score -= Math.Max(0, depth - 1) * 0.3;

                (size >= 300 * 1024 || namedLikeGame ? candidates : small).Add((exe, score));
            }
            if (candidates.Count == 0) candidates = small;

            var ordered = candidates.OrderByDescending(c => c.Score).ToList();
            var result = ordered.Take(1).Select(c => c.Path).ToList();
            if (result.Count == 0) return result;
            AddShippingCounterpart(result, folder);

            // Some games ship separate exes, e.g. for DX11 / DX12 / Vulkan: also watch other exes that score almost as
            // well as the best one (at most 3 names in total). Only exes that clearly belong to the game: the name
            // matches the game / folder name, or starts like the main exe ("Game_DX12" next to "Game"). Tools,
            // launchers, installers and patchers are never added (e.g. a bundled "p4vinst64.exe" next to "OLGame.exe").
            // An exe whose name adds a short suffix to the main exe's name counts at any score: "Heroes of the Storm.exe"
            // only starts "HeroesOfTheStorm_x64.exe" in a subfolder (but "Fallout3 - Garden of Eden Creation Kit" is
            // the editor).
            double best = ordered[0].Score;
            string bestKey = Normalize(Path.GetFileNameWithoutExtension(ordered[0].Path));
            var names = new HashSet<string>(result.Select(p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);
            foreach (var (path, score) in ordered.Skip(1))
            {
                if (names.Count >= 3) break;
                string file = Path.GetFileNameWithoutExtension(path);
                string lower = file.ToLowerInvariant();
                if (names.Contains(file) || NotAGameExeParts.Any(lower.Contains) || LooksLikeLauncher(file)) continue;

                string key = Normalize(file);
                // The main name plus a tool ending ("Sins of a Solar Empire Rebellion Dev")
                if (key.Length > bestKey.Length && key.StartsWith(bestKey) && ToolEndings.Contains(key.Substring(bestKey.Length))) continue;
                bool extendsMain = bestKey.Length >= 4 && key.Length > bestKey.Length && key.Length <= bestKey.Length + 5 && key.StartsWith(bestKey);
                if (score < best - 1.0 && !extendsMain) continue;
                bool namedLikeGame = key.Length >= 3 && (key == nameKey || key == dirKey || nameKey.Contains(key) || dirKey.Contains(key));
                bool sameStem = key.Length >= 4 && bestKey.Length >= 4 && (key.StartsWith(bestKey) || bestKey.StartsWith(key) ||
                                CommonPrefixLength(key, bestKey) >= Math.Max(4, Math.Min(key.Length, bestKey.Length) - 2));
                if (!namedLikeGame && !sameStem) continue;

                result.Add(path);
                names.Add(file);
            }
            return result;
        }

        /// <summary>First letters of a game name's words, numbers kept whole: "Age of Wonders 4" → "aow4".</summary>
        private static string Initials(string name)
        {
            var words = Regex.Split(name.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
            if (words.Count < 2) return string.Empty;
            return string.Concat(words.Select(w => w.All(char.IsDigit) ? w : w.Substring(0, 1)));
        }

        private static int CommonPrefixLength(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }

        /// <summary>Unreal games: "Game.exe" only starts "Game-Win64-Shipping.exe" (the real process) and the other way round.</summary>
        private static void AddShippingCounterpart(List<string> exes, string folder)
        {
            try
            {
                string main = Path.GetFileNameWithoutExtension(exes[0]);
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };

                if (main.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) return;

                string? shipping = Directory.EnumerateFiles(folder, main + "-Win64-Shipping.exe", options).FirstOrDefault()
                                   ?? Directory.EnumerateFiles(folder, main + "-WinGDK-Shipping.exe", options).FirstOrDefault();
                if (shipping != null && !exes.Contains(shipping, StringComparer.OrdinalIgnoreCase))
                    exes.Add(shipping);
            }
            catch { }
        }
    }
}
