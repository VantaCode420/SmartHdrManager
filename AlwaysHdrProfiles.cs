using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartHdrManager
{
    /// <summary>
    /// Always-HDR: puts the corrected or the calibration-only profile into each display's HDR slot. Idempotent: it looks
    /// at what Windows has now and changes only the difference. The original default of each display is saved before
    /// anything changes, keyed by the monitor's device path, so it can be restored even after a crash.
    /// Only profiles named SHM_* are ever installed, changed or removed. Used on one thread at a time.
    /// </summary>
    internal sealed class AlwaysHdrProfiles
    {
        public const double Gamma = 2.2;
        public static readonly string StatePath = Path.Combine(ColorProfileLog.Folder, "always-hdr-state.json");
        public static readonly string ProfileFolder = Path.Combine(ColorProfileLog.Folder, "profiles");

        public sealed class DisplayRecord
        {
            public string DevicePath { get; set; } = string.Empty;
            public int Scope { get; set; }
            public string? OriginalDefault { get; set; }          // null: the display had no HDR-slot profile
            public List<string> Generated { get; set; } = new();   // SHM_* profiles installed for this display
        }

        private readonly List<DisplayRecord> _state;
        private readonly Dictionary<string, string> _problems = new(StringComparer.OrdinalIgnoreCase);
        private string? _colorDir;

        public AlwaysHdrProfiles() => _state = Load();

        /// <summary>Why a display runs with calibration only, by device path (empty when everything works).</summary>
        public IReadOnlyDictionary<string, string> Problems => _problems;

        public bool HasSavedState => _state.Count > 0;

        public static bool IsOurs(string? profile) => profile != null && profile.StartsWith("SHM_", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Before the mode is turned on: true when at least one HDR display has a profile in its HDR slot (normally the one
        /// the Windows HDR Calibration app made). Read-only. <paramref name="hdrDisplays"/>: how many HDR displays there are.
        /// </summary>
        public static bool AnyCalibrationProfile(out int hdrDisplays)
        {
            hdrDisplays = 0;
            foreach (var d in HdrController.ListDisplayTargets().Where(x => x.HdrSupported))
            {
                hdrDisplays++;
                if (ColorProfileNative.GetUserScope(d.AdapterId, d.SourceId, out int scope) != 0) continue;
                if (ColorProfileNative.GetDefault(scope, d.AdapterId, d.SourceId, ColorProfileNative.SubtypeHdr, out _) != null) return true;
            }
            return false;
        }

        private static List<DisplayRecord> Load()
        {
            try
            {
                if (File.Exists(StatePath))
                    return JsonSerializer.Deserialize<List<DisplayRecord>>(File.ReadAllText(StatePath)) ?? new List<DisplayRecord>();
            }
            catch (Exception ex)
            {
                ColorProfileLog.Write($"Saved state could not be read ({ex.Message}); starting without it.");
            }
            return new List<DisplayRecord>();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(ColorProfileLog.Folder);
                if (_state.Count == 0)
                {
                    File.Delete(StatePath);
                    return;
                }
                string temp = StatePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, StatePath, overwrite: true);
            }
            catch (Exception ex)
            {
                ColorProfileLog.Write("Saving the state failed: " + ex.Message);
            }
        }

        private string? ColorDir => _colorDir ??= ColorProfileNative.GetColorDirectory();

        private static string Short(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];

        private readonly Dictionary<string, string> _loggedProblems = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records why a display runs with calibration only. Logged once per new reason, not at every check.</summary>
        private void Problem(HdrController.DisplayTarget d, string text)
        {
            _problems[d.DevicePath] = text;
            if (_loggedProblems.TryGetValue(d.DevicePath, out string? last) && last == text) return;
            _loggedProblems[d.DevicePath] = text;
            ColorProfileLog.Write($"PROBLEM {d.Name}: {text}");
        }

        /// <summary>The record of a display, created (and saved) before the first change. Null when it can't be used.</summary>
        private DisplayRecord? Record(HdrController.DisplayTarget d, out string? current, bool quiet)
        {
            current = null;
            var record = _state.FirstOrDefault(r => r.DevicePath.Equals(d.DevicePath, StringComparison.OrdinalIgnoreCase));
            int scope;
            if (record != null)
            {
                scope = record.Scope;
            }
            else
            {
                int hr = ColorProfileNative.GetUserScope(d.AdapterId, d.SourceId, out scope);
                if (hr != 0) { Problem(d, $"Windows did not report the profile scope (0x{hr:X8})."); return null; }
            }

            current = ColorProfileNative.GetDefault(scope, d.AdapterId, d.SourceId, ColorProfileNative.SubtypeHdr, out _, quiet && record != null);
            if (record != null) return record;

            if (IsOurs(current))
            {
                Problem(d, $"The HDR slot holds '{current}' but no saved state exists. Use \"Remove Always-HDR profiles\" and pick your calibration in Windows.");
                return null;
            }
            record = new DisplayRecord { DevicePath = d.DevicePath, Scope = scope, OriginalDefault = current };
            _state.Add(record);
            Save();   // before anything changes
            ColorProfileLog.Write($"Saved original state: {d.DevicePath} scope={scope} default='{current}'");
            return record;
        }

        /// <summary>
        /// Shows the corrected profile (at <paramref name="white"/> nits) or the calibration-only profile on a display
        /// with HDR on. <paramref name="force"/> sets it again even if Windows already reports it (after standby Windows can
        /// drop the transform). Any failure falls back to calibration only. Returns false when something went wrong.
        /// </summary>
        /// <param name="liftPercent">Shadow lift of the corrected profile, 0–100 % (0 = the full gamma 2.2 correction).</param>
        public bool Apply(HdrController.DisplayTarget d, bool corrected, double white, bool force, string trigger, int liftPercent = 0)
        {
            if (!d.HdrEnabled)   // the HDR slot only matters while HDR is on
            {
                _problems.Remove(d.DevicePath);
                return true;
            }
            bool quiet = trigger == "poll";   // the once-a-second check: only changes and problems are logged

            var record = Record(d, out string? current, quiet);
            if (record == null) return false;
            bool hadProblem = _problems.Remove(d.DevicePath);

            // Re-calibrated, or another tool set its own profile: the two can't be told apart, so it becomes the new base
            if (current != null && !IsOurs(current) && !string.Equals(current, record.OriginalDefault, StringComparison.OrdinalIgnoreCase))
            {
                ColorProfileLog.Write($"{d.Name}: the HDR slot now holds '{current}' instead of '{record.OriginalDefault}' – adopted as the new calibration.");
                record.OriginalDefault = current;
                Save();
            }
            if (record.OriginalDefault == null)
            {
                Problem(d, "No Windows HDR Calibration profile on this display. Run the Windows HDR Calibration app first.");
                return false;
            }

            string target = record.OriginalDefault;
            bool ok = true;
            if (corrected)
            {
                string? name = EnsureCorrected(d, record, white, liftPercent);
                if (name != null) target = name;
                else ok = false;   // stays on calibration only; the reason is in Problems
            }

            bool unchanged = string.Equals(current, target, StringComparison.OrdinalIgnoreCase) && !force;
            if (!quiet || !unchanged || hadProblem)
                ColorProfileLog.Write($"APPLY {d.Name} trigger={trigger} W={white:0.#} lift={liftPercent}% corrected={corrected} current='{current}' desired='{target}'{(force ? " (forced)" : "")}");
            if (ok && hadProblem) _loggedProblems.Remove(d.DevicePath);
            bool switched = unchanged || Switch(d, record, target, current, force);
            if (!switched)
            {
                Problem(d, $"Windows did not switch to '{target}'.");
                if (!string.Equals(target, record.OriginalDefault, StringComparison.OrdinalIgnoreCase)) Switch(d, record, record.OriginalDefault, null, false);
                return false;
            }
            if (IsOurs(target)) RemoveSuperseded(d, record, keep: target);
            return ok;
        }

        private static bool Switch(HdrController.DisplayTarget d, DisplayRecord record, string target, string? current, bool force)
        {
            // Forcing the same name again may do nothing: go through the calibration profile first
            if (force && string.Equals(current, target, StringComparison.OrdinalIgnoreCase) && record.OriginalDefault != null &&
                !string.Equals(target, record.OriginalDefault, StringComparison.OrdinalIgnoreCase))
                ColorProfileNative.SetDefault(record.Scope, record.OriginalDefault, d.AdapterId, d.SourceId);

            var timer = System.Diagnostics.Stopwatch.StartNew();
            int hr = ColorProfileNative.SetDefault(record.Scope, target, d.AdapterId, d.SourceId);
            string? now = ColorProfileNative.GetDefault(record.Scope, d.AdapterId, d.SourceId, ColorProfileNative.SubtypeHdr, out _);
            bool ok = hr == 0 && string.Equals(now, target, StringComparison.OrdinalIgnoreCase);
            ColorProfileLog.Write($"SWITCH {d.Name} -> '{target}' in {timer.ElapsedMilliseconds} ms, read back '{now}', {(ok ? "OK" : "FAILED")}");
            return ok;
        }

        /// <summary>The corrected profile for this display, white level and shadow lift: generated, validated, installed and associated.</summary>
        private string? EnsureCorrected(HdrController.DisplayTarget d, DisplayRecord record, double white, int liftPercent)
        {
            liftPercent = Math.Clamp(liftPercent, 0, 100);
            if (white <= 0) { Problem(d, "Windows did not report the SDR brightness."); return null; }
            string? colorDir = ColorDir;
            if (colorDir == null) { Problem(d, "Windows did not report its colour folder."); return null; }

            byte[] calibration;
            try { calibration = File.ReadAllBytes(Path.Combine(colorDir, record.OriginalDefault!)); }
            catch (Exception ex) { Problem(d, $"The calibration profile could not be read: {ex.Message}"); return null; }

            long nits = (long)Math.Round(white);
            // Without a lift the name is the same as before 3.0.1, so existing profiles are reused
            string lift = liftPercent > 0 ? $"_L{liftPercent}" : string.Empty;
            string name = $"SHM_{Short(d.DevicePath)}_{Short(record.OriginalDefault!)}{Convert.ToHexString(SHA256.HashData(calibration))[..4]}_W{nits}_g22{lift}.icm";
            if (record.Generated.Contains(name, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(colorDir, name)))
                return name;

            byte[] corrected;
            string description = liftPercent > 0 ? $"SHM gamma 2.2 at {nits} nits, shadow lift {liftPercent} %" : $"SHM gamma 2.2 at {nits} nits";
            try { corrected = CorrectedProfile.FromCalibration(calibration, nits, Gamma, description, liftPercent / 100.0); }
            catch (Exception ex) { Problem(d, $"The corrected profile was not created: {ex.Message}"); return null; }   // incl. failed validation

            try
            {
                Directory.CreateDirectory(ProfileFolder);
                File.WriteAllBytes(Path.Combine(ProfileFolder, name), corrected);
            }
            catch (Exception ex) { Problem(d, $"The corrected profile could not be saved: {ex.Message}"); return null; }
            ColorProfileLog.Write($"Generated '{name}': {corrected.Length} bytes, W={nits} nits, shadow lift {liftPercent} %, base '{record.OriginalDefault}'");
            if (!record.Generated.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                record.Generated.Add(name);
                Save();
            }

            int error = ColorProfileNative.Install(Path.Combine(ProfileFolder, name));
            if (error != 0)
            {
                Problem(d, error == 5 ? "Windows needs administrator rights to install the profile (the app never asks for them)."
                                      : $"Installing the profile failed: {new System.ComponentModel.Win32Exception(error).Message}");
                return null;
            }
            int hr = ColorProfileNative.AddAssociation(record.Scope, name, d.AdapterId, d.SourceId);
            if (hr != 0) { Problem(d, $"Associating the profile with the display failed (0x{hr:X8})."); return null; }
            return name;
        }

        /// <summary>Profiles for older white levels or calibrations: only the base and the current corrected one stay listed.</summary>
        private void RemoveSuperseded(HdrController.DisplayTarget d, DisplayRecord record, string keep)
        {
            var superseded = record.Generated.Where(n => IsOurs(n) && !n.Equals(keep, StringComparison.OrdinalIgnoreCase)).ToList();
            if (superseded.Count == 0) return;   // the usual case: nothing to write
            foreach (string old in superseded)
            {
                ColorProfileNative.RemoveAssociation(record.Scope, old, d.AdapterId, d.SourceId);
                ColorProfileNative.Uninstall(old);
                try { File.Delete(Path.Combine(ProfileFolder, old)); } catch { }
                record.Generated.Remove(old);
            }
            Save();
        }

        /// <summary>
        /// Puts the original profile back on every connected display that has a record (exit). The generated files stay,
        /// so the next start does not need to make them again.
        /// </summary>
        public void RestoreOriginals(List<HdrController.DisplayTarget> displays)
        {
            foreach (var record in _state)
            {
                var d = displays.FirstOrDefault(x => x.DevicePath.Equals(record.DevicePath, StringComparison.OrdinalIgnoreCase));
                if (d == null || record.OriginalDefault == null) continue;
                string? current = ColorProfileNative.GetDefault(record.Scope, d.AdapterId, d.SourceId, ColorProfileNative.SubtypeHdr, out _);
                if (IsOurs(current)) Switch(d, record, record.OriginalDefault, current, false);
            }
        }

        /// <summary>
        /// Removes everything this feature created: the original default goes back, the app's associations are removed and
        /// its SHM_* profiles uninstalled and deleted. A display that is not connected keeps its record for later.
        /// </summary>
        public List<string> RemoveAll(List<HdrController.DisplayTarget> displays)
        {
            var report = new List<string>();
            if (_state.Count == 0) report.Add("Nothing to remove.");
            foreach (var record in _state.ToList())
            {
                var d = displays.FirstOrDefault(x => x.DevicePath.Equals(record.DevicePath, StringComparison.OrdinalIgnoreCase));
                if (d == null) { report.Add($"{record.DevicePath}: not connected, kept for later."); continue; }

                bool ok = true;
                string? current = ColorProfileNative.GetDefault(record.Scope, d.AdapterId, d.SourceId, ColorProfileNative.SubtypeHdr, out _);
                if (record.OriginalDefault != null && IsOurs(current))
                {
                    ok = Switch(d, record, record.OriginalDefault, current, false);
                    report.Add($"{d.Name}: {(ok ? "restored" : "could NOT restore")} '{record.OriginalDefault}'.");
                }
                else
                {
                    report.Add($"{d.Name}: HDR slot is '{current ?? "(none)"}', nothing to put back.");
                }
                if (!ok) continue;   // keep the record so that it can be tried again

                foreach (string name in record.Generated.Where(IsOurs))
                {
                    ColorProfileNative.RemoveAssociation(record.Scope, name, d.AdapterId, d.SourceId);
                    ColorProfileNative.Uninstall(name);
                    try { File.Delete(Path.Combine(ProfileFolder, name)); } catch { }
                }
                _state.Remove(record);
                _problems.Remove(record.DevicePath);
            }
            Save();
            ColorProfileLog.Write("Remove all: " + string.Join(" | ", report));
            return report;
        }
    }
}
