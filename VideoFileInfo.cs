using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartHdrManager
{
    /// <summary>
    /// Is the file a local video player plays an HDR video? For players whose window title has no HDR keyword
    /// (mpv shows the title stored in the file, many files have no "HDR" in their name).
    ///
    /// 1. The file: taken from the player's command line (double-click, "Open with", right-click entries). A file opened
    ///    later from the player's own menu or by drag and drop is not on the command line – then the title keywords
    ///    stay the only signal, as before.
    /// 2. HDR: a keyword in the file name, or the video track's header – MKV/WebM (Colour › TransferCharacteristics,
    ///    MaxCLL/MaxFALL, mastering metadata, Dolby Vision mapping) and MP4/MOV ("colr" nclx transfer, "mdcv"/"clli",
    ///    Dolby Vision sample entries and "dvcC"/"dvvC"). PQ (16) and HLG (18) count as HDR.
    ///
    /// Read-only, only the start of the file (MKV) or the "moov" box (MP4), once per file (cached by path, size and time).
    /// Never throws. Engine thread only.
    /// </summary>
    internal sealed class VideoFileInfo
    {
        private const int MkvReadBytes = 4 * 1024 * 1024;     // the track headers are at the start of the file
        private const long MaxMoovBytes = 32L * 1024 * 1024;  // "moov" (sample tables) of a long movie: a few MB
        private const int MaxCachedFiles = 64;
        private const int MaxCachedProcesses = 32;

        private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".webm", ".mk3d", ".mp4", ".m4v", ".mov", ".ts", ".m2ts", ".mts", ".avi", ".hevc", ".265", ".h265"
        };

        // Players whose window title can be the title stored in the file instead of the file name
        private static readonly HashSet<string> MediaTitlePlayers = new(StringComparer.OrdinalIgnoreCase) { "mpv", "mpvnet", "vlc" };

        private readonly Dictionary<string, (long Size, DateTime Time, string? Reason)> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<uint, (long At, List<string> Files)> _commandLines = new();

        /// <summary>
        /// Why the video playing in this player is HDR ("HDR in the file: transfer PQ"), or null if it is not known to be HDR.
        /// </summary>
        public string? HdrReason(uint pid, string process, string title, string[] keywords)
        {
            try
            {
                string? file = PlayingFile(pid, process, title);
                if (file == null) return null;

                var info = new FileInfo(file);
                if (!info.Exists) return null;
                if (_files.TryGetValue(file, out var cached) && cached.Size == info.Length && cached.Time == info.LastWriteTimeUtc)
                    return cached.Reason;

                string? reason = null;
                string name = Path.GetFileName(file);
                foreach (var k in keywords)
                    if (name.Contains(k, StringComparison.OrdinalIgnoreCase)) { reason = $"HDR keyword '{k}' in the file name"; break; }
                if (reason == null)
                {
                    try { reason = ReadHeader(file); }
                    catch { reason = null; }   // unreadable or damaged: remembered as "not HDR", not retried every scan
                }

                if (_files.Count >= MaxCachedFiles) _files.Clear();
                _files[file] = (info.Length, info.LastWriteTimeUtc, reason);
                return reason;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The video file on the player's command line that it plays now: the one whose name is in the window title.
        /// mpv / mpv.net / VLC may show the title stored in the file instead – for them a single file on the command line
        /// counts too. (MPC-HC reuses its window for the next file but keeps its first command line, so it needs the name.)
        /// </summary>
        private string? PlayingFile(uint pid, string process, string title)
        {
            var files = CommandLineFiles(pid);
            if (files.Count == 0) return null;
            foreach (var f in files)
            {
                string stem = Path.GetFileNameWithoutExtension(f);
                if (stem.Length >= 3 && title.Contains(stem, StringComparison.OrdinalIgnoreCase)) return f;
            }
            return files.Count == 1 && MediaTitlePlayers.Contains(process) ? files[0] : null;
        }

        private List<string> CommandLineFiles(uint pid)
        {
            long now = Environment.TickCount64;
            if (_commandLines.TryGetValue(pid, out var cached) && now - cached.At < 60000) return cached.Files;   // PIDs get reused

            var files = new List<string>();
            string? commandLine = Native.CommandLineOf(pid);
            if (!string.IsNullOrEmpty(commandLine))
            {
                var args = Native.SplitArgs(commandLine);
                for (int i = 1; i < args.Length && files.Count < 50; i++)   // args[0] = the player itself
                {
                    string a = args[i].Trim();
                    if (a.Length < 5 || a.StartsWith("-", StringComparison.Ordinal) || a.StartsWith("/", StringComparison.Ordinal)) continue;
                    if (a.Contains("://", StringComparison.Ordinal)) continue;   // streams and URLs
                    if (!Path.IsPathRooted(a) || !VideoExtensions.Contains(Path.GetExtension(a))) continue;
                    if (File.Exists(a)) files.Add(a);
                }
            }

            if (_commandLines.Count >= MaxCachedProcesses) _commandLines.Clear();
            _commandLines[pid] = (now, files);
            return files;
        }

        // ------------------------------------------------------------------ file headers

        private static string? ReadHeader(string file)
        {
            string ext = Path.GetExtension(file);
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            if (ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".mk3d", StringComparison.OrdinalIgnoreCase))
                return Mkv(fs);
            if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".mov", StringComparison.OrdinalIgnoreCase))
                return Mp4(fs);
            return null;   // .ts/.m2ts/.avi/raw HEVC: HDR is only in the video stream itself – not read
        }

        private static string? TransferName(ulong t) => t switch { 16 => "PQ", 18 => "HLG", _ => null };

        // ---- Matroska / WebM (EBML) ----

        private const uint EbmlSegment = 0x18538067, EbmlTracks = 0x1654AE6B, EbmlTrackEntry = 0xAE, EbmlVideo = 0xE0,
            EbmlColour = 0x55B0, EbmlTransfer = 0x55BA, EbmlMaxCll = 0x55BC, EbmlMaxFall = 0x55BD, EbmlMastering = 0x55D0,
            EbmlBlockAdditionMapping = 0x41E4, EbmlBlockAddIdType = 0x41E7, EbmlCluster = 0x1F43B675;

        private static string? Mkv(FileStream fs)
        {
            var buf = new byte[(int)Math.Min(fs.Length, MkvReadBytes)];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < 4 || buf[0] != 0x1A || buf[1] != 0x45 || buf[2] != 0xDF || buf[3] != 0xA3) return null;   // no EBML header
            return MkvLevel(buf, 0, read, 0);
        }

        /// <summary>Walks the elements in buf[pos..end), descending only into the ones that lead to the video track's colour.</summary>
        private static string? MkvLevel(byte[] buf, int pos, int end, int depth)
        {
            if (depth > 8) return null;
            while (pos < end)
            {
                if (!ReadVint(buf, ref pos, end, keepMarker: true, out ulong id)) return null;
                if (!ReadVint(buf, ref pos, end, keepMarker: false, out ulong size, out bool unknownSize)) return null;
                if (id == EbmlCluster) return null;   // video data starts: the headers are behind us

                int dataEnd = unknownSize || size > (ulong)(end - pos) ? end : pos + (int)size;
                switch ((uint)id)
                {
                    case EbmlSegment:
                    case EbmlTracks:
                    case EbmlTrackEntry:
                    case EbmlVideo:
                    case EbmlColour:
                    case EbmlBlockAdditionMapping:
                        string? inner = MkvLevel(buf, pos, dataEnd, depth + 1);
                        if (inner != null) return inner;
                        break;
                    case EbmlTransfer:
                        if (TransferName(ReadUInt(buf, pos, dataEnd)) is string t) return $"HDR in the file: transfer {t}";
                        break;
                    case EbmlMaxCll:
                    case EbmlMaxFall:
                        if (ReadUInt(buf, pos, dataEnd) > 0) return "HDR in the file: MaxCLL/MaxFALL";
                        break;
                    case EbmlMastering:
                        return "HDR in the file: mastering display metadata";
                    case EbmlBlockAddIdType:
                        ulong type = ReadUInt(buf, pos, dataEnd);
                        if (type == 0x64766343 || type == 0x64767643) return "HDR in the file: Dolby Vision";   // "dvcC" / "dvvC"
                        break;
                }
                if (unknownSize && (uint)id != EbmlSegment) return null;   // cannot skip an element of unknown size
                pos = dataEnd;
            }
            return null;
        }

        private static bool ReadVint(byte[] buf, ref int pos, int end, bool keepMarker, out ulong value) =>
            ReadVint(buf, ref pos, end, keepMarker, out value, out _);

        /// <summary>EBML variable-length integer: element IDs keep their length marker, sizes do not.</summary>
        private static bool ReadVint(byte[] buf, ref int pos, int end, bool keepMarker, out ulong value, out bool unknown)
        {
            value = 0;
            unknown = false;
            if (pos >= end) return false;
            byte first = buf[pos];
            int length = 1;
            while (length <= 8 && (first & (0x80 >> (length - 1))) == 0) length++;
            if (length > 8 || pos + length > end) return false;

            value = keepMarker ? first : (ulong)(first & (0xFF >> length));
            bool allOnes = (first & (0xFF >> length)) == (0xFF >> length);
            for (int i = 1; i < length; i++)
            {
                value = (value << 8) | buf[pos + i];
                if (buf[pos + i] != 0xFF) allOnes = false;
            }
            unknown = !keepMarker && allOnes;
            pos += length;
            return true;
        }

        private static ulong ReadUInt(byte[] buf, int pos, int end)
        {
            ulong v = 0;
            for (int i = pos; i < end && i < pos + 8; i++) v = (v << 8) | buf[i];
            return v;
        }

        // ---- MP4 / MOV (ISO base media boxes) ----

        private static readonly HashSet<string> Mp4Containers = new(StringComparer.Ordinal) { "trak", "mdia", "minf", "stbl" };
        private static readonly HashSet<string> DolbyVisionEntries = new(StringComparer.Ordinal) { "dvh1", "dvhe", "dva1", "dvav", "dav1" };
        private static readonly HashSet<string> VideoEntries = new(StringComparer.Ordinal)
            { "hvc1", "hev1", "avc1", "avc3", "av01", "vp09", "dvh1", "dvhe", "dva1", "dvav", "dav1" };

        private static string? Mp4(FileStream fs)
        {
            // Top-level boxes: jump from header to header until "moov" (at the start or at the end of the file)
            long pos = 0, length = fs.Length;
            var header = new byte[16];
            for (int guard = 0; guard < 64 && pos + 8 <= length; guard++)
            {
                fs.Position = pos;
                if (fs.Read(header, 0, 16) < 8) return null;
                long size = ReadBE32(header, 0);
                string type = Encoding.ASCII.GetString(header, 4, 4);
                int headerSize = 8;
                if (size == 1) { size = (long)ReadBE64(header, 8); headerSize = 16; }
                else if (size == 0) size = length - pos;
                if (size < headerSize) return null;

                if (type == "moov")
                {
                    long bodySize = size - headerSize;
                    if (bodySize <= 0 || bodySize > MaxMoovBytes) return null;
                    var moov = new byte[bodySize];
                    fs.Position = pos + headerSize;
                    int read = 0;
                    while (read < moov.Length)
                    {
                        int n = fs.Read(moov, read, moov.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return Mp4Level(moov, 0, read, 0);
                }
                pos += size;
            }
            return null;
        }

        private static string? Mp4Level(byte[] buf, int pos, int end, int depth)
        {
            if (depth > 8) return null;
            while (pos + 8 <= end)
            {
                long size = ReadBE32(buf, pos);
                string type = Encoding.ASCII.GetString(buf, pos + 4, 4);
                int headerSize = 8;
                if (size == 1)
                {
                    if (pos + 16 > end) return null;
                    size = (long)ReadBE64(buf, pos + 8);
                    headerSize = 16;
                }
                else if (size == 0) size = end - pos;
                if (size < headerSize || size > end - pos) return null;
                int bodyStart = pos + headerSize, boxEnd = pos + (int)size;

                if (Mp4Containers.Contains(type))
                {
                    string? inner = Mp4Level(buf, bodyStart, boxEnd, depth + 1);
                    if (inner != null) return inner;
                }
                else if (type == "stsd" && bodyStart + 8 <= boxEnd)
                {
                    string? inner = Mp4SampleEntries(buf, bodyStart + 8, boxEnd);   // version/flags + entry count
                    if (inner != null) return inner;
                }
                pos = boxEnd;
            }
            return null;
        }

        private static string? Mp4SampleEntries(byte[] buf, int pos, int end)
        {
            while (pos + 8 <= end)
            {
                long size = ReadBE32(buf, pos);
                string type = Encoding.ASCII.GetString(buf, pos + 4, 4);
                if (size < 8 || size > end - pos) return null;
                int entryEnd = pos + (int)size;

                if (DolbyVisionEntries.Contains(type)) return "HDR in the file: Dolby Vision";
                if (VideoEntries.Contains(type))
                {
                    // VisualSampleEntry: 8 header + 78 bytes of fixed fields, then its child boxes
                    string? inner = Mp4VideoBoxes(buf, pos + 8 + 78, entryEnd);
                    if (inner != null) return inner;
                }
                pos = entryEnd;
            }
            return null;
        }

        private static string? Mp4VideoBoxes(byte[] buf, int pos, int end)
        {
            while (pos + 8 <= end)
            {
                long size = ReadBE32(buf, pos);
                string type = Encoding.ASCII.GetString(buf, pos + 4, 4);
                if (size < 8 || size > end - pos) return null;
                int body = pos + 8, boxEnd = pos + (int)size;

                switch (type)
                {
                    case "colr" when body + 10 <= boxEnd:
                        string colourType = Encoding.ASCII.GetString(buf, body, 4);
                        if ((colourType == "nclx" || colourType == "nclc") &&
                            TransferName((ulong)((buf[body + 6] << 8) | buf[body + 7])) is string t)
                            return $"HDR in the file: transfer {t}";
                        break;
                    case "vpcC" when body + 9 <= boxEnd:
                        // full box (4) + profile, level, bit depth/chroma/range, colour primaries, transfer
                        if (TransferName(buf[body + 8]) is string vt) return $"HDR in the file: transfer {vt}";
                        break;
                    case "mdcv":
                    case "clli":
                        return "HDR in the file: mastering display metadata";
                    case "dvcC":
                    case "dvvC":
                    case "dvwC":
                        return "HDR in the file: Dolby Vision";
                }
                pos = boxEnd;
            }
            return null;
        }

        private static long ReadBE32(byte[] b, int i) => ((long)b[i] << 24) | ((long)b[i + 1] << 16) | ((long)b[i + 2] << 8) | b[i + 3];

        private static ulong ReadBE64(byte[] b, int i)
        {
            ulong v = 0;
            for (int k = 0; k < 8; k++) v = (v << 8) | b[i + k];
            return v;
        }

        // ------------------------------------------------------------------ command line of another process

        private static class Native
        {
            private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            private const int ProcessCommandLineInformation = 60;   // Windows 8.1 and newer

            [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
            [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
            [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
            [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
            [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

            /// <summary>The command line of another process, with the same limited access Task Manager uses. Null if not readable.</summary>
            public static string? CommandLineOf(uint pid)
            {
                if (pid == 0) return null;
                IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (process == IntPtr.Zero) return null;
                try
                {
                    NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out int needed);
                    if (needed <= 0 || needed > 1 << 20) return null;
                    IntPtr buffer = Marshal.AllocHGlobal(needed);
                    try
                    {
                        if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, needed, out _) != 0) return null;
                        // UNICODE_STRING { ushort Length; ushort MaximumLength; PWSTR Buffer } – the text follows in the same buffer
                        int bytes = (ushort)Marshal.ReadInt16(buffer);
                        IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                        if (bytes <= 0 || text == IntPtr.Zero) return null;
                        long offset = text.ToInt64() - buffer.ToInt64();
                        if (offset < 0 || offset + bytes > needed) return null;   // must point into our own buffer
                        return Marshal.PtrToStringUni(text, bytes / 2);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
                catch
                {
                    return null;
                }
                finally
                {
                    CloseHandle(process);
                }
            }

            public static string[] SplitArgs(string commandLine)
            {
                IntPtr argv = CommandLineToArgvW(commandLine, out int count);
                if (argv == IntPtr.Zero) return Array.Empty<string>();
                try
                {
                    var args = new string[count];
                    for (int i = 0; i < count; i++)
                        args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? string.Empty;
                    return args;
                }
                finally
                {
                    LocalFree(argv);
                }
            }
        }
    }
}
