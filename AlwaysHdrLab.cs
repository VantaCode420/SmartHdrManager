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
        // Apply / toggle / restore, through the same code the Always-HDR mode uses (AlwaysHdrProfiles). The original
        // state is saved before anything changes, and restore works from that file alone, also after a crash.
        // ------------------------------------------------------------------
        private static List<HdrController.DisplayTarget> HdrDisplays(List<string> report)
        {
            if (!ColorProfileNative.IsAvailable(out string missing))
            {
                report.Add($"This version of Windows does not have the colour-profile functions ({missing}).");
                return new List<HdrController.DisplayTarget>();
            }
            var displays = HdrController.ListDisplayTargets().Where(d => d.HdrSupported).ToList();
            foreach (var d in displays.Where(d => !d.HdrEnabled)) report.Add($"{d.Name}: HDR is off, skipped. Turn HDR on first.");
            return displays.Where(d => d.HdrEnabled).ToList();
        }

        private static bool ApplyAll(AlwaysHdrProfiles profiles, List<HdrController.DisplayTarget> displays, bool corrected, List<string> report)
        {
            bool all = true;
            foreach (var d in displays)
            {
                bool ok = profiles.Apply(d, corrected, d.SdrWhiteNits ?? 0, force: false, trigger: "lab");
                if (profiles.Problems.TryGetValue(d.DevicePath, out string? problem)) report.Add($"{d.Name}: {problem}");
                all &= ok;
            }
            return all;
        }

        private static int Apply()
        {
            var report = new List<string>();
            var profiles = new AlwaysHdrProfiles();
            var displays = HdrDisplays(report);
            bool ok = displays.Count > 0 && ApplyAll(profiles, displays, corrected: true, report);
            if (ok) report.AddRange(displays.Select(d => $"{d.Name}: corrected profile active at {d.SdrWhiteNits:0} nits."));
            Show($"{string.Join("\n", report)}\n\nUse --hdr-lab restore to go back.", ok);
            return ok ? 0 : 1;
        }

        private static int RestoreAll(bool showResult)
        {
            var profiles = new AlwaysHdrProfiles();
            var report = profiles.RemoveAll(HdrController.ListDisplayTargets());
            if (showResult) Show(string.Join("\n", report), !profiles.HasSavedState);
            return profiles.HasSavedState ? 1 : 0;
        }

        private static void Show(string text, bool ok) =>
            MessageBox.Show(text, "Smart HDR Manager – colour test", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        private static int Toggle(int seconds)
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var report = new List<string>();
            var displays = HdrDisplays(report);
            if (displays.Count == 0)
            {
                Show(string.Join("\n", report) + "\n\nNothing was changed.", false);
                return 1;
            }

            using (var form = new ToggleForm(new AlwaysHdrProfiles(), displays, seconds))
                Application.Run(form);
            return RestoreAll(showResult: true);
        }

        /// <summary>Near-black steps and the current state, so a switch can be seen and matched to the log.</summary>
        private sealed class ToggleForm : Form
        {
            private static readonly int[] Levels = { 0, 2, 4, 6, 8, 10, 13, 15, 18, 20, 23, 26, 38, 51, 77, 128 };
            private readonly AlwaysHdrProfiles _profiles;
            private readonly List<HdrController.DisplayTarget> _displays;
            private readonly System.Windows.Forms.Timer _timer = new();
            private bool _corrected;
            private int _switches;
            private string _state = "Starting …";

            public ToggleForm(AlwaysHdrProfiles profiles, List<HdrController.DisplayTarget> displays, int seconds)
            {
                _profiles = profiles;
                _displays = displays;
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
                var report = new List<string>();
                bool all = ApplyAll(_profiles, _displays, _corrected, report);
                _switches++;
                _state = (_corrected ? "CORRECTED  (gamma 2.2)" : "CALIBRATION ONLY") + (all ? string.Empty : "  –  A SWITCH FAILED, see the log");
                ColorProfileLog.Write($"TOGGLE #{_switches}: {_state} {string.Join(" | ", report)}");
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
