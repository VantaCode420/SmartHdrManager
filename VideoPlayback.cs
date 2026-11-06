using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SmartHdrManager
{
    // =========================================================================
    // VIDEO PLAYBACK DETECTION (for "Switch to HDR for video playback")
    // =========================================================================
    public enum VideoKind { Sdr, Hdr }

    /// <summary>"Smart": HDR videos + SDR videos when RTX Video HDR can use them. "NativeOnly": HDR videos only. "Always": every video.</summary>
    public static class VideoHdrModes
    {
        public const string Smart = "Smart";
        public const string NativeOnly = "NativeOnly";
        public const string Always = "Always";

        public static string Normalize(string? value) =>
            string.Equals(value, NativeOnly, StringComparison.OrdinalIgnoreCase) ? NativeOnly
            : string.Equals(value, Always, StringComparison.OrdinalIgnoreCase) ? Always
            : Smart;
    }

    public static class VideoTriggers
    {
        public const string Fullscreen = "Fullscreen";
        public const string Any = "Any";

        public static string Normalize(string? value) =>
            string.Equals(value, Any, StringComparison.OrdinalIgnoreCase) ? Any : Fullscreen;
    }

    /// <summary>A window that is playing a video right now.</summary>
    public sealed class VideoCandidate
    {
        public IntPtr Hwnd { get; init; }
        public IntPtr Monitor { get; init; }
        public string Process { get; init; } = string.Empty;
        public VideoKind Kind { get; init; }
        public bool Fullscreen { get; init; }
        public bool Foreground { get; init; }
        public bool IsBrowser { get; init; }
        public bool Reported { get; init; }                   // marked by the browser script: certainly a playing video
        public string Source { get; init; } = string.Empty;   // for the log
        public string Title { get; init; } = string.Empty;    // window title (tab title / file name in the player)

        /// <summary>Same window, same kind of video, same monitor = same playback (for the activation delay).</summary>
        public string Key => $"{Hwnd.ToInt64():X}|{Kind}|{Monitor.ToInt64():X}";

        /// <summary>
        /// Which video it is: the window title without the script markers and without YouTube's "(3) " notification
        /// count. Changes when the next video starts (new tab title / next file in the player).
        /// </summary>
        public string VideoName
        {
            get
            {
                string t = Regex.Replace(Title, @"\s*\[(real_hdr_active|sdr_video_active)\]", string.Empty, RegexOptions.IgnoreCase);
                t = Regex.Replace(t, @"^\(\d+\+?\)\s*", string.Empty);
                return t.Trim();
            }
        }

        public string Describe() =>
            $"{(Kind == VideoKind.Hdr ? "HDR" : "SDR")} video in {Process}{(Fullscreen ? " (full screen)" : "")} – {Source}";
    }

    public sealed class VideoScanOptions
    {
        public ICollection<string> Browsers { get; init; } = Array.Empty<string>();
        public ICollection<string> Players { get; init; } = Array.Empty<string>();
        public bool IncludeBrowsers { get; init; }
        public bool IncludePlayers { get; init; }
        public bool FullscreenOnly { get; init; }
        public bool AllowSdr { get; init; }
        public ICollection<string> HdrOnly { get; init; } = Array.Empty<string>();   // Apps tab: "only HDR videos"
        public string HdrToken { get; init; } = string.Empty;
        public string SdrToken { get; init; } = string.Empty;
        public string[] HdrKeywords { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// Finds a window that plays a video. Engine thread only. Cost per call (every 500 ms while the feature is on):
    /// one EnumWindows pass with cached process names; the Windows audio meters are read only when a video player
    /// or a full-screen browser without the script marker is open.
    ///
    /// Signals:
    ///   • Browsers: the userscript marker in the tab title ([real_hdr_active] = HDR video, [sdr_video_active] = SDR video).
    ///     Without the script: a full-screen browser that is playing sound counts as an SDR video.
    ///   • Video players: the player's audio session is producing sound; HDR if the window title has an HDR keyword,
    ///     or the file on the player's command line has one in its name or HDR in its video header (VideoFileInfo).
    /// </summary>
    internal sealed class VideoPlaybackMonitor
    {
        private const int AudibleWindowMs = 1200;   // sound within this time = "playing" (bridges quiet scenes between samples)

        private readonly AudioActivity _audio = new();
        private readonly VideoFileInfo _files = new();   // HDR from the played file when the title has no keyword
        private readonly Dictionary<uint, string> _names = new();
        private long _namesClearedAt;
        private readonly StringBuilder _sb = new(512);

        private struct WindowInfo
        {
            public IntPtr Hwnd;
            public uint Pid;
            public string Process;
            public string Title;
            public bool IsBrowser;
        }

        private readonly List<WindowInfo> _windows = new();

        public VideoCandidate? Find(VideoScanOptions o)
        {
            if (!o.IncludeBrowsers && !o.IncludePlayers) return null;

            long now = Environment.TickCount64;
            if (now - _namesClearedAt > 60000) { _names.Clear(); _namesClearedAt = now; }   // PIDs get reused

            CollectWindows(o);
            if (_windows.Count == 0) return null;

            IntPtr foreground = Native.GetForegroundWindow();
            bool audioSampled = false;
            VideoCandidate? best = null;

            foreach (var w in _windows)
            {
                bool fullscreen = Native.IsFullscreen(w.Hwnd);
                if (o.FullscreenOnly && !fullscreen) continue;

                VideoKind? kind = null;
                string source = string.Empty;
                bool reported = false;

                if (w.IsBrowser)
                {
                    if (o.HdrToken.Length > 0 && w.Title.Contains(o.HdrToken, StringComparison.OrdinalIgnoreCase))
                    {
                        kind = VideoKind.Hdr; source = "HDR stream reported by the browser script"; reported = true;
                    }
                    else if (o.SdrToken.Length > 0 && w.Title.Contains(o.SdrToken, StringComparison.OrdinalIgnoreCase))
                    {
                        kind = VideoKind.Sdr; source = "video reported by the browser script"; reported = true;
                    }
                    else if (fullscreen)
                    {
                        // No script on this site: a full-screen browser with sound is almost always a video.
                        // (Never in a window: that could just be music in a background tab.)
                        if (!audioSampled) { _audio.Sample(ProcessName); audioSampled = true; }
                        if (_audio.IsAudible(w.Process, now, AudibleWindowMs))
                        {
                            kind = VideoKind.Sdr; source = "full-screen browser playing sound";
                        }
                    }
                }
                else
                {
                    // Players: an open, running audio stream = playing, even during silent scenes.
                    // (Players stop their stream when paused; browsers keep theirs open, so browsers use the sound level.)
                    if (!audioSampled) { _audio.Sample(ProcessName); audioSampled = true; }
                    if (_audio.IsStreaming(w.Process, now, AudibleWindowMs))
                    {
                        string? keyword = null;
                        foreach (var k in o.HdrKeywords)
                            if (w.Title.Contains(k, StringComparison.OrdinalIgnoreCase)) { keyword = k; break; }
                        kind = keyword != null ? VideoKind.Hdr : VideoKind.Sdr;
                        source = keyword != null ? $"player is playing, HDR keyword '{keyword}' in the title" : "player is playing";

                        // No keyword in the title (mpv shows the title stored in the file, many names have no "HDR"):
                        // look at the file on the player's command line – its name and its video header
                        if (keyword == null && _files.HdrReason(w.Pid, w.Process, w.Title, o.HdrKeywords) is string fromFile)
                        {
                            kind = VideoKind.Hdr;
                            source = $"player is playing, {fromFile}";
                        }
                    }
                }

                if (kind == null) continue;
                if (kind == VideoKind.Sdr && (!o.AllowSdr || o.HdrOnly.Contains(w.Process))) continue;

                var candidate = new VideoCandidate
                {
                    Hwnd = w.Hwnd,
                    Monitor = Native.MonitorFromWindow(w.Hwnd, Native.MONITOR_DEFAULTTONEAREST),
                    Process = w.Process,
                    Kind = kind.Value,
                    Fullscreen = fullscreen,
                    Foreground = w.Hwnd == foreground,
                    IsBrowser = w.IsBrowser,
                    Reported = reported,
                    Source = source,
                    Title = w.Title   // always set (GetWindowText result) – a "?? " here made the compiler warn above
                };
                if (best == null || Rank(candidate) > Rank(best)) best = candidate;
            }
            return best;
        }

        // The window in front wins, then HDR over SDR, then full screen over windowed
        private static int Rank(VideoCandidate c) => (c.Foreground ? 4 : 0) + (c.Kind == VideoKind.Hdr ? 2 : 0) + (c.Fullscreen ? 1 : 0);

        private void CollectWindows(VideoScanOptions o)
        {
            _windows.Clear();
            Native.EnumWindows((hWnd, _) =>
            {
                if (!Native.IsWindowVisible(hWnd) || Native.IsIconic(hWnd) || Native.IsCloaked(hWnd)) return true;
                _sb.Clear();
                if (Native.GetWindowText(hWnd, _sb, _sb.Capacity) == 0) return true;

                Native.GetWindowThreadProcessId(hWnd, out uint pid);
                string name = ProcessName(pid);
                if (name.Length == 0) return true;

                bool browser = o.IncludeBrowsers && o.Browsers.Contains(name);
                bool player = !browser && o.IncludePlayers && o.Players.Contains(name);
                if (browser || player)
                    _windows.Add(new WindowInfo { Hwnd = hWnd, Pid = pid, Process = name, Title = _sb.ToString(), IsBrowser = browser });
                return true;
            }, IntPtr.Zero);
        }

        private string ProcessName(uint pid)
        {
            if (pid == 0) return string.Empty;
            if (_names.TryGetValue(pid, out var cached)) return cached;
            string name;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName.ToLowerInvariant();
            }
            catch
            {
                name = string.Empty;
            }
            _names[pid] = name;
            return name;
        }

        private static class Native
        {
            public const uint MONITOR_DEFAULTTONEAREST = 2;
            private const int DWMWA_CLOAKED = 14;

            [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
            [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

            public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
            [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
            [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
            [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
            [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
            [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
            [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

            /// <summary>Windows on another virtual desktop and suspended UWP windows are "visible" but cloaked.</summary>
            public static bool IsCloaked(IntPtr hWnd) =>
                DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

            public static bool IsFullscreen(IntPtr hWnd)
            {
                if (!GetWindowRect(hWnd, out RECT r)) return false;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST), ref mi)) return false;
                return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                       r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
            }
        }
    }

    // =========================================================================
    // AUDIO ACTIVITY (Windows Core Audio: which processes are producing sound right now)
    // =========================================================================
    /// <summary>
    /// Reads the peak meter of every active audio session on every output device. Plain COM interop, no extra
    /// libraries. Only the peak value is read (no audio data). Used on the engine thread only.
    /// </summary>
    internal sealed class AudioActivity
    {
        private const float AudiblePeak = 0.001f;            // about -60 dB
        private const int DeviceRefreshMs = 30000;           // re-list output devices (headset plugged in, ...)

        private readonly Dictionary<string, long> _lastAudible = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<IAudioSessionManager2> _managers = new();
        private long _managersAt = long.MinValue / 2;
        private bool _failedLogged;

        private readonly Dictionary<string, long> _lastStreaming = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The process produced sound recently.</summary>
        public bool IsAudible(string process, long now, int windowMs) =>
            _lastAudible.TryGetValue(process, out long at) && now - at <= windowMs;

        /// <summary>The process had a running (active) audio stream recently, silent or not.</summary>
        public bool IsStreaming(string process, long now, int windowMs) =>
            _lastStreaming.TryGetValue(process, out long at) && now - at <= windowMs;

        public void Sample(Func<uint, string> processName)
        {
            long now = Environment.TickCount64;
            try
            {
                if (now - _managersAt > DeviceRefreshMs || _managers.Count == 0) RefreshDevices(now);

                foreach (var manager in _managers)
                {
                    if (manager.GetSessionEnumerator(out IAudioSessionEnumerator? sessions) != 0 || sessions == null) continue;
                    try
                    {
                        if (sessions.GetCount(out int count) != 0) continue;
                        for (int i = 0; i < count; i++)
                        {
                            if (sessions.GetSession(i, out object? session) != 0 || session == null) continue;
                            try
                            {
                                if (session is not IAudioSessionControl2 control) continue;
                                if (control.GetState(out int state) != 0 || state != 1) continue;   // 1 = AudioSessionStateActive
                                if (control.GetProcessId(out uint pid) != 0 || pid == 0) continue;
                                string name = processName(pid);
                                if (name.Length == 0) continue;
                                _lastStreaming[name] = now;

                                if (session is not IAudioMeterInformation meter) continue;
                                if (meter.GetPeakValue(out float peak) != 0 || peak < AudiblePeak) continue;
                                _lastAudible[name] = now;
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(session);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sessions);
                    }
                }
            }
            catch (Exception ex)
            {
                // Audio service restarted, device removed, ...: start over with a fresh device list next time
                ReleaseManagers();
                if (!_failedLogged)
                {
                    _failedLogged = true;
                    HdrEngine.WriteFileLog($"VIDEO  Audio sessions could not be read: {ex.Message}");
                }
            }
        }

        private void RefreshDevices(long now)
        {
            ReleaseManagers();
            _managersAt = now;

            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                if (enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out IMMDeviceCollection? devices) != 0 || devices == null)
                    return;
                try
                {
                    devices.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (devices.Item(i, out IMMDevice? device) != 0 || device == null) continue;
                        try
                        {
                            Guid iid = typeof(IAudioSessionManager2).GUID;
                            if (device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out object? manager) == 0 &&
                                manager is IAudioSessionManager2 m)
                                _managers.Add(m);
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(devices);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }

        private void ReleaseManagers()
        {
            foreach (var m in _managers)
            {
                try { Marshal.ReleaseComObject(m); } catch { }
            }
            _managers.Clear();
        }

        // ---- COM declarations (vtable order matters; methods that are not used are declared as placeholders) ----
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection? devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice? device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);   // IAudioSessionManager
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);      // IAudioSessionManager
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object? session);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName(out IntPtr name);
            [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);
            [PreserveSig] int GetIconPath(out IntPtr path);
            [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);
            [PreserveSig] int GetGroupingParam(out Guid groupingParam);
            [PreserveSig] int SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr id);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
            [PreserveSig] int GetProcessId(out uint pid);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
        }
    }

    // =========================================================================
    // NVIDIA RTX VIDEO HDR (read-only)
    // =========================================================================
    /// <summary>
    /// Whether an NVIDIA RTX GPU is installed and whether "RTX Video HDR" is switched on in the NVIDIA App.
    ///
    /// Read-only on purpose: NVAPI has no public call for RTX Video HDR, and the driver value lives under HKLM
    /// (needs administrator rights to change, and the driver does not pick up a change while running).
    /// Switching it off for native HDR videos is not needed anyway: RTX Video HDR only converts SDR video;
    /// HDR video is passed through unchanged.
    /// </summary>
    public sealed class RtxVideoStatus
    {
        public bool RtxGpu { get; init; }
        public string? GpuName { get; init; }
        public bool? VideoHdrOn { get; init; }   // null = could not be read (value missing / no access)

        /// <summary>Smart mode: SDR videos only switch Windows to HDR if RTX Video HDR can actually use it.</summary>
        public bool CanUpgradeSdr => RtxGpu && VideoHdrOn != false;

        public string Describe() =>
            !RtxGpu ? "No NVIDIA RTX graphics card found – SDR videos stay in SDR in Smart mode."
            : VideoHdrOn == true ? $"RTX Video HDR is on ({GpuName})."
            : VideoHdrOn == false ? $"RTX Video HDR is off in the NVIDIA App ({GpuName}) – SDR videos stay in SDR in Smart mode."
            : $"{GpuName} found. RTX Video HDR state could not be read; SDR videos are treated as supported.";

        private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        public static RtxVideoStatus Query()
        {
            string? gpu = null;
            bool? on = null;
            try
            {
                using var cls = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
                if (cls != null)
                {
                    foreach (var sub in cls.GetSubKeyNames())
                    {
                        if (sub.Length != 4 || !int.TryParse(sub, out _)) continue;   // "0000", "0001", ... (skip "Properties")
                        try
                        {
                            using var key = cls.OpenSubKey(sub);
                            string desc = key?.GetValue("DriverDesc") as string ?? string.Empty;
                            if (desc.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) < 0 ||
                                desc.IndexOf("RTX", StringComparison.OrdinalIgnoreCase) < 0) continue;

                            gpu ??= desc;
                            // Written by the NVIDIA App / Control Panel: 0 = off, anything else = on
                            if (key!.GetValue("_User_Global_VAL_TrueHDR") is int value)
                            {
                                on = value != 0;
                                gpu = desc;
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return new RtxVideoStatus { RtxGpu = gpu != null, GpuName = gpu, VideoHdrOn = on };
        }
    }

    // =========================================================================
    // POWER
    // =========================================================================
    internal static class PowerInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        /// <summary>True only on a laptop that is running on battery (desktops report "online" or "unknown").</summary>
        public static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;
    }
}
