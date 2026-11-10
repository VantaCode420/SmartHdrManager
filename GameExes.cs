using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmartHdrManager
{
    /// <summary>One exe a game is watched by, for Games tab › Options › Programs.</summary>
    public sealed class GameExe
    {
        public string Process { get; init; } = string.Empty;   // lower case, no .exe
        public string? Path { get; init; }                     // null when only the name is known (e.g. learned from a launcher)
        public GameExeOrigin Origin { get; init; }
    }

    public enum GameExeOrigin { Detected, Learned, Added }

    /// <summary>
    /// The exes of a library game that you changed by hand (GitHub issue #11). Stored as a difference to what the scan finds
    /// (<see cref="AppConfig.GameExeOverrides"/>), so a rescan finds the game again and your changes are applied on top.
    /// </summary>
    public sealed partial class HdrEngine
    {
        internal static string ProcessNameOf(string exePath)
        {
            try { return System.IO.Path.GetFileNameWithoutExtension(exePath.Trim()).ToLowerInvariant(); }
            catch { return string.Empty; }
        }

        /// <summary>Caller holds Sync.</summary>
        private bool ExeRemovedByUser(string gameId, string process) =>
            Config.GameExeOverrides.TryGetValue(gameId, out var o) && o != null && o.Removed.Contains(process, Cmp);

        /// <summary>Applies the exes changed in Options to freshly scanned games. No disk access (runs under the lock). Caller holds Sync.</summary>
        private void ApplyExeOverrides(List<LibraryGame> games, bool log)
        {
            var overrides = Config.GameExeOverrides;
            if (overrides == null || overrides.Count == 0) return;
            foreach (var game in games)
            {
                if (!overrides.TryGetValue(game.Id, out var o) || o == null || o.IsEmpty) continue;
                var before = game.Processes.ToList();
                ApplyOverride(game, o);
                if (log && !before.SequenceEqual(game.Processes, Cmp))
                    Log("SCAN", $"'{game.Name}': your exe changes applied – watching {string.Join(", ", game.Processes.Select(p => p + ".exe"))}.");
            }
        }

        /// <summary>Adds the exes you added and drops the ones you removed. Never leaves a game without an exe.</summary>
        private static void ApplyOverride(LibraryGame game, GameExeOverride o)
        {
            foreach (string path in o.Added)
            {
                string process = ProcessNameOf(path);
                if (process.Length == 0) continue;
                if (!game.Processes.Contains(process, Cmp)) game.Processes.Add(process);
                if (!game.ExePaths.Contains(path, Cmp)) game.ExePaths.Add(path);
            }

            var removed = new HashSet<string>(o.Removed, Cmp);
            removed.ExceptWith(o.Added.Select(ProcessNameOf));   // added again later: the newer choice wins
            if (removed.Count > 0 && game.Processes.Any(p => !removed.Contains(p)))
            {
                game.Processes.RemoveAll(removed.Contains);
                game.ExePaths.RemoveAll(p => removed.Contains(ProcessNameOf(p)));
            }

            // The main exe (icon, Launch with HDR) is the first one left
            if (game.ExePath == null || !game.Processes.Contains(ProcessNameOf(game.ExePath), Cmp))
                game.ExePath = game.ExePaths.FirstOrDefault() ?? game.ExePath;
        }

        /// <summary>
        /// The game as the scan found it, plus the launchers' games and the given changes. Works on a copy. Caller holds Sync.
        /// </summary>
        private LibraryGame Rebuild(LibraryGame game, GameExeOverride? change)
        {
            var copy = CopyOf(game);
            if (copy.DetectedProcesses.Count > 0)   // always, except for a game saved before 3.0.0 and not scanned since
            {
                copy.Processes = new List<string>(copy.DetectedProcesses);
                copy.ExePaths = new List<string>(copy.DetectedExePaths);
                copy.ExePath = copy.DetectedExePaths.FirstOrDefault() ?? game.ExePath;
            }
            var list = new List<LibraryGame> { copy };
            MergeLearnedLaunchers(list);
            if (change != null && !change.IsEmpty) ApplyOverride(copy, change);
            return copy;
        }

        private LibraryGame? FindLibraryGame(string gameId) =>
            (Config.LibraryGames ?? new List<LibraryGame>()).FirstOrDefault(g => g.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase));

        /// <summary>A copy of the exe changes stored for a library game (empty when there are none).</summary>
        public GameExeOverride GetGameExeOverride(string gameId)
        {
            lock (Sync)
            {
                return Config.GameExeOverrides.TryGetValue(gameId, out var o) && o != null
                    ? new GameExeOverride { Added = new List<string>(o.Added), Removed = new List<string>(o.Removed) }
                    : new GameExeOverride();
            }
        }

        /// <summary>
        /// The exes a library game is watched by, with where each one comes from: as stored, or (with
        /// <paramref name="draft"/>) as they would be with those changes. Null for an unknown id.
        /// </summary>
        public List<GameExe>? GetGameExes(string gameId, GameExeOverride? draft = null)
        {
            lock (Sync)
            {
                var stored = FindLibraryGame(gameId);
                if (stored == null) return null;
                var o = draft ?? (Config.GameExeOverrides.TryGetValue(stored.Id, out var s) ? s : null);
                var game = Rebuild(stored, o);
                var detected = new HashSet<string>(stored.DetectedProcesses.Count > 0 ? stored.DetectedProcesses : stored.Processes, Cmp);
                var added = new HashSet<string>((o?.Added ?? new List<string>()).Select(ProcessNameOf), Cmp);

                var result = new List<GameExe>();
                foreach (string process in game.Processes)
                {
                    string? path = game.ExePaths.LastOrDefault(p => ProcessNameOf(p).Equals(process, StringComparison.OrdinalIgnoreCase));
                    if (path == null && game.ExePath != null && ProcessNameOf(game.ExePath).Equals(process, StringComparison.OrdinalIgnoreCase)) path = game.ExePath;
                    if (path == null && Config.ProgramPaths.TryGetValue(process, out var known)) path = known;
                    result.Add(new GameExe
                    {
                        Process = process,
                        Path = path,
                        Origin = added.Contains(process) ? GameExeOrigin.Added : detected.Contains(process) ? GameExeOrigin.Detected : GameExeOrigin.Learned
                    });
                }
                return result;
            }
        }

        /// <summary>The name of another game (library or not) that is already watched by this process, or null.</summary>
        public string? GameUsingProcess(string process, string exceptGameId)
        {
            lock (Sync)
            {
                var library = Config.LibraryGames ?? new List<LibraryGame>();
                var other = library.FirstOrDefault(g => !g.Id.Equals(exceptGameId, StringComparison.OrdinalIgnoreCase) && g.Processes.Contains(process, Cmp));
                if (other != null) return other.Name;
                // A program you added by hand (not part of any library game)
                bool byHand = Config.HdrGamesAndApps.Contains(process, Cmp) && !library.Any(g => g.Processes.Contains(process, Cmp));
                return byHand && !exceptGameId.Equals("proc:" + process, StringComparison.OrdinalIgnoreCase) ? process : null;
            }
        }

        /// <summary>
        /// Stores your exe changes for a library game and applies them right away: HDR on, Never HDR and the game options
        /// move to the new exes like after a rescan. Null or empty = back to what the scan found.
        /// </summary>
        public void SetGameExeOverride(string gameId, GameExeOverride? change)
        {
            LibraryGame copy;
            string name;
            lock (Sync)
            {
                var game = FindLibraryGame(gameId);
                if (game == null) return;
                name = game.Name;
                var old = CopyOf(game);

                GameExeOverride? stored = null;
                if (change != null)
                {
                    stored = new GameExeOverride
                    {
                        Added = change.Added.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(Cmp).ToList(),
                        Removed = change.Removed.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim().ToLowerInvariant()).Distinct(Cmp).ToList()
                    };
                }
                if (stored == null || stored.IsEmpty) Config.GameExeOverrides.Remove(game.Id);
                else Config.GameExeOverrides[game.Id] = stored;

                // Rebuilt from what the scan found, the launchers' games and your changes
                var rebuilt = Rebuild(game, stored);
                game.Processes = rebuilt.Processes;
                game.ExePaths = rebuilt.ExePaths;
                game.ExePath = rebuilt.ExePath;

                CarryOverGameSettings(Config.LibraryGames.Select(g => ReferenceEquals(g, game) ? old : g).ToList(), Config.LibraryGames);
                InvalidateSnapshot();
                Save();
                copy = CopyOf(game);

                var o = stored == null || stored.IsEmpty ? null : stored;
                Log("LIST", o == null
                    ? $"'{name}': exes reset to the ones found automatically – watching {string.Join(", ", game.Processes.Select(p => p + ".exe"))}."
                    : $"'{name}': exes changed by you – watching {string.Join(", ", game.Processes.Select(p => p + ".exe"))}" +
                      (o.Added.Count > 0 ? $"; added {string.Join(", ", o.Added)}" : string.Empty) +
                      (o.Removed.Count > 0 ? $"; removed {string.Join(", ", o.Removed.Select(p => p + ".exe"))}" : string.Empty) + ".");
            }

            // The RenoDX check looks next to the exes: check again outside the lock
            GameLibraryScanner.DetectRenoDx(new[] { copy });
            lock (Sync)
            {
                var game = Config.LibraryGames.FirstOrDefault(g => g.Id.Equals(gameId, StringComparison.OrdinalIgnoreCase));
                if (game != null && (game.RenoDx != copy.RenoDx || game.RenoDxAddon != copy.RenoDxAddon))
                {
                    game.RenoDx = copy.RenoDx;
                    game.RenoDxAddon = copy.RenoDxAddon;
                    InvalidateSnapshot();
                    Save();
                    Log("LIST", copy.RenoDx ? $"'{name}': RenoDX found (renodx-{copy.RenoDxAddon})." : $"'{name}': no RenoDX HDR mod found next to its exes.");
                }
            }
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// A program outside the game libraries: watch another exe instead (Games tab › Options › Change exe…). HDR on, Never
        /// HDR and the game options move to the new process name.
        /// </summary>
        public void ReplaceProgramExe(string oldProcess, string newExePath)
        {
            string newProcess = ProcessNameOf(newExePath);
            if (newProcess.Length == 0) return;
            lock (Sync)
            {
                static void Rename(List<string> list, string from, string to)
                {
                    if (list.RemoveAll(n => n.Equals(from, StringComparison.OrdinalIgnoreCase)) > 0 && !list.Contains(to, Cmp)) list.Add(to);
                }
                if (!newProcess.Equals(oldProcess, StringComparison.OrdinalIgnoreCase))
                {
                    Rename(Config.HdrGamesAndApps, oldProcess, newProcess);
                    Rename(Config.IgnoredGames, oldProcess, newProcess);
                    Rename(Config.DiscoveredGames, oldProcess, newProcess);
                    Config.HdrGamesAndApps.Sort(Cmp);
                    if (Config.PerGameOptions.Remove(oldProcess, out var options) && !Config.PerGameOptions.ContainsKey(newProcess))
                        Config.PerGameOptions[newProcess] = options;
                    Config.ProgramPaths.Remove(oldProcess);
                }
                Config.ProgramPaths[newProcess] = newExePath;
                InvalidateSnapshot();
                Save();
            }
            Log("LIST", newProcess.Equals(oldProcess, StringComparison.OrdinalIgnoreCase)
                ? $"'{oldProcess}': exe location set to {newExePath}."
                : $"'{oldProcess}.exe' replaced by '{newProcess}.exe' ({newExePath}) – HDR setting kept.");
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
