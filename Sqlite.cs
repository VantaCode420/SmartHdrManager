using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartHdrManager
{
    /// <summary>
    /// Minimal read-only SQLite access through winsqlite3.dll, the SQLite build that is part of Windows 10 and 11
    /// (no extra DLL or NuGet package). Used to read the install databases of the Amazon Games and itch.io apps.
    /// Loaded from System32 only, never from the app's folder.
    /// </summary>
    internal static class ReadOnlySqlite
    {
        private const string Dll = "winsqlite3.dll";
        private const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
        private const int SQLITE_OPEN_READONLY = 0x1;
        private const int BusyTimeoutMs = 2000;   // the launcher may be writing to its database right now

        [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr sqlite3_column_text(IntPtr stmt, int column);
        [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Dll), DefaultDllImportSearchPaths(DllImportSearchPath.System32)] private static extern IntPtr sqlite3_errmsg(IntPtr db);

        /// <summary>
        /// Runs <paramref name="sql"/> on the database file and returns every row as text (null for NULL values).
        /// The file is opened read-only: nothing in it is changed. For a database in WAL mode SQLite may still create its
        /// usual "-wal" and "-shm" side files, as the launcher itself does. Throws <see cref="IOException"/> when it cannot be opened or queried.
        /// </summary>
        public static List<string?[]> Query(string path, string sql)
        {
            var rows = new List<string?[]>();
            if (sqlite3_open_v2(Utf8(path), out IntPtr db, SQLITE_OPEN_READONLY, IntPtr.Zero) != SQLITE_OK)
            {
                string error = db == IntPtr.Zero ? "out of memory" : ErrorText(db);
                sqlite3_close_v2(db);
                throw new IOException($"{Path.GetFileName(path)} could not be opened: {error}");
            }

            try
            {
                sqlite3_busy_timeout(db, BusyTimeoutMs);
                if (sqlite3_prepare_v2(db, Utf8(sql), -1, out IntPtr stmt, IntPtr.Zero) != SQLITE_OK)
                    throw new IOException($"{Path.GetFileName(path)} could not be read: {ErrorText(db)}");
                try
                {
                    int columns = sqlite3_column_count(stmt);
                    int result;
                    while ((result = sqlite3_step(stmt)) == SQLITE_ROW)
                    {
                        var row = new string?[columns];
                        for (int i = 0; i < columns; i++)
                        {
                            IntPtr text = sqlite3_column_text(stmt, i);
                            row[i] = text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);
                        }
                        rows.Add(row);
                    }
                    if (result != SQLITE_DONE)
                        throw new IOException($"{Path.GetFileName(path)} could not be read: {ErrorText(db)}");
                }
                finally
                {
                    sqlite3_finalize(stmt);
                }
            }
            finally
            {
                sqlite3_close_v2(db);
            }
            return rows;
        }

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

        private static string ErrorText(IntPtr db) => Marshal.PtrToStringUTF8(sqlite3_errmsg(db)) ?? "unknown error";
    }
}
