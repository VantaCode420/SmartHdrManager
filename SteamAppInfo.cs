using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SmartHdrManager
{
    /// <summary>
    /// Reads the launch options of installed Steam games from Steam's local app cache (steam\appcache\appinfo.vdf),
    /// so the app watches the exe that Steam really starts instead of guessing it from the install folder.
    /// File format: a header (magic + version 39–41, universe, and from version 41 the offset of a string table that
    /// holds every key name), then one record per app with its data as binary KeyValues – the layout used by
    /// ValveResourceFormat's SteamAppInfo parser. Read-only; any other version is ignored (the caller falls back).
    /// </summary>
    internal static class SteamAppInfo
    {
        private const uint Magic = 0x07_56_44;   // the three upper bytes of the first uint32; the low byte is the version
        private const int MaxStringBytes = 64 * 1024;

        // Launch option types that start something other than the game itself
        private static readonly HashSet<string> SkippedLaunchTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "server", "editor", "manual", "config", "benchmark", "openvroverlay"
        };

        /// <summary>One app from appinfo.vdf: its type ("game", "application", "tool", …) and Windows launch executables.</summary>
        public sealed class App
        {
            public string Type = string.Empty;
            public List<string> Executables = new();
        }

        private sealed class Node
        {
            public string? Value;
            public Dictionary<string, Node>? Children;

            public Node? this[string key] => Children != null && Children.TryGetValue(key, out var n) ? n : null;
            public string Text(string key) => this[key]?.Value ?? string.Empty;
        }

        /// <summary>
        /// Type and Windows launch executables (relative to the game's install folder, default option first) of the given
        /// apps. Apps missing from the file are left out. Throws <see cref="InvalidDataException"/> for an unknown format.
        /// </summary>
        public static Dictionary<uint, App> ReadApps(string path, ICollection<uint> appIds)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return ReadApps(fs, appIds);
        }

        public static Dictionary<uint, App> ReadApps(Stream stream, ICollection<uint> appIds)
        {
            var result = new Dictionary<uint, App>();
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

            uint magic = reader.ReadUInt32();
            uint version = magic & 0xFF;
            if (magic >> 8 != Magic || version < 39 || version > 41)
                throw new InvalidDataException($"Unknown appinfo.vdf format (0x{magic:X8}).");
            reader.ReadUInt32();   // universe

            string[]? strings = null;
            if (version >= 41)
            {
                long tableOffset = reader.ReadInt64();
                long dataStart = stream.Position;
                stream.Position = tableOffset;
                uint count = reader.ReadUInt32();
                if (count > 1_000_000) throw new InvalidDataException("appinfo.vdf: string table too large.");
                strings = new string[count];
                for (int i = 0; i < count; i++) strings[i] = ReadString(reader);
                stream.Position = dataStart;
            }

            int remaining = appIds.Count;
            while (remaining > 0)
            {
                uint appId = reader.ReadUInt32();
                if (appId == 0) break;   // end of the app records
                uint size = reader.ReadUInt32();
                long end = stream.Position + size;

                if (appIds.Contains(appId))
                {
                    remaining--;
                    // info state, last updated, PICS token, SHA-1 of the text data, change number (+ SHA-1 of the binary data)
                    stream.Position += 4 + 4 + 8 + 20 + 4 + (version >= 40 ? 20 : 0);
                    var root = ReadDocument(reader, strings);
                    result[appId] = new App
                    {
                        Type = root["appinfo"]?["common"]?.Text("type").Trim().ToLowerInvariant() ?? string.Empty,
                        Executables = LaunchExecutables(root)
                    };
                }
                stream.Position = end;
            }
            return result;
        }

        /// <summary>appinfo › config › launch › 0, 1, …: Windows options for everyone (no beta branch), default first.</summary>
        private static List<string> LaunchExecutables(Node root)
        {
            var launch = root["appinfo"]?["config"]?["launch"]?.Children;
            if (launch == null) return new List<string>();

            var options = new List<(string Exe, bool Default)>();
            foreach (var option in launch.OrderBy(kv => int.TryParse(kv.Key, out int n) ? n : int.MaxValue).Select(kv => kv.Value))
            {
                string exe = option.Text("executable").Trim().Replace('/', '\\').TrimStart('\\');
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || exe.Contains("..")) continue;

                string type = option.Text("type");
                if (SkippedLaunchTypes.Contains(type)) continue;

                var config = option["config"];
                string os = config?.Text("oslist") ?? string.Empty;
                if (os.Length > 0 && !os.Contains("windows", StringComparison.OrdinalIgnoreCase)) continue;
                if (!ForPublicBranch(config?.Text("betakey") ?? string.Empty)) continue;

                options.Add((exe, type.Length == 0 || type.Equals("default", StringComparison.OrdinalIgnoreCase)));
            }

            return options.OrderByDescending(o => o.Default)
                          .Select(o => o.Exe)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();
        }

        /// <summary>
        /// True when an option's "betakey" (the branches it is for, separated by spaces or commas) includes the public
        /// branch: empty, "public" or "default".
        /// </summary>
        private static bool ForPublicBranch(string betaKey)
        {
            var branches = betaKey.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            return branches.Length == 0 || branches.Any(b => b.Equals("public", StringComparison.OrdinalIgnoreCase) ||
                                                              b.Equals("default", StringComparison.OrdinalIgnoreCase));
        }

        private static Node ReadDocument(BinaryReader reader, string[]? strings)
        {
            var root = new Node { Children = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase) };
            ReadChildren(reader, strings, root, depth: 0);
            return root;
        }

        private static void ReadChildren(BinaryReader reader, string[]? strings, Node parent, int depth)
        {
            if (depth > 32) throw new InvalidDataException("appinfo.vdf: nesting too deep.");
            while (true)
            {
                byte type = reader.ReadByte();
                if (type == 8 || type == 11) return;   // end of this object

                string key = strings != null ? Lookup(strings, reader.ReadInt32()) : ReadString(reader);
                var node = new Node();
                switch (type)
                {
                    case 0:   // object
                        node.Children = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
                        ReadChildren(reader, strings, node, depth + 1);
                        break;
                    case 1:   // UTF-8 string
                        node.Value = ReadString(reader);
                        break;
                    case 2: case 4: case 6:   // int32, pointer, color
                        node.Value = reader.ReadInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case 3:   // float32
                        node.Value = reader.ReadSingle().ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case 5:   // UTF-16 string: int32 character count including the terminator
                        int chars = reader.ReadInt32();
                        if (chars < 0 || chars > MaxStringBytes) throw new InvalidDataException("appinfo.vdf: bad wide string.");
                        node.Value = Encoding.Unicode.GetString(reader.ReadBytes(chars * 2)).TrimEnd('\0');
                        break;
                    case 7:   // uint64
                        node.Value = reader.ReadUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case 9:   // binary blob
                        uint length = reader.ReadUInt32();
                        reader.BaseStream.Position += length;
                        break;
                    case 10:   // int64
                        node.Value = reader.ReadInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    default:
                        throw new InvalidDataException($"appinfo.vdf: unknown value type {type}.");
                }
                parent.Children![key] = node;
            }
        }

        private static string Lookup(string[] strings, int index) =>
            index >= 0 && index < strings.Length ? strings[index] : throw new InvalidDataException("appinfo.vdf: bad string index.");

        private static string ReadString(BinaryReader reader)
        {
            var bytes = new List<byte>(32);
            byte b;
            while ((b = reader.ReadByte()) != 0)
            {
                if (bytes.Count >= MaxStringBytes) throw new InvalidDataException("appinfo.vdf: string too long.");
                bytes.Add(b);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }
}
