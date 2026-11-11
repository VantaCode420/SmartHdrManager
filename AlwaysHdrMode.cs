using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SmartHdrManager
{
    /// <summary>
    /// Always-HDR mode. HDR stays on and the engine's existing decisions are translated: where Classic mode would turn HDR
    /// on, the correction goes off on those displays; where it would turn HDR off, the correction comes back. The engine
    /// reads "HDR on" as "correction off" (<see cref="QueryHdr"/>), so its launcher, delay and hold logic carries over
    /// unchanged. With the mode off none of this runs and every call goes straight to Windows as before.
    /// Engine thread only, apart from <see cref="AlwaysHdrStatus"/>.
    /// </summary>
    public sealed partial class HdrEngine
    {
        private static readonly Lazy<bool> AlwaysHdrSupportedLazy = new(() => ColorProfileNative.IsAvailable(out _));

        /// <summary>This Windows has the colour-profile functions the mode needs (Windows 11, build 20348 and newer).</summary>
        public static bool AlwaysHdrSupported => AlwaysHdrSupportedLazy.Value;

        private const int AlwaysCheckMs = 1000;        // brightness slider and profile check
        private const int WhiteDebounceMs = 750;

        private AlwaysHdrProfiles? _profiles;
        private AlwaysHdrProfiles Profiles => _profiles ??= new AlwaysHdrProfiles();
        private bool _alwaysActive;
        private readonly HashSet<string> _correctionOff = new(StringComparer.OrdinalIgnoreCase);   // device paths
        private bool _alwaysRealSdr;            // this mode turned HDR off for an SDR app, a recording app or the SDR override
        private string? _alwaysSdrFor;
        private bool _alwaysStartPending;       // HDR still has to be turned on for the mode
        private int _alwaysReapply;             // 1 = check now, 2 = set the profiles again (after standby)
        private long _nextAlwaysCheckMs;
        private long _lastRealSwitchMs = long.MinValue / 2;
        private bool _pauseCorrected;
        private bool _alwaysLeftoverChecked;
        private bool _alwaysWasHdrOn;           // HDR was on when the app started (or the mode was switched on)
        private bool _alwaysRealHdrOn;          // Windows HDR is really on (for the window header and the tray icon)
        private readonly Dictionary<string, (double Stable, double Pending, long Since)> _white = new(StringComparer.OrdinalIgnoreCase);
        private int _shadowLift;                // shadow lift in use (%), changed once the setting has stayed the same for a moment
        private int _shadowLiftPending;
        private long _shadowLiftSince;

        /// <summary>Short state for the Settings page (any thread).</summary>
        public string AlwaysHdrStatus { get; private set; } = string.Empty;

        private bool AlwaysHdrRunning => _alwaysActive;

        /// <summary>The engine's view of HDR: in Always-HDR mode "on" means "HDR on and the correction off".</summary>
        private bool QueryHdr(Snapshot s, MonitorFilter target, out bool any, out bool all)
        {
            if (!_alwaysActive) return HdrController.QueryHdrState(target, out any, out all);
            any = all = false;
            var displays = TargetDisplays(target);
            if (displays.Count == 0) return HdrController.QueryHdrState(target, out any, out all);
            int on = displays.Count(d => d.HdrEnabled && _correctionOff.Contains(d.DevicePath));
            any = on > 0;
            all = on == displays.Count;
            return true;
        }

        private static List<HdrController.DisplayTarget> TargetDisplays(MonitorFilter target)
        {
            var keys = HdrController.ResolveTargetKeys(target);
            return HdrController.ListDisplayTargets().Where(d => d.HdrSupported && keys.Contains(d.DevicePath)).ToList();
        }

        /// <summary>What TurnHdrOn / TurnHdrOff do in this mode. HDR is turned on first if it is off.</summary>
        private bool SetCorrection(MonitorFilter monitors, bool correctionOff, string reason)
        {
            var displays = TargetDisplays(monitors);
            foreach (var d in displays)
            {
                if (correctionOff) _correctionOff.Add(d.DevicePath);
                else _correctionOff.Remove(d.DevicePath);
            }
            Log("HDR", $"Always-HDR: correction {(correctionOff ? "OFF" : "ON")} ({monitors.Describe()}) – {reason}");

            if (correctionOff && displays.Any(d => !d.HdrEnabled))
            {
                // HDR content while HDR is off (turned off by you, or after the SDR override): HDR comes on for it
                if (!HdrController.SetHdrState(true, monitors))
                {
                    Log("ERROR", HdrController.LastError);
                    return false;
                }
                _alwaysRealSdr = false;
                _alwaysRealHdrOn = true;
                _lastRealSwitchMs = Now;
                Interlocked.Exchange(ref _alwaysReapply, 1);   // the HDR slot counts once Windows reports HDR on
                return true;
            }
            ApplyProfiles(displays, force: false, trigger: reason);
            return true;
        }

        /// <summary>Puts the right profile on each display and collects the problems for the Settings page.</summary>
        private void ApplyProfiles(List<HdrController.DisplayTarget> displays, bool force, string trigger)
        {
            long now = Now;
            _alwaysRealHdrOn = displays.Any(d => d.HdrEnabled);
            foreach (var d in displays.Where(d => d.HdrEnabled))
            {
                double white = StableWhite(d, now);
                Profiles.Apply(d, corrected: !_correctionOff.Contains(d.DevicePath), white, force, trigger, _shadowLift);
            }
            UpdateAlwaysStatus(displays);
        }

        /// <summary>The SDR white level, changed only once it has stayed the same for a moment (slider being dragged).</summary>
        private double StableWhite(HdrController.DisplayTarget d, long now)
        {
            double current = Math.Round(d.SdrWhiteNits ?? 0);
            if (!_white.TryGetValue(d.DevicePath, out var w))
            {
                _white[d.DevicePath] = (current, current, now);
                return current;
            }
            if (current != w.Pending) w = (w.Stable, current, now);
            else if (w.Pending != w.Stable && now - w.Since >= WhiteDebounceMs)
            {
                ColorProfileLog.Write($"{d.Name}: SDR white level {w.Stable} -> {w.Pending} nits");
                w = (w.Pending, w.Pending, now);
            }
            _white[d.DevicePath] = w;
            return w.Stable;
        }

        private void UpdateAlwaysStatus(List<HdrController.DisplayTarget> displays)
        {
            var problems = Profiles.Problems;
            if (problems.Count > 0)
            {
                AlwaysHdrStatus = "Calibration only: " + string.Join(" ", problems.Values.Distinct());
                return;
            }
            var on = displays.Where(d => d.HdrEnabled).ToList();
            if (_alwaysRealSdr) AlwaysHdrStatus = $"HDR is off while '{_alwaysSdrFor}' needs SDR.";
            else if (on.Count == 0) AlwaysHdrStatus = "HDR is off – turn it on (Win+Alt+B) or start HDR content.";
            else if (on.All(d => _correctionOff.Contains(d.DevicePath))) AlwaysHdrStatus = "HDR content: correction off.";
            else AlwaysHdrStatus = "Correction on (gamma 2.2 at " + string.Join(", ", on.Select(d => $"{StableWhite(d, Now):0} nits")) + ").";
        }

        /// <summary>Every tick: mode start / stop, real SDR for SDR apps, pause, and the profile check every second.</summary>
        private void MaintainAlwaysHdr(Snapshot s, long now, HdrOverride mode)
        {
            if (s.AlwaysHdr != _alwaysActive)
            {
                if (s.AlwaysHdr) StartAlwaysHdr(s);
                else StopAlwaysHdr(s, removeEverything: true, exiting: false);
            }
            if (!_alwaysActive)
            {
                // Mode off but profiles left from a crash (or a mode switched off in config.json): put things back once
                if (!_alwaysLeftoverChecked)
                {
                    _alwaysLeftoverChecked = true;
                    if (System.IO.File.Exists(AlwaysHdrProfiles.StatePath))
                    {
                        Log("HDR", "Always-HDR is off, but its colour profiles are still set – removing them.");
                        Profiles.RemoveAll(HdrController.ListDisplayTargets());
                    }
                }
                return;
            }

            // Apps set to SDR, recording apps set to "No HDR" and the SDR override really turn HDR off, as in Classic mode
            string? sdrFor = mode == HdrOverride.ForceSdr ? "the SDR override"
                : Paused || mode != HdrOverride.None ? null
                : _runningAllBlocker ?? (s.ForceSdr.Contains(_foregroundProcess) ? _foregroundProcess : null);
            bool spacing = now - _lastRealSwitchMs >= SwitchSpacingMs;
            if (sdrFor != null && !_alwaysRealSdr && spacing)
            {
                var displays = TargetDisplays(s.Monitors);
                if (displays.Any(d => d.HdrEnabled))
                {
                    Log("SDR", $"Always-HDR: switching to SDR while '{sdrFor}' needs it ({s.Monitors.Describe()}).");
                    _lastRealSwitchMs = now;
                    if (HdrController.SetHdrState(false, s.Monitors))
                    {
                        _alwaysRealSdr = true;
                        _alwaysRealHdrOn = false;
                        _alwaysSdrFor = sdrFor;
                        if (s.ReloadProfile || s.RefreshApps)
                            SchedulePostSdrFixes(s.Monitors, s.ReloadProfile, s.RefreshApps, s.Browsers, null);
                    }
                    else Log("ERROR", HdrController.LastError);
                }
            }
            else if (sdrFor == null && _alwaysRealSdr && spacing && !CreativeSdrHeld(s, now))
            {
                Log("HDR", $"Always-HDR: '{_alwaysSdrFor}' no longer needs SDR – HDR back on.");
                _lastRealSwitchMs = now;
                if (HdrController.SetHdrState(true, s.Monitors))
                {
                    _alwaysRealSdr = false;
                    _alwaysRealHdrOn = true;
                    _alwaysSdrFor = null;
                    Interlocked.Exchange(ref _alwaysReapply, 1);
                }
                else Log("ERROR", HdrController.LastError);
            }

            // The mode turns HDR on once (at start); after that, HDR you turn off yourself stays off until HDR content starts
            if (_alwaysStartPending && sdrFor == null && spacing)
            {
                _alwaysStartPending = false;
                if (TargetDisplays(s.Monitors).Any(d => !d.HdrEnabled))
                {
                    Log("HDR", $"Always-HDR: turning HDR on ({s.Monitors.Describe()}).");
                    _lastRealSwitchMs = now;
                    if (!HdrController.SetHdrState(true, s.Monitors)) Log("ERROR", HdrController.LastError);
                }
                Interlocked.Exchange(ref _alwaysReapply, 1);
            }

            // Paused: the correction stays on and nothing switches
            if (Paused && !_pauseCorrected)
            {
                _pauseCorrected = true;
                if (_correctionOff.Count > 0)
                {
                    _correctionOff.Clear();
                    Log("HDR", "Always-HDR: paused – correction on, no switching until resumed.");
                    _queriedMonitors = null;   // not "HDR changed by someone else"
                    Interlocked.Exchange(ref _alwaysReapply, 1);
                    Interlocked.Exchange(ref _forceHdrQuery, 1);
                }
            }
            else if (!Paused) _pauseCorrected = false;

            // Shadow lift: a new profile once the setting has stayed the same for a moment (clicked several times in a row)
            if (s.ShadowLift != _shadowLiftPending)
            {
                _shadowLiftPending = s.ShadowLift;
                _shadowLiftSince = now;
            }
            else if (_shadowLiftPending != _shadowLift && now - _shadowLiftSince >= WhiteDebounceMs)
            {
                Log("COLOR", $"Always-HDR: shadow lift {_shadowLift} % -> {_shadowLiftPending} %.");
                ColorProfileLog.Write($"Shadow lift {_shadowLift} % -> {_shadowLiftPending} %");
                _shadowLift = _shadowLiftPending;
                Interlocked.CompareExchange(ref _alwaysReapply, 1, 0);
            }

            int reapply = Interlocked.Exchange(ref _alwaysReapply, 0);
            if (reapply == 0 && now < _nextAlwaysCheckMs) return;
            _nextAlwaysCheckMs = now + AlwaysCheckMs;
            ApplyProfiles(TargetDisplays(s.Monitors), force: reapply == 2, trigger: reapply == 2 ? "resume" : reapply == 1 ? "check" : "poll");
        }

        private void StartAlwaysHdr(Snapshot s)
        {
            _alwaysActive = true;
            var displays = TargetDisplays(s.Monitors);
            // How HDR was when this session of the mode started (app start or mode switched on): what exiting goes back to
            _alwaysWasHdrOn = displays.Any(d => d.HdrEnabled);
            // HDR content that runs right now keeps its HDR: the correction starts off on those displays
            _correctionOff.Clear();
            if (_hdrAny && _hdrOnByApp)
                foreach (var d in displays) _correctionOff.Add(d.DevicePath);
            _alwaysRealSdr = false;
            _alwaysStartPending = true;
            _shadowLift = _shadowLiftPending = s.ShadowLift;   // no waiting at the start
            _queriedMonitors = null;   // the next HDR reading is a fresh starting point, not "changed by someone else"
            Interlocked.Exchange(ref _forceHdrQuery, 1);
            Interlocked.Exchange(ref _alwaysReapply, 1);
            Log("HDR", $"Always-HDR mode on: HDR stays on, SDR content gets a gamma 2.2 correction" +
                       $"{(s.ShadowLift > 0 ? $" with {s.ShadowLift} % shadow lift" : "")} ({s.Monitors.Describe()}).");
        }

        /// <param name="removeEverything">Mode switched off: the generated profiles are removed. On exit they are kept.</param>
        private void StopAlwaysHdr(Snapshot s, bool removeEverything, bool exiting)
        {
            _alwaysActive = false;
            var displays = HdrController.ListDisplayTargets();
            if (removeEverything) Profiles.RemoveAll(displays);
            else Profiles.RestoreOriginals(displays);

            bool wasOn = _alwaysWasHdrOn;
            bool keepOn;
            lock (Sync) keepOn = exiting && Config.AlwaysHdrKeepHdrOnExit;

            bool content = _correctionOff.Count > 0;
            _correctionOff.Clear();
            _alwaysRealSdr = false;
            _alwaysStartPending = false;
            _queriedMonitors = null;
            Interlocked.Exchange(ref _forceHdrQuery, 1);
            AlwaysHdrStatus = string.Empty;

            // Back to how HDR was before the mode started, unless HDR content still runs (Classic mode then switches it
            // off when that ends) or "keep HDR on" was chosen for exiting
            if (content && !exiting)
            {
                _hdrOnByApp = true;
                Log("HDR", "Always-HDR mode off – HDR stays on for the content that is running and goes off when it ends.");
            }
            else if (!wasOn && !keepOn && TargetDisplays(s.Monitors).Any(d => d.HdrEnabled))
            {
                Log("SDR", $"Always-HDR mode {(exiting ? "– app closed" : "off")}: back to SDR, as it was before Always-HDR turned HDR on.");
                if (!HdrController.SetHdrState(false, s.Monitors)) Log("ERROR", HdrController.LastError);
            }
            else Log("HDR", $"Always-HDR mode {(exiting ? "– app closed" : "off")}: HDR left as it is.");
        }

        /// <summary>App exit: the calibration profile goes back, the generated files stay (no regeneration at the next start).</summary>
        private void ExitAlwaysHdr()
        {
            try
            {
                var s = GetSnapshot();
                bool busy = _runningGame != null || _externalHoldProcess != null || Now < Interlocked.Read(ref _launchHoldUntilMs);
                if (busy)
                {
                    // Switching to SDR now would interrupt the game: only the calibration profile goes back
                    Profiles.RestoreOriginals(HdrController.ListDisplayTargets());
                    _alwaysActive = false;
                    Log("HDR", "Always-HDR – app closed while a game runs: calibration profile restored, HDR left on.");
                    return;
                }
                StopAlwaysHdr(s, removeEverything: false, exiting: true);
            }
            catch (Exception ex)
            {
                Log("ERROR", $"Always-HDR on exit: {ex.Message}");
            }
        }

        /// <summary>Settings › Remove Always-HDR profiles: switches the mode off and removes everything it created.</summary>
        public void RemoveAlwaysHdr() => Post(() =>
        {
            bool wasActive = _alwaysActive;
            lock (Sync)
            {
                Config.AlwaysHdrMode = false;
                InvalidateSnapshot();
                Save();
            }
            if (wasActive) StopAlwaysHdr(GetSnapshot(), removeEverything: true, exiting: false);
            else Profiles.RemoveAll(HdrController.ListDisplayTargets());
            Log("HDR", "Always-HDR: all generated colour profiles removed.");
        });
    }
}
