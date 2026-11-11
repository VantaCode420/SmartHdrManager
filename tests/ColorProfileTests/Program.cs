using System;
using System.IO;
using System.Linq;
using SmartHdrManager;

internal static class Tests
{
    private static int _failed, _passed;

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        if (ok) _passed++; else _failed++;
    }

    private static void Near(double actual, double expected, double tolerance, string name) =>
        Check(Math.Abs(actual - expected) <= tolerance, $"{name} (got {actual:R}, expected {expected:R})");

    private static double[] Identity = { 0, 1 };

    private static int Main()
    {
        TransferFunctionTests();
        CurveTests();
        ShadowLiftTests();
        CalibrationMergeTests();
        ValidatorTests();
        FileFormatTests();
        RealProfileTest();
        RenoDxTests();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // GitHub issue #9: only RenoDX HDR mods count, not its tools; ReShade.ini decides what is loaded
    private static void RenoDxTests()
    {
        foreach (string tool in new[] { "devkit", "dlssfix", "fpslimiter", "dlss", "dlss5", "DLSS5", "mfgunlock", "universal_ue-dof-fix",
                                        "dlss-fg", "framegen", "some_fov", "fps-limiter", "dofremover-dof", "ue-mfg", "unlockfps" })
            Check(RenoDxAddons.IsTool(tool), $"RenoDX tool: {tool}");
        foreach (string mod in new[] { "cp2077", "dyinglight", "horizonzdr-fwce", "unrealengine", "ue-extended", "unityengine", "sourceengine",
                                       "fromsoft_engine", "duniaengine", "_ksp", "_hbr", "_p5s", "_univ", "re4", "dmc5", "ff7rebirth" })
            Check(!RenoDxAddons.IsTool(mod), $"RenoDX HDR mod: {mod}");

        Check(RenoDxAddons.IsAddonFile("renodx-cp2077.addon64") && RenoDxAddons.IsAddonFile("RenoDX-CP2077.ADDON32") &&
              RenoDxAddons.IsAddonFile("renodx-x.addon"), "add-on extensions ReShade loads");
        Check(!RenoDxAddons.IsAddonFile("renodx-cp2077.addon64.disabled") && !RenoDxAddons.IsAddonFile("renodx-cp2077.zip") &&
              !RenoDxAddons.IsAddonFile("RenoDX-DLSS5-crash.log") && !RenoDxAddons.IsAddonFile("other-cp2077.addon64"),
              "other files are no add-ons");
        Check(RenoDxAddons.NameOf(@"C:\g\renodx-cp2077.addon64") == "cp2077" && RenoDxAddons.NameOf("renodx-_ksp.addon64") == "_ksp" &&
              RenoDxAddons.NameOf("RenoDX-universal_ue-dof-fix.addon64") == "universal_ue-dof-fix", "add-on names");

        // The format seen in real ReShade.ini files (comma list, "Name@file" entries)
        var s = RenoDxAddons.ParseReShadeIni(
            "[GENERAL]\r\nAddonPath=wrong\r\n[ADDON]\r\nDisabledAddons=Generic Depth,Effect Runtime Sync,DLSS 5 Neural Rendering@renodx-dlss5.addon64, @renodx-old.addon64\r\n" +
            "AddonPath=..\\addons\r\nOverlayCollapsed=X@renodx-cp2077.addon64\r\n[OVERLAY]\r\nAddonPath=also wrong\r\n");
        Check(s.AddonPath == "..\\addons", "AddonPath read from [ADDON] only");
        Check(s.DisabledFiles.Count == 2 && s.DisabledFiles.Contains("renodx-dlss5.addon64") && s.DisabledFiles.Contains("RENODX-OLD.addon64"),
              "DisabledAddons: file entries only, case-insensitive");
        var empty = RenoDxAddons.ParseReShadeIni("[addon]\nDisabledAddons=\n; AddonPath=C:\\x\n");
        Check(empty.AddonPath == null && empty.DisabledFiles.Count == 0, "empty settings and comments");
        Check(RenoDxAddons.ParseReShadeIni("[ADDON]\nAddonPath=\"D:\\ReShade Addons\"\n").AddonPath == "D:\\ReShade Addons", "quoted absolute AddonPath");
    }

    private static void TransferFunctionTests()
    {
        Near(TransferFunctions.NitsToPq(10000), 1.0, 1e-12, "PQ of 10,000 nits");
        Near(TransferFunctions.NitsToPq(1000), 0.751827, 1e-5, "PQ of 1,000 nits");
        Near(TransferFunctions.NitsToPq(100), 0.508078, 1e-5, "PQ of 100 nits");
        Check(Enumerable.Range(1, 2000).All(i =>
        {
            double nits = Math.Pow(10, i / 2000.0 * 8 - 4);
            return Math.Abs(TransferFunctions.PqToNits(TransferFunctions.NitsToPq(nits)) - nits) <= nits * 1e-8;
        }), "PQ round trip from 0.0001 to 10,000 nits");
        Check(Enumerable.Range(0, 1001).All(i =>
        {
            double v = i / 1000.0;
            return Math.Abs(TransferFunctions.SrgbEncode(TransferFunctions.SrgbDecode(v)) - v) < 1e-9;
        }), "sRGB round trip");
    }

    private static void CurveTests()
    {
        // Worked out separately from the formulas, in PowerShell, not with this code
        (double W, double L, double Nits, double Pq)[] known =
        {
            (200, 100, 101.70120101297218, 0.50977240750472352),
            (200, 10, 9.2908381794877837, 0.29393039220218037),
            (200, 1, 0.42549002919169565, 0.11069587215049141),
            (200, 0.5, 0.10502085382265582, 0.063638218383915882),
            (240, 60, 61.140687601790383, 0.45957214459461004),
            (80, 40, 40.680480405188874, 0.42085615423077094),
        };
        foreach (var k in known)
        {
            Near(GammaCorrection.CorrectNits(k.L, k.W, 2.2), k.Nits, 1e-9, $"curve W={k.W} L={k.L}");
            Near(GammaCorrection.CorrectPq(TransferFunctions.NitsToPq(k.L), k.W, 2.2), k.Pq, 1e-9, $"curve in PQ W={k.W} L={k.L}");
        }
        Near(GammaCorrection.CorrectNits(200, 200, 2.2), 200, 1e-12, "exactly SDR white is unchanged");
        Check(new[] { 200.5, 201, 300, 1000, 4000, 10000 }.All(n => GammaCorrection.CorrectNits(n, 200, 2.2) == n), "above SDR white is unchanged");
        Near(GammaCorrection.CorrectNits(199.999999, 200, 2.2), 200, 1e-4, "no jump just below SDR white");
        Near(GammaCorrection.CorrectNits(0, 200, 2.2), 0, 0, "black stays black");
    }

    // Settings › Always-HDR › Shadow lift: blends the corrected shadows back towards Windows' sRGB decode
    private static void ShadowLiftTests()
    {
        Check(GammaCorrection.BuildLut(Identity, 200, 2.2, 4096, 0).SequenceEqual(GammaCorrection.BuildLut(Identity, 200, 2.2, 4096)),
              "lift 0 is exactly the plain correction (profiles from 3.0.0 stay valid)");
        Check(Enumerable.Range(0, 2001).All(i =>
        {
            double nits = 200 * i / 2000.0;
            return Math.Abs(GammaCorrection.CorrectNits(nits, 200, 2.2, 1) - nits) <= 1e-9;
        }), "lift 100 % is no correction at all (Windows' sRGB decode)");
        // From the known lift-0 value (worked out separately): 0.4255 + 0.5 x (1 - 0.4255)
        Near(GammaCorrection.CorrectNits(1, 200, 2.2, 0.5), 0.71274501459584783, 1e-9, "lift 50 % at W=200, 1 nit");
        Check(new[] { 200.0, 300, 1000 }.All(n => GammaCorrection.CorrectNits(n, 200, 2.2, 0.4) == n), "lift: SDR white and above unchanged");
        Near(GammaCorrection.CorrectNits(0, 200, 2.2, 0.7), 0, 0, "lift: black stays black");

        bool rising = true, lifted = true, midtones = true;
        foreach (double white in new[] { 80.0, 200, 480 })
            for (int l = 0; l <= 20; l++)
            {
                double lift = l / 20.0;
                double last = -1;
                for (int i = 0; i <= 4000; i++)
                {
                    double nits = white * i / 4000.0;
                    double v = GammaCorrection.CorrectNits(nits, white, 2.2, lift);
                    if (v <= last && i > 0) rising = false;
                    last = v;
                }
                // Deep shadows (below 1 % of white) get brighter; from 15 % of white up nothing moves more than 2.5 %
                for (int i = 1; i <= 100; i++)
                {
                    double nits = white * 0.01 * i / 100.0;
                    if (lift > 0 && GammaCorrection.CorrectNits(nits, white, 2.2, lift) <= GammaCorrection.CorrectNits(nits, white, 2.2)) lifted = false;
                }
                for (int i = 15; i <= 100; i++)
                {
                    double nits = white * i / 100.0;
                    double ratio = GammaCorrection.CorrectNits(nits, white, 2.2, lift) / GammaCorrection.CorrectNits(nits, white, 2.2);
                    if (Math.Abs(ratio - 1) > 0.025) midtones = false;
                }
            }
        Check(rising, "lift: the curve always rises (every lift 0–100 %, W 80/200/480)");
        Check(lifted, "lift: the deep shadows get brighter");
        Check(midtones, "lift: from 15 % of white up, at most 2.5 % change");

        bool valid = true;
        foreach (double white in new[] { 80.0, 200, 480 })
            for (int l = 5; l <= 100; l += 5)
            {
                try { CorrectedProfile.FromIdentity(0.0005, 1000, white, 2.2, "SHM test", l / 100.0); }
                catch (Exception ex) { valid = false; Console.WriteLine($"      W={white} lift={l}: {ex.Message}"); }
            }
        Check(valid, "lift: every profile 5–100 % passes validation (W 80/200/480)");
    }

    private static void CalibrationMergeTests()
    {
        double[] calibration = Enumerable.Range(0, 33).Select(i => Math.Pow(i / 32.0, 1.05)).ToArray();
        var lut = GammaCorrection.BuildLut(calibration, 200, 2.2, 4096);
        double x = 1800.0 / 4095;
        double expected = GammaCorrection.Sample(calibration, GammaCorrection.CorrectPq(x, 200, 2.2));
        Near(lut[1800], expected, 1.0 / 65536, "calibration is applied after the correction");
        Near(GammaCorrection.Sample(new[] { 0.0, 0.5, 1.0 }, 0.25), 0.25, 1e-12, "LUT is read with linear interpolation");
    }

    private static double[] Good(out Func<double, double> analytic, double white = 200)
    {
        double w = white;
        analytic = x => GammaCorrection.Analytic(x, Identity, w, 2.2);
        return GammaCorrection.BuildLut(Identity, white, 2.2, 4096);
    }

    private static void ValidatorTests()
    {
        foreach (double white in new[] { 80.0, 120, 200, 240, 400, 480 })
        {
            var lut = Good(out var analytic, white);
            Check(LutValidator.Validate(lut, analytic) == null, $"correct LUT passes at W={white}");
        }

        var near = Good(out var f);
        Check(near[1] == near[0] || near[1] > near[0], "first entries may tie after rounding");

        var plateau = Good(out f);
        for (int i = 2000; i < 2039; i++) plateau[i] = plateau[2000];
        Check(LutValidator.Validate(plateau, f) != null, "rejects a 39-entry plateau: " + LutValidator.Validate(plateau, f));

        var small = Good(out f);
        for (int i = 2000; i < 2005; i++) small[i] = small[2000];
        string? smallResult = LutValidator.Validate(small, f);
        Check(smallResult != null, "rejects a short unexplained plateau: " + smallResult);

        var jump = Good(out f);
        for (int i = 2500; i < 4096; i++) jump[i] = Math.Min(1, jump[i] + 0.01);
        Check(LutValidator.Validate(jump, f) != null, "rejects a jump: " + LutValidator.Validate(jump, f));

        var drop = Good(out f);
        drop[1500] = drop[1499] - 0.0001;
        Check(LutValidator.Validate(drop, f) != null, "rejects a decreasing entry: " + LutValidator.Validate(drop, f));

        var nan = Good(out f);
        nan[9] = double.NaN;
        Check(LutValidator.Validate(nan, f) != null, "rejects NaN");
    }

    private static void FileFormatTests()
    {
        byte[] file = CorrectedProfile.FromIdentity(0.0005, 1000, 200, 2.2, "SHM test");
        var icc = IccProfile.Parse(file);
        var parsed = Mhc2Tag.Parse(icc.Find("MHC2")!.Data);
        Check(parsed.EntryCount == 4096, "writes 4096 entries");
        Near(parsed.MaxNits, 1000, 1e-4, "max luminance survives");
        Near(parsed.MinNits, 0.0005, 1.0 / 65536, "min luminance survives");
        Check(parsed.Matrix.SequenceEqual(new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 }), "identity matrix survives");
        Check(parsed.Luts.All(l => l.SequenceEqual(GammaCorrection.BuildLut(Identity, 200, 2.2, 4096))), "LUT values survive a write and read");
        Check(BitConverter.ToUInt32(file.Take(4).Reverse().ToArray()) == file.Length, "header holds the file size");

        var again = icc.Serialize();
        var hashCopy = (byte[])again.Clone();
        Array.Clear(hashCopy, 44, 4);
        Array.Clear(hashCopy, 64, 4);
        Array.Clear(hashCopy, 84, 16);
        Check(System.Security.Cryptography.MD5.HashData(hashCopy).SequenceEqual(again.Skip(84).Take(16)), "profile ID is the MD5 of the file");

        var tag = Mhc2Tag.Parse(icc.Find("MHC2")!.Data);
        Check(tag.Serialize().SequenceEqual(icc.Find("MHC2")!.Data), "MHC2 write, read, write is identical");
    }

    /// <summary>Against the real Windows calibration profile when this PC has one (never changes it).</summary>
    private static void RealProfileTest()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");
        string? real = Directory.Exists(folder) ? Directory.GetFiles(folder, "HDR *.icc").FirstOrDefault() : null;
        if (real == null)
        {
            Console.WriteLine("SKIP  no Windows HDR Calibration profile on this PC");
            return;
        }

        byte[] original = File.ReadAllBytes(real);
        var before = IccProfile.Parse(original);
        byte[] output = CorrectedProfile.FromCalibration(original, 240, 2.2, "SHM test");
        var after = IccProfile.Parse(output);
        var baseTag = Mhc2Tag.Parse(before.Find("MHC2")!.Data);
        var newTag = Mhc2Tag.Parse(after.Find("MHC2")!.Data);

        Check(before.Tags.Select(t => t.Signature).SequenceEqual(after.Tags.Select(t => t.Signature)), "real profile: same tags in the same order");
        Check(before.Tags.Where(t => t.Signature is not ("MHC2" or "desc")).All(t => t.Data.SequenceEqual(after.Find(t.Signature)!.Data)), "real profile: all other tags unchanged");
        Check(baseTag.Matrix.SequenceEqual(newTag.Matrix), "real profile: matrix unchanged");
        Check(baseTag.MinNits == newTag.MinNits && baseTag.MaxNits == newTag.MaxNits, "real profile: luminance unchanged");
        Check(newTag.EntryCount == 4096, "real profile: corrected LUT has 4096 entries");
        Check(original.SequenceEqual(File.ReadAllBytes(real)), "real profile: file on disk untouched");
        Check(IccProfile.Parse(before.Serialize()).Tags.Zip(before.Tags).All(p => p.First.Data.SequenceEqual(p.Second.Data)), "real profile: unchanged round trip");

        double worst = 0;
        for (int i = 0; i < 4095; i++)
        {
            for (int s = 0; s <= 8; s++)
            {
                double t = s / 8.0;
                double value = newTag.Luts[0][i] + (newTag.Luts[0][i + 1] - newTag.Luts[0][i]) * t;
                worst = Math.Max(worst, Math.Abs(value - GammaCorrection.Analytic((i + t) / 4095, baseTag.Luts[0], 240, 2.2)));
            }
        }
        Console.WriteLine($"INFO  largest interpolation error {worst:0.00000000} (limit {LutValidator.MaxErrorPq:0.00000000})");
        Check(worst < LutValidator.MaxErrorPq / 4, "real profile: far inside the error limit");

        bool liftValid = true;
        foreach (int l in new[] { 5, 25, 50, 100 })
        {
            try
            {
                var lifted = IccProfile.Parse(CorrectedProfile.FromCalibration(original, 240, 2.2, "SHM test", l / 100.0));
                var liftTag = Mhc2Tag.Parse(lifted.Find("MHC2")!.Data);
                if (!liftTag.Matrix.SequenceEqual(baseTag.Matrix) ||
                    !before.Tags.Where(t => t.Signature is not ("MHC2" or "desc")).All(t => t.Data.SequenceEqual(lifted.Find(t.Signature)!.Data)))
                    liftValid = false;
            }
            catch (Exception ex) { liftValid = false; Console.WriteLine($"      lift {l}: {ex.Message}"); }
        }
        Check(liftValid, "real profile: shadow lift 5/25/50/100 % passes validation, other tags unchanged");
        Check(original.SequenceEqual(File.ReadAllBytes(real)), "real profile: still untouched");
    }
}
