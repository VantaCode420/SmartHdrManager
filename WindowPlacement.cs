using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// Saved size and position of one window (config.json › WindowPlacements, GitHub issue #13). Device pixels as
    /// Windows reported them at <see cref="Dpi"/>; the normal (not maximized) bounds.
    /// </summary>
    public sealed class WindowPlacement
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Dpi { get; set; } = 96;
        public bool Maximized { get; set; }

        public bool SameAs(WindowPlacement? o) =>
            o != null && o.X == X && o.Y == Y && o.Width == Width && o.Height == Height && o.Dpi == Dpi && o.Maximized == Maximized;
    }

    /// <summary>
    /// Makes a window remember its size and position across app restarts (GitHub issue #13). Restored when the window
    /// loads, saved when it is hidden or closed. A position that is no longer on any screen (monitor disconnected,
    /// resolution changed) falls back to the window's default place.
    /// </summary>
    internal static class WindowMemory
    {
        // Window keys in config.json › WindowPlacements
        public const string Main = "Main";
        public const string GameOptions = "GameOptions";
        public const string GamePrograms = "GamePrograms";
        public const string NeverHdr = "NeverHdr";
        public const string RenoDxConflicts = "RenoDxConflicts";
        public const string AddRunning = "AddRunningProgram";
        public const string Update = "Update";

        /// <summary>
        /// Call before the window is shown. <paramref name="minClient"/>: the smallest client size in 96-DPI pixels;
        /// null keeps a minimum size that is already set, or else uses the window's default size.
        /// </summary>
        public static void Remember(Form form, HdrEngine engine, string key, Size? minClient = null)
        {
            bool loaded = false, maximized = false;

            form.Load += (_, _) =>
            {
                if (minClient != null)
                {
                    Size min = form.LogicalToDeviceUnits(minClient.Value);   // + title bar and borders
                    form.MinimumSize = new Size(min.Width + form.Width - form.ClientSize.Width, min.Height + form.Height - form.ClientSize.Height);
                }
                else if (form.MinimumSize.IsEmpty)
                    form.MinimumSize = form.Size;

                WindowPlacement? saved = null;
                try { saved = engine.Read(c => c.WindowPlacements.TryGetValue(key, out var p) ? p : null); }
                catch { }
                if (saved != null) Restore(form, saved);
                maximized = form.WindowState == FormWindowState.Maximized;
                loaded = true;
            };
            form.Resize += (_, _) =>
            {
                if (form.WindowState != FormWindowState.Minimized) maximized = form.WindowState == FormWindowState.Maximized;
            };
            // Closing hides a dialog (and the main window, which only goes to the tray); Exit closes it for real
            form.VisibleChanged += (_, _) => { if (loaded && !form.Visible) Save(form, engine, key, maximized); };
            form.FormClosing += (_, e) => { if (loaded) Save(form, engine, key, maximized); };
        }

        private static void Save(Form form, HdrEngine engine, string key, bool maximized)
        {
            try
            {
                if (form.IsDisposed || !form.IsHandleCreated) return;
                Rectangle normal = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
                if (normal.Width <= 0 || normal.Height <= 0) return;
                var placement = new WindowPlacement
                {
                    X = normal.X, Y = normal.Y, Width = normal.Width, Height = normal.Height,
                    Dpi = form.DeviceDpi > 0 ? form.DeviceDpi : 96,
                    Maximized = maximized && form.MaximizeBox
                };
                if (engine.Read(c => placement.SameAs(c.WindowPlacements.TryGetValue(key, out var p) ? p : null))) return;
                engine.Update(c => c.WindowPlacements[key] = placement);
            }
            catch { }   // a window position is never worth an error
        }

        private static void Restore(Form form, WindowPlacement p)
        {
            if (p.Width <= 0 || p.Height <= 0 || p.Dpi <= 0) return;

            // Scaling changed since it was saved (or the window starts on a screen with another scaling): Windows rescales
            // the window by its new DPI when it moves, so the size is given in the DPI the window has now.
            double factor = form.DeviceDpi > 0 ? form.DeviceDpi / (double)p.Dpi : 1.0;
            var size = new Size((int)Math.Round(p.Width * factor), (int)Math.Round(p.Height * factor));
            size = new Size(Math.Max(size.Width, form.MinimumSize.Width), Math.Max(size.Height, form.MinimumSize.Height));
            var bounds = new Rectangle(p.X, p.Y, size.Width, size.Height);

            Screen? screen = ScreenShowing(form, bounds);
            if (screen == null)
            {
                // Off-screen (monitor disconnected, resolution changed): keep the size, default place
                Rectangle area = form.Owner != null && form.Owner.Visible ? Screen.FromControl(form.Owner).WorkingArea : Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(form).WorkingArea;
                bounds.Size = Fit(bounds.Size, area);
                Rectangle center = form.Owner != null && form.Owner.Visible && form.Owner.WindowState != FormWindowState.Minimized ? form.Owner.Bounds : area;
                bounds.Location = new Point(center.X + (center.Width - bounds.Width) / 2, center.Y + (center.Height - bounds.Height) / 2);
                bounds = KeepInside(bounds, area);
            }
            else
            {
                Rectangle area = screen.WorkingArea;
                bounds.Size = Fit(bounds.Size, area);
                bounds = KeepInside(bounds, area);
            }

            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = bounds;
            if (p.Maximized && form.MaximizeBox) form.WindowState = FormWindowState.Maximized;
        }

        /// <summary>The screen the window's title bar is on – enough of it to grab and move the window – or null.</summary>
        private static Screen? ScreenShowing(Form form, Rectangle bounds)
        {
            int titleHeight = form.LogicalToDeviceUnits(32), needWidth = Math.Min(bounds.Width, form.LogicalToDeviceUnits(120));
            var title = new Rectangle(bounds.X, bounds.Y, bounds.Width, titleHeight);
            Screen? best = null;
            int bestArea = 0;
            foreach (var s in Screen.AllScreens)
            {
                var visible = Rectangle.Intersect(s.WorkingArea, title);
                if (visible.Width < needWidth || visible.Height < titleHeight / 2) continue;
                int area = visible.Width * visible.Height;
                if (area > bestArea) { best = s; bestArea = area; }
            }
            return best;
        }

        private static Size Fit(Size size, Rectangle area) => new(Math.Min(size.Width, area.Width), Math.Min(size.Height, area.Height));

        private static Rectangle KeepInside(Rectangle r, Rectangle area)
        {
            int x = Math.Clamp(r.X, area.Left, Math.Max(area.Left, area.Right - r.Width));
            int y = Math.Clamp(r.Y, area.Top, Math.Max(area.Top, area.Bottom - r.Height));
            return new Rectangle(x, y, r.Width, r.Height);
        }

        /// <summary>config.json clean-up: case-insensitive keys, no empty or impossible entries.</summary>
        public static Dictionary<string, WindowPlacement> Normalize(Dictionary<string, WindowPlacement>? placements)
        {
            var result = new Dictionary<string, WindowPlacement>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in placements ?? new Dictionary<string, WindowPlacement>())
            {
                var p = kv.Value;
                if (string.IsNullOrWhiteSpace(kv.Key) || p == null) continue;
                if (p.Width <= 0 || p.Height <= 0 || p.Width > 32768 || p.Height > 32768 || p.Dpi < 48 || p.Dpi > 960) continue;
                result[kv.Key.Trim()] = p;
            }
            return result;
        }
    }
}
