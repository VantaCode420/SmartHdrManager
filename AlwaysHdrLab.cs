using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// Hidden test commands for Always-HDR, run as a separate small process: SmartHdrManager.exe --hdr-lab dump.
    /// They answer whether the colour-profile mechanism works on a given PC before the mode is built into the engine.
    /// </summary>
    internal static class AlwaysHdrLab
    {
        public static readonly string DumpPath = Path.Combine(ColorProfileLog.Folder, "always-hdr-diagnostics.txt");

        public static int Run(string[] args, int index)
        {
            string action = index + 1 < args.Length ? args[index + 1].ToLowerInvariant() : "dump";
            ColorProfileLog.Write($"=== --hdr-lab {action} (OS build {Environment.OSVersion.Version.Build}) ===");
            switch (action)
            {
                case "dump": return WriteDump();
                case "apply": return Apply();
                case "restore": return RestoreAll(showResult: true);
                case "toggle":
                    int seconds = index + 2 < args.Length && int.TryParse(args[index + 2], out int s) ? Math.Clamp(s, 1, 60) : 4;
                    return Toggle(seconds);
                default:
                    ColorProfileLog.Write("Unknown action: " + action);
                    return 2;
            }
        }

        private static int WriteDump()
        {
            var o = new StringBuilder();
            o.AppendLine($"Smart HDR Manager colour diagnostics – {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            o.AppendLine($"Windows build: {Environment.OSVersion.Version}");
            bool available = ColorProfileNative.IsAvailable(out string missing);
            o.AppendLine(available ? "Colour-profile functions: all available" : $"Colour-profile functions: NOT available ({missing})");
            string? colorDir = ColorProfileNative.GetColorDirectory();
            o.AppendLine($"Colour directory: {colorDir}");

            foreach (var d in HdrController.ListDisplayTargets())
            {
                o.AppendLine();
                o.AppendLine($"==== {d.Name} ====");
                o.AppendLine($"Device path:  {d.DevicePath}");
                o.AppendLine($"Adapter LUID: {d.AdapterId.HighPart:X8}:{d.AdapterId.LowPart:X8}  source {d.SourceId}  target {d.TargetId}  primary {d.IsPrimary}");
                o.AppendLine($"HDR supported {d.HdrSupported}, HDR on {d.HdrEnabled}, SDR white level {(d.SdrWhiteNits?.ToString("0.0") ?? "unknown")} nits");
                if (!available) continue;

                int hr = ColorProfileNative.GetUserScope(d.AdapterId, d.SourceId, out int scope);
                o.AppendLine($"Profile scope: {(hr == 0 ? (scope == ColorProfileNative.ScopeCurrentUser ? "current user" : "system wide") : $"unknown (0x{hr:X8})")}");

                foreach (int s in new[] { ColorProfileNative.ScopeCurrentUser, ColorProfileNative.ScopeSystemWide })
                {
                    string label = s == ColorProfileNative.ScopeCurrentUser ? "user" : "system";
                    string? sdr = ColorProfileNative.GetDefault(s, d.AdapterId, d.SourceId, ColorProfileNative.SubtypeSdr, out int sdrHr);
                    string? hdr = ColorProfileNative.GetDefault(s, d.AdapterId, d.SourceId, ColorProfileNative.SubtypeHdr, out int hdrHr);
                    var list = ColorProfileNative.GetList(s, d.AdapterId, d.SourceId, out int listHr);
                    o.AppendLine($"[{label}] SDR slot (7) default: {sdr ?? "(none)"}  [0x{sdrHr:X8}]");
                    o.AppendLine($"[{label}] HDR slot (8) default: {hdr ?? "(none)"}  [0x{hdrHr:X8}]");
                    o.AppendLine($"[{label}] associated [0x{listHr:X8}]: {(list.Count == 0 ? "(none)" : string.Join(", ", list))}");
                }

                string? active = ColorProfileNative.GetDefault(scope, d.AdapterId, d.SourceId, ColorProfileNative.SubtypeHdr, out _);
                if (active != null && colorDir != null) DumpProfile(o, Path.Combine(colorDir, active));
            }

            File.WriteAllText(DumpPath, o.ToString());
            ColorProfileLog.Write("Diagnostics written to " + DumpPath);
            return 0;
        }

        private static void DumpProfile(StringBuilder o, string path)
        {
            o.AppendLine($"--- HDR-slot profile: {path}");
            try
            {
                byte[] file = File.ReadAllBytes(path);
                o.AppendLine($"Size {file.Length} bytes, SHA-256 {Convert.ToHexString(SHA256.HashData(file))}");
                var icc = IccProfile.Parse(file);
                o.AppendLine($"ICC version {file[8]}.{file[9] >> 4}, class '{Encoding.ASCII.GetString(file, 12, 4)}', space '{Encoding.ASCII.GetString(file, 16, 4)}', PCS '{Encoding.ASCII.GetString(file, 20, 4)}'");
                o.AppendLine("Tags: " + string.Join(", ", icc.Tags.Select(t => $"{t.Signature}@{t.OriginalOffset}+{t.Data.Length}")));

                foreach (string sig in new[] { "lumi", "wtpt", "rXYZ", "gXYZ", "bXYZ" })
                {
                    var tag = icc.Find(sig);
                    if (tag == null || tag.Data.Length < 20) { o.AppendLine($"{sig}: (missing)"); continue; }
                    o.AppendLine($"{sig}: {S15(tag.Data, 8):0.####} {S15(tag.Data, 12):0.####} {S15(tag.Data, 16):0.####}");
                }

                var mhc = icc.Find("MHC2");
                if (mhc == null) { o.AppendLine("MHC2: (missing)"); return; }
                o.AppendLine($"MHC2 first bytes: {Convert.ToHexString(mhc.Data.Take(64).ToArray())}");
                o.AppendLine($"MHC2 fields: entries {U32(mhc.Data, 8)}, min {S15(mhc.Data, 12):0.######}, max {S15(mhc.Data, 16):0.######}, offsets matrix {U32(mhc.Data, 20)}, R {U32(mhc.Data, 24)}, G {U32(mhc.Data, 28)}, B {U32(mhc.Data, 32)}");
                int matrixAt = (int)U32(mhc.Data, 20);
                o.AppendLine($"Bytes at matrix offset: {Convert.ToHexString(mhc.Data.Skip(matrixAt).Take(16).ToArray())}");
                int lutAt = (int)U32(mhc.Data, 24);
                o.AppendLine($"Bytes at R LUT offset:  {Convert.ToHexString(mhc.Data.Skip(lutAt).Take(16).ToArray())}");

                var parsed = Mhc2Tag.Parse(mhc.Data);
                for (int r = 0; r < 3; r++)
                    o.AppendLine($"Matrix row {r}: {string.Join("  ", parsed.Matrix.Skip(r * 4).Take(4).Select(v => v.ToString("0.######")))}");
                o.AppendLine($"LUT entries {parsed.EntryCount}, identity: {parsed.IsIdentityLut()}");
                for (int c = 0; c < 3; c++)
                {
                    var lut = parsed.Luts[c];
                    int n = lut.Length;
                    var at = new[] { 0, 1, 2, n / 16, n / 8, n / 4, n / 2, 3 * n / 4, n - 2, n - 1 }.Where(i => i >= 0 && i < n).Distinct();
                    o.AppendLine($"LUT {"RGB"[c]}: " + string.Join("  ", at.Select(i => $"[{i}]={lut[i]:0.######}")));
                }
                // Safety net for the writer: an unchanged profile must round-trip to the same tags
                var again = IccProfile.Parse(icc.Serialize());
                bool same = again.Tags.Count == icc.Tags.Count && again.Tags.Zip(icc.Tags).All(p => p.First.Signature == p.Second.Signature && p.First.Data.AsSpan().SequenceEqual(p.Second.Data));
                o.AppendLine($"Round-trip through reader and writer: {(same ? "identical tags" : "DIFFERENT")}");
            }
            catch (Exception ex)
            {
                o.AppendLine("Could not parse it: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Apply / toggle / restore. The original state is saved before anything changes, and restore works from that
        // file alone, so it also works after a crash.
        // ------------------------------------------------------------------
        private const double Gamma = 2.2;
        private static readonly string StatePath = Path.Combine(ColorProfileLog.Folder, "always-hdr-state.json");
        private static readonly string ProfileFolder = Path.Combine(ColorProfileLog.Folder, "profiles");

        private sealed class DisplayRecord
        {
            public string DevicePath { get; set; } = string.Empty;
            public int Scope { get; set; }
            public string? OriginalDefault { get; set; }
            public List<string> Generated { get; set; } = new();
        }

        private sealed class Prepared
        {
            public HdrController.DisplayTarget Display = null!;
            public DisplayRecord Record = null!;
            public string Corrected = string.Empty;
        }

        private static List<DisplayRecord> LoadState()
        {
            if (!File.Exists(StatePath)) return new List<DisplayRecord>();
            return JsonSerializer.Deserialize<List<DisplayRecord>>(File.ReadAllText(StatePath)) ?? new List<DisplayRecord>();
        }

        private static void SaveState(List<DisplayRecord> state)
        {
            Directory.CreateDirectory(ColorProfileLog.Folder);
            if (state.Count == 0) { File.Delete(StatePath); return; }
            string temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, StatePath, overwrite: true);
        }

        private static string Short(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];

        /// <summary>Generates, installs and associates the corrected profile of every display that has HDR on.</summary>
        private static List<Prepared> PrepareAll(List<DisplayRecord> state, List<string> report)
        {
            var prepared = new List<Prepared>();
            if (!ColorProfileNative.IsAvailable(out string missing))
            {
                report.Add($"This version of Windows does not have the colour-profile functions ({missing}).");
                return prepared;
            }
            string? colorDir = ColorProfileNative.GetColorDirectory();
            if (colorDir == null) { report.Add("Windows did not report its colour folder."); return prepared; }

            foreach (var d in HdrController.ListDisplayTargets())
            {
                try
                {
                    if (!d.HdrEnabled) { report.Add($"{d.Name}: HDR is off, skipped. Turn HDR on first."); continue; }
                    double white = d.SdrWhiteNits ?? 0;
                    if (white <= 0) { report.Add($"{d.Name}: SDR white level unknown, skipped."); continue; }

                    int hr = ColorProfileNative.GetUserScope(d.AdapterId, d.SourceId, out int scope);
                    if (hr != 0) { report.Add($"{d.Name}: could not read the profile scope (0x{hr:X8})."); continue; }

                    var record = state.FirstOrDefault(r => r.DevicePath.Equals(d.DevicePath, StringComparison.OrdinalIgnoreCase));
                    if (record == null)
                    {
                        string? current = ColorProfileNative.GetDefault(scope, d.AdapterId, d.SourceId, ColorProfileNative.SubtypeHdr, out _);
                        if (current == null) { report.Add($"{d.Name}: no calibration profile in the HDR slot; this test needs one."); continue; }
                        if (current.StartsWith("SHM_", StringComparison.OrdinalIgnoreCase)) { report.Add($"{d.Name}: the default is already '{current}' but no saved state exists; not touching it."); continue; }
                        record = new DisplayRecord { DevicePath = d.DevicePath, Scope = scope, OriginalDefault = current };
                        state.Add(record);
                        SaveState(state);   // before anything changes
                        ColorProfileLog.Write($"Saved original state: {d.DevicePath} scope={scope} default='{current}'");
                    }

                    byte[] calibration = File.ReadAllBytes(Path.Combine(colorDir, record.OriginalDefault!));
                    string name = $"SHM_{Short(d.DevicePath)}_{Short(record.OriginalDefault!)}{Convert.ToHexString(SHA256.HashData(calibration))[..4]}_W{Math.Round(white)}_g22.icm";
                    byte[] corrected = CorrectedProfile.FromCalibration(calibration, white, Gamma, $"SHM gamma 2.2 at {Math.Round(white)} nits");
                    Directory.CreateDirectory(ProfileFolder);
                    string path = Path.Combine(ProfileFolder, name);
                    File.WriteAllBytes(path, corrected);
                    ColorProfileLog.Write($"Generated '{name}': {corrected.Length} bytes, W={white:0.0} nits, base '{record.OriginalDefault}'");

                    if (!record.Generated.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        record.Generated.Add(name);
                        SaveState(state);
                    }

                    int error = ColorProfileNative.Install(path);
                    if (error != 0) { report.Add($"{d.Name}: installing the profile failed: {new System.ComponentModel.Win32Exception(error).Message} ({error})."); continue; }
                    hr = ColorProfileNative.AddAssociation(record.Scope, name, d.AdapterId, d.SourceId);
                    if (hr != 0) { report.Add($"{d.Name}: associating the profile failed (0x{hr:X8})."); continue; }
                    prepared.Add(new Prepared { Display = d, Record = record, Corrected = name });
                }
                catch (Exception ex)
                {
                    ColorProfileLog.Write($"{d.Name}: preparing failed: {ex}");
                    report.Add($"{d.Name}: {ex.Message}");
                }
            }
            return prepared;
        }

        /// <summary>Sets the HDR-slot default and reads it back. Reading it back does not prove the screen changed.</summary>
        private static bool Switch(Prepared p, string profile, List<string>? report)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int hr = ColorProfileNative.SetDefault(p.Record.Scope, profile, p.Display.AdapterId, p.Display.SourceId);
            string? now = ColorProfileNative.GetDefault(p.Record.Scope, p.Display.AdapterId, p.Display.SourceId, ColorProfileNative.SubtypeHdr, out _);
            bool ok = hr == 0 && string.Equals(now, profile, StringComparison.OrdinalIgnoreCase);
            ColorProfileLog.Write($"SWITCH {p.Display.Name} -> '{profile}' in {timer.ElapsedMilliseconds} ms, read back '{now}', {(ok ? "OK" : "FAILED")}");
            if (!ok) report?.Add($"{p.Display.Name}: switching to '{profile}' failed (0x{hr:X8}, read back '{now}').");
            return ok;
        }

        private static int Apply()
        {
            var state = LoadState();
            var report = new List<string>();
            int applied = 0;
            foreach (var p in PrepareAll(state, report))
            {
                if (Switch(p, p.Corrected, report))
                {
                    applied++;
                    report.Add($"{p.Display.Name}: corrected profile active ({p.Corrected}).");
                }
                else if (p.Record.OriginalDefault != null)
                {
                    Switch(p, p.Record.OriginalDefault, report);
                }
            }
            Show($"{string.Join("\n", report)}\n\nUse --hdr-lab restore to go back.", applied > 0);
            return applied > 0 ? 0 : 1;
        }

        private static int RestoreAll(bool showResult)
        {
            var state = LoadState();
            var report = new List<string>();
            if (state.Count == 0) report.Add("Nothing to restore: no saved state.");
            var displays = HdrController.ListDisplayTargets();
            foreach (var record in state.ToList())
            {
                var d = displays.FirstOrDefault(x => x.DevicePath.Equals(record.DevicePath, StringComparison.OrdinalIgnoreCase));
                if (d == null) { report.Add($"{record.DevicePath}: not connected, kept for later."); continue; }

                bool ok = true;
                if (record.OriginalDefault != null)
                {
                    ok = ColorProfileNative.SetDefault(record.Scope, record.OriginalDefault, d.AdapterId, d.SourceId) == 0;
                    string? now = ColorProfileNative.GetDefault(record.Scope, d.AdapterId, d.SourceId, ColorProfileNative.SubtypeHdr, out _);
                    ok &= string.Equals(now, record.OriginalDefault, StringComparison.OrdinalIgnoreCase);
                    report.Add($"{d.Name}: {(ok ? "restored" : "could NOT restore")} '{record.OriginalDefault}' (now '{now}').");
                }
                if (!ok) continue;   // keep the record so that restore can be tried again

                foreach (string name in record.Generated.Where(n => n.StartsWith("SHM_", StringComparison.OrdinalIgnoreCase)))
                {
                    ColorProfileNative.RemoveAssociation(record.Scope, name, d.AdapterId, d.SourceId);
                    ColorProfileNative.Uninstall(name);
                    try { File.Delete(Path.Combine(ProfileFolder, name)); } catch { }
                }
                state.Remove(record);
            }
            SaveState(state);
            ColorProfileLog.Write("Restore: " + string.Join(" | ", report));
            if (showResult) Show(string.Join("\n", report), true);
            return state.Count == 0 ? 0 : 1;
        }

        private static void Show(string text, bool ok) =>
            MessageBox.Show(text, "Smart HDR Manager – colour test", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        private static int Toggle(int seconds)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var state = LoadState();
            var report = new List<string>();
            var prepared = PrepareAll(state, report);
            if (prepared.Count == 0)
            {
                Show(string.Join("\n", report) + "\n\nNothing was changed.", false);
                RestoreAll(showResult: false);
                return 1;
            }

            using var form = new ToggleForm(prepared, seconds, report);
            Application.Run(form);
            return RestoreAll(showResult: true);
        }

        /// <summary>Near-black steps and the current state, so a switch can be seen and matched to the log.</summary>
        private sealed class ToggleForm : Form
        {
            private static readonly int[] Levels = { 0, 2, 4, 6, 8, 10, 13, 15, 18, 20, 23, 26, 38, 51, 77, 128 };
            private readonly List<Prepared> _displays;
            private readonly List<string> _report;
            private readonly System.Windows.Forms.Timer _timer = new();
            private bool _corrected;
            private int _switches;
            private string _state = "Starting …";

            public ToggleForm(List<Prepared> displays, int seconds, List<string> report)
            {
                _displays = displays;
                _report = report;
                Text = "Always-HDR colour test – close this window to stop and restore";
                BackColor = Color.Black;
                ForeColor = Color.White;
                DoubleBuffered = true;
                WindowState = FormWindowState.Maximized;
                _timer.Interval = seconds * 1000;
                _timer.Tick += (_, _) => Flip();
                Shown += (_, _) => { Flip(); _timer.Start(); };
                FormClosing += (_, _) => _timer.Stop();
                Resize += (_, _) => Invalidate();
            }

            private void Flip()
            {
                _corrected = !_corrected;
                bool all = true;
                foreach (var p in _displays) all &= Switch(p, _corrected ? p.Corrected : p.Record.OriginalDefault!, _report);
                _switches++;
                _state = (_corrected ? "CORRECTED  (gamma 2.2)" : "CALIBRATION ONLY") + (all ? string.Empty : "  –  A SWITCH FAILED, see the log");
                ColorProfileLog.Write($"TOGGLE #{_switches}: {_state}");
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                int w = ClientSize.Width, h = ClientSize.Height;
                using var big = new Font("Segoe UI", 34, FontStyle.Bold);
                using var small = new Font("Segoe UI", 12);
                e.Graphics.DrawString(_state, big, Brushes.White, 40, 30);
                e.Graphics.DrawString($"Switch {_switches} at {DateTime.Now:HH:mm:ss}.  In CORRECTED the dark steps below should get darker, with no flicker or black screen.  Close this window to stop and restore.", small, Brushes.Gray, 44, 110);

                int count = Levels.Length, gap = 8, top = 160;
                int cell = (w - 80 - gap * (count - 1)) / count, height = h - top - 70;
                for (int i = 0; i < count; i++)
                {
                    int level = Levels[i];
                    using var brush = new SolidBrush(Color.FromArgb(level, level, level));
                    int x = 40 + i * (cell + gap);
                    e.Graphics.FillRectangle(brush, x, top, cell, height);
                    e.Graphics.DrawString(level.ToString(), small, Brushes.Gray, x, top + height + 8);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _timer.Dispose();
                base.Dispose(disposing);
            }
        }

        private static uint U32(byte[] d, int at) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at));
        private static double S15(byte[] d, int at) => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(at)) / 65536.0;
    }
}
