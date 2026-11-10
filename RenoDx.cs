using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmartHdrManager
{
    /// <summary>
    /// Which RenoDX add-ons are HDR mods. RenoDX names every add-on renodx-&lt;name&gt;.addon64 (or .addon32), HDR mods and
    /// tools alike, so tools are recognised by name. No Windows dependency, so it is unit-tested.
    /// </summary>
    internal static class RenoDxAddons
    {
        // Known tools (checked 2026-11): the official RenoDX utilities (src/addons) and community tools with RenoDX names
        private static readonly HashSet<string> Tools = new(StringComparer.OrdinalIgnoreCase)
        {
            "devkit", "dlssfix", "fpslimiter",                 // official: developer kit, DLSS frame-generation fix, FPS limiter
            "dlss", "dlss5", "mfgunlock", "universal_ue-dof-fix"   // DLSS tools, frame-generation unlock, Unreal depth-of-field fix
        };

        // Parts of a name (split at "-" and "_") that mark a tool, for tools not known yet
        private static readonly HashSet<string> ToolParts = new(StringComparer.OrdinalIgnoreCase)
        {
            "mfg", "dof", "fov", "devkit", "fpslimiter", "limiter", "framegen"
        };

        /// <summary>renodx-*.addon, .addon64 or .addon32 – the extensions ReShade loads.</summary>
        public static bool IsAddonFile(string fileName)
        {
            string name = Path.GetFileName(fileName);
            if (!name.StartsWith("renodx", StringComparison.OrdinalIgnoreCase)) return false;
            string ext = Path.GetExtension(name);
            return ext.Equals(".addon", StringComparison.OrdinalIgnoreCase) || ext.Equals(".addon64", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".addon32", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>"renodx-cp2077.addon64" -> "cp2077", "renodx-_ksp.addon64" -> "_ksp".</summary>
        public static string NameOf(string fileName)
        {
            string stem = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName));
            if (stem.StartsWith("renodx", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(6);
            if (stem.StartsWith("-")) stem = stem.Substring(1);
            return stem;
        }

        /// <summary>True for RenoDX tools that have nothing to do with HDR (FPS limiter, DLSS / frame-generation tools, fixes).</summary>
        public static bool IsTool(string name)
        {
            if (Tools.Contains(name)) return true;
            foreach (string part in name.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("dlss", StringComparison.OrdinalIgnoreCase) || part.Contains("unlock", StringComparison.OrdinalIgnoreCase) ||
                    ToolParts.Contains(part))
                    return true;
            }
            return false;
        }

        /// <summary>The [ADDON] settings of a ReShade.ini that decide which add-ons ReShade loads.</summary>
        public sealed class ReShadeSettings
        {
            public string? AddonPath;                                                              // relative to the ini's folder, or absolute
            public HashSet<string> DisabledFiles = new(StringComparer.OrdinalIgnoreCase);       // file names
        }

        /// <summary>
        /// Reads [ADDON] AddonPath and DisabledAddons (entries "Name@file.addon64" or "@file.addon64", separated by commas).
        /// Entries without "@" name an add-on by its internal name, which can't be known without loading it: ignored.
        /// </summary>
        public static ReShadeSettings ParseReShadeIni(string text)
        {
            var settings = new ReShadeSettings();
            string section = string.Empty;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    int close = line.IndexOf(']');
                    section = close > 1 ? line.Substring(1, close - 1).Trim() : string.Empty;
                    continue;
                }
                if (!section.Equals("ADDON", StringComparison.OrdinalIgnoreCase)) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim().Trim('"');
                if (key.Equals("AddonPath", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Length > 0) settings.AddonPath = value;
                }
                else if (key.Equals("DisabledAddons", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        int at = entry.LastIndexOf('@');
                        if (at >= 0 && at < entry.Length - 1) settings.DisabledFiles.Add(entry.Substring(at + 1).Trim());
                    }
                }
            }
            return settings;
        }
    }
}
