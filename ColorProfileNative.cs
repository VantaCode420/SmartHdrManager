using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartHdrManager
{
    /// <summary>
    /// Log file of the colour-profile code. Separate from log.txt because the test commands run next to the tray app, and
    /// two processes appending to one file can lose lines.
    /// </summary>
    internal static class ColorProfileLog
    {
        public static readonly string Folder = System.IO.Path.GetDirectoryName(HdrEngine.LogPath)!;
        public static readonly string FilePath = System.IO.Path.Combine(Folder, "always-hdr-log.txt");
        private static readonly object Gate = new();
        private const long MaxBytes = 1024 * 1024;   // then the older part moves to always-hdr-log.old.txt, like log.txt
        private static int _writes;

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    System.IO.Directory.CreateDirectory(Folder);
                    if (++_writes % 100 == 1 && System.IO.File.Exists(FilePath) && new System.IO.FileInfo(FilePath).Length > MaxBytes)
                        System.IO.File.Move(FilePath, System.IO.Path.ChangeExtension(FilePath, ".old.txt"), overwrite: true);
                    System.IO.File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
                }
            }
            catch { }   // logging must never break the feature
        }
    }

    /// <summary>
    /// The Windows colour-profile functions Always-HDR needs (mscms.dll, Windows 11). Every call is logged with its
    /// arguments and result, so a log file is enough to tell why something did not work on someone's screen.
    /// </summary>
    internal static class ColorProfileNative
    {
        public const int ScopeSystemWide = 0;
        public const int ScopeCurrentUser = 1;
        public const int TypeIcc = 0;
        public const int SubtypeSdr = 7;   // CPST_STANDARD_DISPLAY_COLOR_MODE
        public const int SubtypeHdr = 8;   // CPST_EXTENDED_DISPLAY_COLOR_MODE

        private const string Mscms = "mscms.dll";

        [DllImport(Mscms, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool InstallColorProfileW(string? machine, string profilePath);

        [DllImport(Mscms, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool UninstallColorProfileW(string? machine, string profileName, [MarshalAs(UnmanagedType.Bool)] bool delete);

        [DllImport(Mscms, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetColorDirectoryW(string? machine, StringBuilder? buffer, ref uint size);

        [DllImport(Mscms)] private static extern int ColorProfileGetDisplayUserScope(HdrController.LUID adapter, uint sourceId, out int scope);

        [DllImport(Mscms, CharSet = CharSet.Unicode)]
        private static extern int ColorProfileGetDisplayDefault(int scope, HdrController.LUID adapter, uint sourceId, int type, int subtype, out IntPtr profileName);

        [DllImport(Mscms)]
        private static extern int ColorProfileGetDisplayList(int scope, HdrController.LUID adapter, uint sourceId, out IntPtr profileList, out uint count);

        [DllImport(Mscms, CharSet = CharSet.Unicode)]
        private static extern int ColorProfileAddDisplayAssociation(int scope, string profileName, HdrController.LUID adapter, uint sourceId,
                                                                    [MarshalAs(UnmanagedType.Bool)] bool setAsDefault,
                                                                    [MarshalAs(UnmanagedType.Bool)] bool associateAsAdvancedColor);

        [DllImport(Mscms, CharSet = CharSet.Unicode)]
        private static extern int ColorProfileRemoveDisplayAssociation(int scope, string profileName, HdrController.LUID adapter, uint sourceId,
                                                                       [MarshalAs(UnmanagedType.Bool)] bool dissociateAdvancedColor);

        [DllImport(Mscms, CharSet = CharSet.Unicode)]
        private static extern int ColorProfileSetDisplayDefaultAssociation(int scope, string profileName, int type, int subtype, HdrController.LUID adapter, uint sourceId);

        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

        private static void Log(string line) => ColorProfileLog.Write(line);

        private static string Id(HdrController.LUID adapter, uint sourceId) => $"adapter={adapter.HighPart:X8}:{adapter.LowPart:X8} source={sourceId}";

        private static string Hr(int hr) => hr == 0 ? "S_OK" : $"0x{hr:X8} ({new Win32Exception(hr).Message})";

        /// <summary>The functions are documented from build 20348 on: true when this Windows has all of them.</summary>
        public static bool IsAvailable(out string missing)
        {
            missing = string.Empty;
            if (!NativeLibrary.TryLoad(Mscms, typeof(ColorProfileNative).Assembly, DllImportSearchPath.System32, out IntPtr library))
            {
                missing = Mscms;
                return false;
            }
            foreach (string name in new[]
            {
                "ColorProfileGetDisplayUserScope", "ColorProfileGetDisplayDefault", "ColorProfileGetDisplayList",
                "ColorProfileAddDisplayAssociation", "ColorProfileRemoveDisplayAssociation", "ColorProfileSetDisplayDefaultAssociation"
            })
            {
                if (NativeLibrary.TryGetExport(library, name, out _)) continue;
                missing = name;
                return false;
            }
            return true;
        }

        public static string? GetColorDirectory()
        {
            uint size = 0;
            GetColorDirectoryW(null, null, ref size);
            var buffer = new StringBuilder((int)size / 2 + 1);
            if (GetColorDirectoryW(null, buffer, ref size)) return buffer.ToString();
            Log($"GetColorDirectoryW failed: {Marshal.GetLastPInvokeError()}");
            return null;
        }

        public static int GetUserScope(HdrController.LUID adapter, uint sourceId, out int scope)
        {
            int hr = ColorProfileGetDisplayUserScope(adapter, sourceId, out scope);
            Log($"ColorProfileGetDisplayUserScope({Id(adapter, sourceId)}) = {Hr(hr)}, scope={scope}");
            return hr;
        }

        /// <summary>
        /// Default profile of a slot, or null when there is none. The result code is returned too. <paramref name="quiet"/>:
        /// the once-a-second check, logged only when it fails (else the log would grow by megabytes a day).
        /// </summary>
        public static string? GetDefault(int scope, HdrController.LUID adapter, uint sourceId, int subtype, out int hr, bool quiet = false)
        {
            hr = ColorProfileGetDisplayDefault(scope, adapter, sourceId, TypeIcc, subtype, out IntPtr name);
            string? value = null;
            if (name != IntPtr.Zero)
            {
                value = Marshal.PtrToStringUni(name);
                LocalFree(name);
            }
            const int NoProfile = unchecked((int)0x80070002);   // "file not found": the slot has no profile
            if (!quiet || hr != 0 && hr != NoProfile)
                Log($"ColorProfileGetDisplayDefault(scope={scope}, {Id(adapter, sourceId)}, subtype={subtype}) = {Hr(hr)}, name='{value}'");
            return hr == 0 && !string.IsNullOrEmpty(value) ? value : null;
        }

        public static List<string> GetList(int scope, HdrController.LUID adapter, uint sourceId, out int hr)
        {
            var result = new List<string>();
            hr = ColorProfileGetDisplayList(scope, adapter, sourceId, out IntPtr list, out uint count);
            if (hr == 0 && list != IntPtr.Zero)
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                    if (entry == IntPtr.Zero) continue;
                    string? name = Marshal.PtrToStringUni(entry);
                    if (name != null) result.Add(name);
                }
                LocalFree(list);
            }
            Log($"ColorProfileGetDisplayList(scope={scope}, {Id(adapter, sourceId)}) = {Hr(hr)}, count={count}: {string.Join(" | ", result)}");
            return result;
        }

        /// <summary>Copies a profile into Windows' colour store. Returns 0 or the Win32 error.</summary>
        public static int Install(string path)
        {
            bool ok = InstallColorProfileW(null, path);
            int error = ok ? 0 : Marshal.GetLastPInvokeError();
            Log($"InstallColorProfileW('{path}') = {(ok ? "OK" : "FAILED " + error + " (" + new Win32Exception(error).Message + ")")}");
            return error;
        }

        public static int Uninstall(string profileName)
        {
            bool ok = UninstallColorProfileW(null, profileName, true);
            int error = ok ? 0 : Marshal.GetLastPInvokeError();
            Log($"UninstallColorProfileW('{profileName}', delete) = {(ok ? "OK" : "FAILED " + error + " (" + new Win32Exception(error).Message + ")")}");
            return error;
        }

        public static int AddAssociation(int scope, string profileName, HdrController.LUID adapter, uint sourceId)
        {
            int hr = ColorProfileAddDisplayAssociation(scope, profileName, adapter, sourceId, false, true);
            Log($"ColorProfileAddDisplayAssociation(scope={scope}, '{profileName}', {Id(adapter, sourceId)}, setAsDefault=false, advancedColor=true) = {Hr(hr)}");
            return hr;
        }

        public static int RemoveAssociation(int scope, string profileName, HdrController.LUID adapter, uint sourceId)
        {
            int hr = ColorProfileRemoveDisplayAssociation(scope, profileName, adapter, sourceId, true);
            Log($"ColorProfileRemoveDisplayAssociation(scope={scope}, '{profileName}', {Id(adapter, sourceId)}, advancedColor=true) = {Hr(hr)}");
            return hr;
        }

        public static int SetDefault(int scope, string profileName, HdrController.LUID adapter, uint sourceId)
        {
            int hr = ColorProfileSetDisplayDefaultAssociation(scope, profileName, TypeIcc, SubtypeHdr, adapter, sourceId);
            Log($"ColorProfileSetDisplayDefaultAssociation(scope={scope}, '{profileName}', subtype={SubtypeHdr}, {Id(adapter, sourceId)}) = {Hr(hr)}");
            return hr;
        }
    }
}
