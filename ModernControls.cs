using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SmartHdrManager
{
    // =========================================================================
    // Small owner-drawn controls for a Windows 11 look. All drawing scales with the control size,
    // so they follow the form's DPI scaling automatically.
    // =========================================================================

    /// <summary>Panel with rounded corners. BackColor is the card colour; the corners show the parent colour.</summary>
    public class RoundedPanel : Panel
    {
        private int _radius = 10;
        private Color _borderColor = Color.Empty;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }

        /// <summary>Corner radius in 96-DPI pixels (scaled to the display DPI when drawing).</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Radius
        {
            get => _radius;
            set { _radius = value; Invalidate(); }
        }

        protected float ScaledRadius => LogicalToDeviceUnits(_radius);

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color BorderColor
        {
            get => _borderColor;
            set { _borderColor = value; Invalidate(); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            using var brush = new SolidBrush(BackColor);

            // The inside is one plain colour: filled without anti-aliasing (fast). Only a strip along the edges – the
            // rounded corners and the border – is drawn anti-aliased; that used to cover the whole card, which made
            // resizing the window slow on large high-DPI cards. Same pixels as before.
            int edge = (int)Math.Ceiling(ScaledRadius) + 2;
            var inner = Rectangle.FromLTRB(edge, edge, Width - edge, Height - edge);
            bool hasInner = inner.Width > 0 && inner.Height > 0;
            if (hasInner) g.FillRectangle(brush, inner);

            var state = g.Save();
            if (hasInner) g.ExcludeClip(inner);
            g.Clear(Parent?.BackColor ?? Theme.Back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), ScaledRadius);
            g.FillPath(brush, path);
            if (!_borderColor.IsEmpty)
            {
                using var pen = new Pen(_borderColor);
                g.DrawPath(pen, path);
            }
            g.Restore(state);
            g.SmoothingMode = SmoothingMode.AntiAlias;   // as before, for anything drawn after the background
        }
    }

    /// <summary>A line of text drawn by a <see cref="SettingCard"/> – set like a label's Text / ForeColor.</summary>
    public sealed class SettingText
    {
        private readonly Control _owner;
        private string _text;
        private Color _color;

        internal SettingText(Control owner, string text, Color color)
        {
            _owner = owner;
            _text = text;
            _color = color;
        }

        public string Text
        {
            get => _text;
            set { if (_text == (value ?? string.Empty)) return; _text = value ?? string.Empty; _owner.Invalidate(); }
        }

        public Color ForeColor
        {
            get => _color;
            set { if (_color == value) return; _color = value; _owner.Invalidate(); }
        }

        public bool IsDisposed => _owner.IsDisposed;
    }

    /// <summary>
    /// A settings row: title and description on the left, one control on the right. The two text lines are drawn by
    /// the card itself instead of being two Label windows – with dozens of rows, moving those windows on every step made
    /// resizing the window slow. Text that doesn't fit ends in "…" and shows in full as a tooltip, like a label.
    /// </summary>
    public sealed class SettingCard : RoundedPanel
    {
        // A label's flags (TopLeft, AutoEllipsis, no mnemonics): looks exactly like the labels it replaces
        private const TextFormatFlags TextFlags = TextFormatFlags.Top | TextFormatFlags.Left | TextFormatFlags.WordBreak |
                                                  TextFormatFlags.TextBoxControl | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        private static readonly ToolTip FullText = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 20000 };

        private readonly Control _control;
        private Font? _titleFont;          // Semibold at the scale of this card's Font (Theme.Small, DPI-scaled by WinForms)
        private Rectangle _titleRect, _descRect;
        private int _titleHeight, _descHeight;   // one line of each font (measured once per font)
        private string? _tip;

        public SettingText Title { get; }
        public SettingText Description { get; }

        public SettingCard(string title, string description, Control control)
        {
            Title = new SettingText(this, title, Theme.Text);
            Description = new SettingText(this, description, Theme.Muted);
            _control = control;
            Controls.Add(control);
            Font = Theme.Small;
        }

        private Font TitleFont
        {
            get
            {
                if (_titleFont != null) return _titleFont;
                float scale = Font.SizeInPoints / Theme.Small.SizeInPoints;
                _titleFont = Math.Abs(scale - 1f) < 0.001f
                    ? Theme.Semibold
                    : new Font(Theme.Semibold.FontFamily, Theme.Semibold.SizeInPoints * scale, Theme.Semibold.Style, GraphicsUnit.Point);
                return _titleFont;
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            DropTitleFont();
            base.OnFontChanged(e);
            PerformLayout();
            Invalidate();
        }

        private void DropTitleFont()
        {
            if (_titleFont != null && !ReferenceEquals(_titleFont, Theme.Semibold)) _titleFont.Dispose();
            _titleFont = null;
            _titleHeight = _descHeight = 0;
        }

        private static int LineHeight(Font font) =>
            TextRenderer.MeasureText("Ag", font, new Size(int.MaxValue, int.MaxValue), TextFlags).Height;

        // Explicit layout (DPI-safe): control on the right, the text uses the remaining width
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_control == null) return;   // the base constructor can already lay out
            int S(int v) => LogicalToDeviceUnits(v);
            int w = ClientSize.Width, h = ClientSize.Height;
            _control.Location = new Point(w - S(18) - _control.Width, (h - _control.Height) / 2);

            int textWidth = Math.Max(0, _control.Left - S(16) - S(18));
            if (_titleHeight == 0) { _titleHeight = LineHeight(TitleFont); _descHeight = LineHeight(Font); }
            int titleHeight = _titleHeight, descHeight = _descHeight;
            int top = Math.Max(0, (h - titleHeight - descHeight) / 2);
            var title = new Rectangle(S(18), top, textWidth, titleHeight);
            var desc = new Rectangle(S(18), top + titleHeight, textWidth, descHeight);
            if (title == _titleRect && desc == _descRect) return;
            _titleRect = title;
            _descRect = desc;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            if (_titleRect.Width > 0)
                TextRenderer.DrawText(g, Title.Text, TitleFont, _titleRect, Title.ForeColor, TextFlags);
            if (_descRect.Width > 0)
                TextRenderer.DrawText(g, Description.Text, Font, _descRect, Description.ForeColor, TextFlags);
        }

        private static bool Cut(string text, Font font, Rectangle rect) =>
            text.Length > 0 && TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width > rect.Width;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            string? tip = _titleRect.Contains(e.Location) && Cut(Title.Text, TitleFont, _titleRect) ? Title.Text
                        : _descRect.Contains(e.Location) && Cut(Description.Text, Font, _descRect) ? Description.Text
                        : null;
            if (tip == _tip) return;
            _tip = tip;
            FullText.SetToolTip(this, tip);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_tip == null) return;
            _tip = null;
            FullText.SetToolTip(this, null);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FullText.SetToolTip(this, null);
                DropTitleFont();
            }
            base.Dispose(disposing);
        }
    }

    public enum HdrVisualState { Off, On, Paused }

    /// <summary>
    /// Status card with a coloured dot. When HDR turns on, a soft green tint sweeps across the card from left
    /// to right and the dot fades from grey to green with a glow; when HDR turns off it fades back.
    /// Children should use a transparent BackColor so the sweep shows behind them.
    /// </summary>
    public sealed class StatusCard : RoundedPanel
    {
        private const int SweepInDelayMs = 1500;   // wait until the monitor is back from the HDR mode switch (black screen)
        private const int SweepInMs = 2200;        // slow, calm sweep from left to right
        private const int SweepOutMs = 600;

        private static readonly Color DotOn = Color.FromArgb(255, 214, 170);

        private static readonly Color[] HdrGradient =
        {
            Color.FromArgb(132, 76, 236),    // violet
            Color.FromArgb(232, 96, 132),    // pink
            Color.FromArgb(255, 170, 110)    // warm orange
        };

        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
        private readonly System.Diagnostics.Stopwatch _clock = new();
        private HdrVisualState _state = HdrVisualState.Off;
        private float _sweep;          // 0 = no tint, 1 = fully tinted
        private float _from, _to;
        private int _durationMs;
        private int _delayMs;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int IndicatorX { get; set; } = 26;

        public StatusCard()
        {
            _timer.Tick += (_, _) => Animate();
        }

        public void SetState(HdrVisualState state)
        {
            if (state == _state) return;
            _state = state;

            float target = state == HdrVisualState.On ? 1f : 0f;
            if (Math.Abs(target - _sweep) < 0.001f || !IsHandleCreated || !Visible)
            {
                // Already there, or hidden (no animation). Also stops a sweep that is still waiting for its start
                // delay: HDR on -> off within 1.5 s used to let that sweep run on and colour the card while HDR was off.
                _timer.Stop();
                _sweep = _from = _to = target;
                Invalidate(true);
                return;
            }

            _from = _sweep;
            _to = target;
            bool sweepingIn = target > _sweep;
            _durationMs = sweepingIn ? SweepInMs : SweepOutMs;
            _delayMs = sweepingIn ? SweepInDelayMs : 0;
            _clock.Restart();
            _timer.Start();
        }

        private void Animate()
        {
            double elapsed = _clock.Elapsed.TotalMilliseconds - _delayMs;
            if (elapsed < 0) return;   // still waiting for the display to come back after switching to HDR
            float t = Math.Min(1f, (float)elapsed / Math.Max(1, _durationMs));
            // ease-in-out cubic: starts and ends gently
            float eased = t < 0.5f ? 4f * t * t * t : 1f - (float)Math.Pow(-2f * t + 2f, 3) / 2f;
            _sweep = _from + (_to - _from) * eased;
            if (t >= 1f)
            {
                _sweep = _to;
                _timer.Stop();
            }
            Invalidate(true);   // children are transparent, so they repaint the new background too
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // HDR gradient (violet -> pink -> warm orange, like the app icon) revealed from left to right.
            // The colours stay fixed relative to the card; the sweep only changes how much of it is visible,
            // with a feathered leading edge. Built from many colour stops for a smooth result.
            if (_sweep > 0.001f && Width > 2 && Height > 2)
            {
                var state = g.Save();
                using (var clip = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), ScaledRadius))
                    g.SetClip(clip);

                float soft = Width * 0.35f;
                float edge = _sweep * (Width + soft);
                const int stops = 32;
                var colors = new Color[stops];
                var positions = new float[stops];

                for (int i = 0; i < stops; i++)
                {
                    float p = i / (stops - 1f);
                    float x = p * Width;

                    Color baseColor = p < 0.5f
                        ? Theme.Blend(HdrGradient[0], HdrGradient[1], p * 2f)
                        : Theme.Blend(HdrGradient[1], HdrGradient[2], (p - 0.5f) * 2f);
                    float alpha = 120f - 45f * p;   // a bit stronger on the left, softer on the right

                    float reveal;
                    if (x <= edge - soft) reveal = 1f;
                    else if (x >= edge) reveal = 0f;
                    else
                    {
                        float t = (edge - x) / soft;
                        reveal = t * t * (3f - 2f * t);   // smoothstep
                    }

                    colors[i] = Color.FromArgb((int)(alpha * reveal), baseColor);
                    positions[i] = p;
                }

                using (var brush = new LinearGradientBrush(new PointF(-1f, 0), new PointF(Width + 1f, 0), colors[0], colors[stops - 1]))
                {
                    brush.InterpolationColors = new ColorBlend { Colors = colors, Positions = positions };
                    g.FillRectangle(brush, 0, 0, Width, Height);
                }
                g.Restore(state);
            }

            // Status dot: grey -> warm light (with a warm glow) following the sweep; amber when paused
            float d = Math.Max(10f, Height * 0.15f);
            float cx = LogicalToDeviceUnits(IndicatorX);   // IndicatorX is in 96-DPI pixels
            float cy = Height / 2f;

            Color dot = _state == HdrVisualState.Paused
                ? Theme.Warn
                : Theme.Blend(Theme.Idle, DotOn, Math.Clamp(_sweep * 1.6f, 0f, 1f));
            float glow = _state == HdrVisualState.Paused ? 0f : Math.Clamp((_sweep - 0.3f) / 0.7f, 0f, 1f);

            if (glow > 0f)
            {
                for (int i = 3; i >= 1; i--)
                {
                    float gd = d + i * d * 0.6f;
                    using var glowBrush = new SolidBrush(Color.FromArgb((int)(34 * glow), HdrGradient[2]));
                    g.FillEllipse(glowBrush, cx - gd / 2, cy - gd / 2, gd, gd);
                }
            }

            using var brushDot = new SolidBrush(dot);
            g.FillEllipse(brushDot, cx - d / 2, cy - d / 2, d, d);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Windows 11 style on/off switch with a short animation.</summary>
    public sealed class ToggleSwitch : Control
    {
        private bool _checked;
        private float _position;   // 0 = off, 1 = on
        private bool _hover;
        private readonly System.Windows.Forms.Timer _animation = new() { Interval = 15 };

        public event EventHandler? CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // every click toggles, even quick double-taps
            Size = new Size(44, 22);
            Cursor = Cursors.Hand;
            TabStop = true;

            _animation.Tick += (_, _) =>
            {
                float target = _checked ? 1f : 0f;
                _position += (target - _position) * 0.35f;
                if (Math.Abs(target - _position) < 0.02f)
                {
                    _position = target;
                    _animation.Stop();
                }
                Invalidate();
            };
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get => _checked;
            set
            {
                if (_checked == value) return;
                _checked = value;
                if (IsHandleCreated && Visible)
                {
                    _animation.Start();
                }
                else
                {
                    _position = value ? 1f : 0f;
                    Invalidate();
                }
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            Focus();
            Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                Checked = !Checked;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawSwitch(g, new RectangleF(1, 1, Width - 3, Height - 3), _position, _hover, Enabled);

            if (Focused && ShowFocusCues)
            {
                using var pen = new Pen(Color.FromArgb(120, Theme.Text)) { DashStyle = DashStyle.Dot };
                using var path = Theme.RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f);
                g.DrawPath(pen, path);
            }
        }

        /// <summary>Shared drawing so the games list can draw identical switches inside its rows.</summary>
        public static void DrawSwitch(Graphics g, RectangleF track, float position, bool hover, bool enabled)
        {
            float radius = track.Height / 2f;
            Color offTrack = hover ? Theme.ButtonHover : Theme.ButtonBack;
            Color onTrack = hover ? Theme.AccentHover : Theme.Accent;
            Color fill = Theme.Blend(offTrack, onTrack, position);
            if (!enabled) fill = Color.FromArgb(90, fill);

            using (var path = Theme.RoundRect(track, radius))
            {
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, path);

                if (position < 0.5f)
                {
                    using var pen = new Pen(Theme.Blend(Theme.Muted, onTrack, position * 2f));
                    g.DrawPath(pen, path);
                }
            }

            float inset = track.Height * 0.2f;
            float d = track.Height - inset * 2f;
            float x = track.X + inset + position * (track.Width - d - inset * 2f);
            Color thumb = Theme.Blend(Theme.Muted, Theme.OnAccent, position);
            using var thumbBrush = new SolidBrush(enabled ? thumb : Color.FromArgb(120, thumb));
            g.FillEllipse(thumbBrush, x, track.Y + inset, d, d);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _animation.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Rounded button with an optional icon glyph. Primary = filled with the accent colour.</summary>
    public sealed class ModernButton : Control
    {
        private bool _hover;
        private bool _down;
        private bool _primary;
        private string? _glyph;

        public ModernButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // quick repeated clicks (e.g. stepper +/-) all count
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            Font = Theme.Body;
            Size = new Size(120, 34);
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Primary
        {
            get => _primary;
            set { _primary = value; Invalidate(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? Glyph
        {
            get => _glyph;
            set { _glyph = value; UpdateAutoSize(); Invalidate(); }
        }

        /// <summary>
        /// AutoSize: the button is at least as wide as this text needs (the longest text it can show), so it keeps its
        /// width when the text changes. Layout engines only use the preferred size – MinimumSize is not enough.
        /// </summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? SizingText
        {
            get => _sizingText;
            set { _sizingText = value; UpdateAutoSize(); Invalidate(); }
        }
        private string? _sizingText;

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size text = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding);
            if (!string.IsNullOrEmpty(_sizingText))
            {
                Size sizing = TextRenderer.MeasureText(_sizingText, Font, Size.Empty, TextFormatFlags.NoPadding);
                if (sizing.Width > text.Width) text = new Size(sizing.Width, Math.Max(text.Height, sizing.Height));
            }
            int glyphWidth = string.IsNullOrEmpty(_glyph) ? 0 : Font.Height + (text.Width > 0 ? Font.Height / 3 : 0);
            int padding = Font.Height;
            int width = Math.Max(Font.Height * 2 + 4, text.Width + glyphWidth + padding * 2);
            int height = Font.Height + 16;
            return new Size(width, height);
        }

        private void UpdateAutoSize()
        {
            if (AutoSize) Size = GetPreferredSize(Size.Empty);
        }

        protected override void OnTextChanged(EventArgs e) { UpdateAutoSize(); Invalidate(); base.OnTextChanged(e); }
        protected override void OnFontChanged(EventArgs e) { UpdateAutoSize(); base.OnFontChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _down = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        /// <summary>First opaque background colour up the parent chain (BackColor may be transparent).</summary>
        private Color EffectiveBack()
        {
            Control? c = this;
            while (c != null && c.BackColor.A < 255) c = c.Parent;
            return c?.BackColor ?? Theme.Back;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // The background (BackColor, or the parent when transparent) is already painted by OnPaintBackground
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color back = EffectiveBack();

            Color fill, text, border;
            if (_primary)
            {
                fill = _down ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent;
                text = Theme.OnAccent;
                border = fill;
            }
            else
            {
                fill = _down ? Theme.ButtonPressed : _hover ? Theme.ButtonHover : Theme.ButtonBack;
                text = Theme.Text;
                border = Theme.Border;
            }
            if (!Enabled)
            {
                fill = Theme.Blend(fill, back, 0.55f);
                text = Theme.Muted;
                border = Theme.Blend(border, back, 0.55f);
            }

            float radius = Math.Max(4f, Height * 0.18f);
            using (var path = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), radius))
            {
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
                using (var pen = new Pen(border))
                    g.DrawPath(pen, path);
            }

            // Icon + text, centred together
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            Size textSize = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(g, Text, Font, Size.Empty, flags);
            int glyphSize = string.IsNullOrEmpty(_glyph) ? 0 : Font.Height;
            int gap = glyphSize > 0 && textSize.Width > 0 ? Font.Height / 3 : 0;
            int total = glyphSize + gap + textSize.Width;
            int x = (Width - total) / 2;

            if (glyphSize > 0)
            {
                TextRenderer.DrawText(g, _glyph, Theme.IconFont, new Rectangle(x, 0, glyphSize, Height), text,
                    flags | TextFormatFlags.HorizontalCenter);
                x += glyphSize + gap;
            }
            if (textSize.Width > 0)
            {
                TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, textSize.Width + 2, Height), text, flags);
            }

            if (Focused && ShowFocusCues)
            {
                using var pen = new Pen(Color.FromArgb(150, Theme.Text)) { DashStyle = DashStyle.Dot };
                using var path = Theme.RoundRect(new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f), radius - 2);
                g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>Segmented tab bar ("pill" style).</summary>
    public sealed class SegmentedTabs : Control
    {
        private readonly List<string> _items = new();
        private int _selected;
        private int _hover = -1;

        public event EventHandler? SelectedIndexChanged;

        public SegmentedTabs()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Body;
            Cursor = Cursors.Hand;
            Size = new Size(360, 40);
        }

        public void AddTab(string text)
        {
            _items.Add(text);
            UpdateWidth();
            Invalidate();
        }

        private bool _sizing;

        private float OuterPad => Height * 0.1f;

        // Each segment is as wide as its (semibold) label plus padding, so the labels never touch
        private int SegmentWidth(string text) =>
            TextRenderer.MeasureText(text, Theme.Semibold, Size.Empty, TextFormatFlags.NoPadding).Width + (int)(Height * 0.9f);

        /// <summary>The control sizes its width to the tabs (it follows height changes, e.g. from DPI scaling).</summary>
        private void UpdateWidth()
        {
            if (_sizing || _items.Count == 0) return;
            _sizing = true;
            try
            {
                int total = (int)(OuterPad * 2f);
                foreach (var item in _items) total += SegmentWidth(item);
                if (Width != total) Width = total;
            }
            finally { _sizing = false; }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateWidth();
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => _selected;
            set
            {
                if (value < 0 || value >= _items.Count || value == _selected) return;
                _selected = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private RectangleF SegmentRect(int index)
        {
            float pad = OuterPad;
            float x = pad;
            for (int i = 0; i < index; i++) x += SegmentWidth(_items[i]);
            return new RectangleF(x, pad, SegmentWidth(_items[index]), Height - pad * 2f);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _items.Count; i++)
                if (SegmentRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h >= 0) SelectedIndex = h;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var track = Theme.RoundRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Height * 0.25f))
            using (var brush = new SolidBrush(Theme.Surface))
                g.FillPath(brush, track);

            for (int i = 0; i < _items.Count; i++)
            {
                RectangleF r = SegmentRect(i);
                bool selected = i == _selected;

                if (selected || i == _hover)
                {
                    using var path = Theme.RoundRect(r, r.Height * 0.25f);
                    using var brush = new SolidBrush(selected ? Theme.ButtonHover : Theme.SurfaceHover);
                    g.FillPath(brush, path);
                }

                if (selected)
                {
                    // small accent bar under the selected label
                    float barW = Math.Min(r.Width * 0.3f, LogicalToDeviceUnits(28));
                    float barH = Math.Max(2f, Height * 0.07f);
                    using var accent = Theme.RoundRect(new RectangleF(r.X + (r.Width - barW) / 2f, r.Bottom - barH - r.Height * 0.12f, barW, barH), barH / 2f);
                    using var accentBrush = new SolidBrush(Theme.Accent);
                    g.FillPath(accentBrush, accent);
                }

                TextRenderer.DrawText(g, _items[i], selected ? Theme.Semibold : Font, Rectangle.Round(r),
                    selected ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    /// <summary>
    /// "−  value  +" number input (replaces NumericUpDown, whose spin buttons cannot be themed).
    /// Drawn as ONE control without child controls: everything is computed from the current size when painting,
    /// so DPI scaling can never position or size its parts twice (which happened with child buttons).
    /// Keyboard: Left/Down = −1, Right/Up = +1.
    /// </summary>
    public sealed class Stepper : Control
    {
        private enum Part { None, Minus, Plus }

        private int _current;
        private Part _hover = Part.None;
        private Part _pressed = Part.None;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Minimum { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Maximum { get; set; } = 30;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Suffix { get; set; } = " s";
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Step { get; set; } = 1;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string? ZeroText { get; set; }   // e.g. "Off" instead of "0 s"
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int[]? Stops { get; set; }       // optional fixed values (ascending): − / + jump to the previous / next one
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Func<int, string>? Format { get; set; }   // optional display text, e.g. 120 -> "2 min"

        private int NextValue(bool up)
        {
            if (Stops == null || Stops.Length == 0) return _current + (up ? Step : -Step);
            if (up)
            {
                foreach (int v in Stops) if (v > _current) return v;
                return Maximum;
            }
            for (int i = Stops.Length - 1; i >= 0; i--) if (Stops[i] < _current) return Stops[i];
            return Minimum;
        }

        public event EventHandler? ValueChanged;

        public Stepper()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardDoubleClick, false);   // fast repeated clicks all count
            Size = new Size(130, 34);
            Font = Theme.Body;
            TabStop = true;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _current;
            set
            {
                int v = Math.Clamp(value, Minimum, Maximum);
                if (v == _current) return;
                _current = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private Rectangle MinusRect => new(0, 0, Height, Height);
        private Rectangle PlusRect => new(Math.Max(0, Width - Height), 0, Height, Height);

        private Part HitTest(Point p) =>
            MinusRect.Contains(p) ? Part.Minus : PlusRect.Contains(p) ? Part.Plus : Part.None;

        private bool IsEnabled(Part part) =>
            Enabled && (part == Part.Minus ? _current > Minimum : part == Part.Plus && _current < Maximum);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Part h = HitTest(e.Location);
            if (h != _hover)
            {
                _hover = h;
                Cursor = h != Part.None && IsEnabled(h) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = Part.None;
            _pressed = Part.None;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                Focus();
                _pressed = HitTest(e.Location);
                Invalidate();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            Part released = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && released == _pressed && IsEnabled(released))
                Value = NextValue(released == Part.Plus);
            _pressed = Part.None;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Left or Keys.Down) { Value = NextValue(false); e.Handled = true; }
            else if (e.KeyCode is Keys.Right or Keys.Up) { Value = NextValue(true); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            DrawPart(g, MinusRect, "\u2212", Part.Minus);
            DrawPart(g, PlusRect, "+", Part.Plus);

            var valueRect = new Rectangle(Height, 0, Math.Max(0, Width - Height * 2), Height);
            string text = _current == 0 && ZeroText != null ? ZeroText
                : Format != null ? Format(_current)
                : _current + Suffix;
            TextRenderer.DrawText(g, text, Theme.Semibold, valueRect, Enabled ? Theme.Text : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            if (Focused && ShowFocusCues)
            {
                using var pen = new Pen(Color.FromArgb(120, Theme.Text)) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(pen, valueRect.X + 4, 2, Math.Max(0, valueRect.Width - 8), Height - 5);
            }
        }

        private void DrawPart(Graphics g, Rectangle r, string symbol, Part part)
        {
            bool enabled = IsEnabled(part);
            Color fill = !enabled ? Theme.Blend(Theme.ButtonBack, BackColor, 0.55f)
                : _pressed == part ? Theme.ButtonPressed
                : _hover == part ? Theme.ButtonHover
                : Theme.ButtonBack;
            Color border = enabled ? Theme.Border : Theme.Blend(Theme.Border, BackColor, 0.55f);

            var rf = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1.5f, r.Height - 1.5f);
            using (var path = Theme.RoundRect(rf, Math.Max(4f, r.Height * 0.18f)))
            {
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
                using (var pen = new Pen(border))
                    g.DrawPath(pen, path);
            }

            TextRenderer.DrawText(g, symbol, Font, r, enabled ? Theme.Text : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    // =========================================================================
    // Games list: owner-drawn rows with an on/off switch per game
    // =========================================================================
    /// <summary>One row of an <see cref="ExeList"/>: exe name, a coloured tag (where it comes from) and its location.</summary>
    public sealed class ExeListItem
    {
        public string Title { get; init; } = string.Empty;
        public string Tag { get; init; } = string.Empty;
        public Color TagColor { get; init; } = Theme.Muted;
        public string? Path { get; init; }
    }

    /// <summary>
    /// A short list of exes as rounded cards (Games tab › Options › Programs): exe icon, name, a tag on the right and the
    /// full path below. Heights follow the fonts, so nothing is cut at 125–200 % scaling. Click or arrow keys select.
    /// </summary>
    public sealed class ExeList : Control
    {
        private List<ExeListItem> _items = new();
        private int _selected = -1, _hover = -1, _offset;
        private readonly Dictionary<string, Bitmap?> _icons = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _iconsLoading = new(StringComparer.OrdinalIgnoreCase);
        private readonly ToolTip _pathTip = new() { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 20000 };   // the full path (#14)

        public event EventHandler? SelectionChanged;

        public ExeList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Body;
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => _selected;
            set
            {
                int v = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
                if (v == _selected) return;
                _selected = v;
                ScrollIntoView(v);
                Invalidate();
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void SetItems(IList<ExeListItem> items, int selected)
        {
            _items = new List<ExeListItem>(items);
            _hover = -1;
            _offset = Math.Clamp(_offset, 0, MaxOffset);
            _selected = -2;   // always raise SelectionChanged
            SelectedIndex = selected;
        }

        private int S(int v) => LogicalToDeviceUnits(v);
        private int RowHeight => Theme.Semibold.Height + Theme.Small.Height + S(26);
        private int Gap => S(8);
        private int MaxOffset => Math.Max(0, _items.Count * (RowHeight + Gap) - Gap - ClientSize.Height);
        private Rectangle RowRect(int i) => new(0, i * (RowHeight + Gap) - _offset, ClientSize.Width - (MaxOffset > 0 ? S(8) : 0), RowHeight);

        private int IndexAt(Point p)
        {
            for (int i = 0; i < _items.Count; i++) if (RowRect(i).Contains(p)) return i;
            return -1;
        }

        private void ScrollIntoView(int i)
        {
            if (i < 0) return;
            var r = RowRect(i);
            if (r.Top < 0) _offset += r.Top;
            else if (r.Bottom > ClientSize.Height) _offset += r.Bottom - ClientSize.Height;
            _offset = Math.Clamp(_offset, 0, MaxOffset);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = IndexAt(e.Location);
            if (h == _hover) return;
            _hover = h;
            Invalidate();
            _pathTip.SetToolTip(this, h >= 0 ? _items[h].Path ?? string.Empty : string.Empty);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover != -1) { _hover = -1; Invalidate(); }
            _pathTip.SetToolTip(this, string.Empty);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int i = IndexAt(e.Location);
            if (i >= 0) SelectedIndex = i;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _offset = Math.Clamp(_offset - e.Delta * (RowHeight + Gap) / 240, 0, MaxOffset);
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Down) SelectedIndex = Math.Min(_items.Count - 1, _selected + 1);
            else if (e.KeyCode == Keys.Up) SelectedIndex = Math.Max(0, _selected - 1);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _offset = Math.Clamp(_offset, 0, MaxOffset);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags line = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                Rectangle row = RowRect(i);
                if (row.Bottom < 0 || row.Top > ClientSize.Height) continue;
                bool selected = i == _selected, hover = i == _hover;

                // The card
                using (var path = Theme.RoundRect(new RectangleF(row.X + 0.5f, row.Y + 0.5f, row.Width - 1, row.Height - 1), S(10)))
                {
                    using (var brush = new SolidBrush(selected ? Theme.ButtonBack : hover ? Theme.SurfaceHover : Theme.Surface)) g.FillPath(brush, path);
                    if (selected)
                    {
                        using var pen = new Pen(Color.FromArgb(Focused ? 200 : 120, Theme.Accent), Math.Max(1f, S(3) / 2f));
                        g.DrawPath(pen, path);
                    }
                }

                // Exe icon (or a letter)
                int iconSize = S(32);
                var iconRect = new Rectangle(row.X + S(16), row.Y + (row.Height - iconSize) / 2, iconSize, iconSize);
                var icon = GetIcon(item.Path);
                if (icon != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(icon, iconRect);
                }
                else GameList.DrawLetterIcon(g, iconRect, item.Title);

                // Tag on the right, at the height of the name ("found automatically", "added by you" …)
                int textX = iconRect.Right + S(14);
                int nameHeight = Theme.Semibold.Height, pathHeight = Theme.Small.Height;
                int top = row.Y + (row.Height - nameHeight - pathHeight - S(4)) / 2;
                int right = row.Right - S(16);
                if (item.Tag.Length > 0)
                {
                    Size tagText = TextRenderer.MeasureText(g, item.Tag, Theme.Small, Size.Empty, line);
                    var tag = new Rectangle(right - tagText.Width - S(20), top + (nameHeight - tagText.Height - S(6)) / 2, tagText.Width + S(20), tagText.Height + S(6));
                    using (var path = Theme.RoundRect(tag, tag.Height / 2f))
                    using (var brush = new SolidBrush(Color.FromArgb(40, item.TagColor)))
                        g.FillPath(brush, path);
                    TextRenderer.DrawText(g, item.Tag, Theme.Small, tag, item.TagColor, line | TextFormatFlags.HorizontalCenter);
                    TextRenderer.DrawText(g, item.Title, Theme.Semibold, new Rectangle(textX, top, Math.Max(0, tag.X - S(10) - textX), nameHeight),
                                          Theme.Text, line | TextFormatFlags.EndEllipsis);
                }
                else
                {
                    TextRenderer.DrawText(g, item.Title, Theme.Semibold, new Rectangle(textX, top, Math.Max(0, right - textX), nameHeight),
                                          Theme.Text, line | TextFormatFlags.EndEllipsis);
                }

                // Full path below, shortened in the middle when it doesn't fit
                TextRenderer.DrawText(g, item.Path ?? "location not known", Theme.Small,
                                      new Rectangle(textX, top + nameHeight + S(4), Math.Max(0, right - textX), pathHeight),
                                      Theme.Muted, line | TextFormatFlags.PathEllipsis);
            }

            if (MaxOffset > 0)
            {
                float visible = ClientSize.Height, total = MaxOffset + visible;
                float h = Math.Max(S(24), visible * visible / total);
                float y = (visible - h) * _offset / MaxOffset;
                using var path = Theme.RoundRect(new RectangleF(ClientSize.Width - S(5), y, S(4), h), S(2));
                using var brush = new SolidBrush(Color.FromArgb(170, Theme.Border));
                g.FillPath(brush, path);
            }
        }

        /// <summary>Exe icon, loaded in the background (a sleeping disk must not freeze the dialog).</summary>
        private Bitmap? GetIcon(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_icons.TryGetValue(path, out var cached)) return cached;
            if (!IsHandleCreated || !_iconsLoading.Add(path)) return null;
            string file = path;
            System.Threading.Tasks.Task.Run(() =>
            {
                Bitmap? bmp = null;
                try
                {
                    if (System.IO.File.Exists(file))
                    {
                        using var icon = Icon.ExtractAssociatedIcon(file);
                        if (icon != null) bmp = icon.ToBitmap();
                    }
                }
                catch { }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) { bmp?.Dispose(); return; }
                        _icons[file] = bmp;
                        _iconsLoading.Remove(file);
                        Invalidate();
                    }));
                }
                catch { bmp?.Dispose(); }
            });
            return null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var bmp in _icons.Values) bmp?.Dispose();
                _icons.Clear();
                _pathTip.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public sealed class GameItem
    {
        public GameItem(GameRow row)
        {
            Key = row.Key;
            Name = row.Name;
            Subtitle = row.Subtitle;
            Processes = row.Processes;
            ExePath = row.ExePath;
            Hdr = row.Hdr;
            HasOptions = row.HasOptions;
            Excluded = row.Excluded;
        }

        public string Key { get; }
        public string Name { get; }
        public string Subtitle { get; }
        public List<string> Processes { get; }
        public string? ExePath { get; }
        public bool Hdr { get; set; }
        public bool HasOptions { get; set; }
        public bool Excluded { get; }      // "Never HDR" row (only shown while searching)
        public override string ToString() => Name;
    }

    /// <summary>
    /// Animation clock synced to the monitor: a background thread waits for each display frame (DwmFlush returns when
    /// the Windows compositor has presented a frame, i.e. at the monitor's refresh rate: 60, 144, 165 Hz ...) and then
    /// runs one animation step on the UI thread. A WinForms Timer only ticks ~64 times per second, unsynchronised,
    /// which looks stuttery on high-refresh monitors. Start/Stop are called on the UI thread only.
    /// </summary>
    internal sealed class FrameLoop : IDisposable
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmFlush();

        private readonly Control _owner;
        private readonly Action _frame;
        private readonly object _gate = new();
        private volatile bool _running;
        private System.Threading.Thread? _thread;

        public FrameLoop(Control owner, Action frame)
        {
            _owner = owner;
            _frame = frame;
        }

        public bool IsRunning => _running;

        public void Start()
        {
            lock (_gate)
            {
                _running = true;
                if (_thread != null) return;   // the loop thread is still alive and keeps going
                _thread = new System.Threading.Thread(Loop) { IsBackground = true, Name = "FrameLoop" };
                _thread.Start();
            }
        }

        public void Stop() => _running = false;

        private void Loop()
        {
            while (true)
            {
                lock (_gate)
                {
                    if (!_running)
                    {
                        _thread = null;
                        return;
                    }
                }

                if (DwmFlush() != 0) System.Threading.Thread.Sleep(4);   // composition off: fall back to ~250 Hz pacing

                try
                {
                    if (_owner.IsDisposed || !_owner.IsHandleCreated) { _running = false; continue; }
                    _owner.Invoke(_frame);   // one step + immediate repaint, then wait for the next display frame
                }
                catch
                {
                    _running = false;        // control closed meanwhile
                }
            }
        }

        public void Dispose() => _running = false;
    }

    /// <summary>
    /// Games list drawn entirely by this control (no native list box): pixel-precise, animated scrolling for mouse
    /// wheels and touchpads, a slim overlay scrollbar, no flicker (double-buffered), and only visible rows are drawn.
    /// Each row: program icon, name, source line and an HDR switch.
    /// </summary>
    public sealed class GameList : Control
    {
        private List<GameItem> _items = new();
        private float _offset;             // current scroll position in pixels
        private float _target;             // where the scroll animation is heading
        private int _hover = -1;
        private int _selected = -1;
        private int _pressed = -1;
        private bool _pressedOnSwitch;

        private bool _thumbHover;
        private bool _draggingThumb;
        private int _dragStartY;
        private float _dragStartOffset;

        private readonly FrameLoop _animation;
        private readonly System.Diagnostics.Stopwatch _frameClock = new();

        // Softness of the glide: lower = longer, softer deceleration (1/s)
        private const double GlideRate = 8.5;

        private readonly Dictionary<string, Bitmap?> _icons = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _iconsLoading = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<GameItem>? HdrToggled;

        /// <summary>Raised after a scroll animation with its measured frame rate (for the Log tab).</summary>
        public event EventHandler<string>? AnimationStats;

        // Each row is drawn once into a bitmap and only re-drawn when it changes; scrolling just copies the bitmaps.
        // (Drawing text, antialiased shapes and scaled icons for every row on every frame was too slow for 144 Hz+.)
        private sealed class RowCache
        {
            public Bitmap Bitmap = null!;
            public int Width;
            public int Height;
            public int State;
        }
        private readonly Dictionary<GameItem, RowCache> _rowCache = new();

        // While the window is being resized the row width changes on every frame: the rows are drawn straight into the
        // paint buffer then (building a new bitmap per row and frame made resizing slow), and cached again once the width
        // has stayed the same for a moment.
        private readonly System.Windows.Forms.Timer _resizeSettle = new() { Interval = 250 };
        private bool _resizing;
        private int _lastWidth;

        // frame statistics of the current scroll animation
        private int _statFrames;
        private double _statPaintMs;
        private readonly System.Diagnostics.Stopwatch _statClock = new();
        private long _lastStatsLogMs = -100000;

        public GameList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            _animation = new FrameLoop(this, AnimateScroll);
            _resizeSettle.Tick += (_, _) =>
            {
                _resizeSettle.Stop();
                _resizing = false;
                Invalidate();   // cached again at the final width
            };
        }

        // ------------------------------------------------------------------
        // Items / selection
        // ------------------------------------------------------------------
        public IReadOnlyList<GameItem> Items => _items;

        public GameItem? SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

        /// <summary>Replaces the rows, keeping the scroll position and (by key) the selection.</summary>
        public void SetItems(IList<GameItem> items, string? selectedKey)
        {
            ClearRowCache();
            _items = new List<GameItem>(items);
            _selected = selectedKey == null ? -1 : _items.FindIndex(i => i.Key == selectedKey);
            _hover = -1;
            _target = Math.Clamp(_target, 0, MaxOffset);
            _offset = Math.Clamp(_offset, 0, MaxOffset);
            Invalidate();
        }

        private int RowHeight => Font.Height + Theme.Small.Height + 22;
        private int Gap => LogicalToDeviceUnits(4);
        private float ContentHeight => _items.Count * RowHeight + Gap * 2;
        private float MaxOffset => Math.Max(0f, ContentHeight - ClientSize.Height);
        private bool ScrollbarVisible => MaxOffset > 0.5f;
        private int ScrollbarWidth => LogicalToDeviceUnits(10);

        private Rectangle RowRect(int index)
        {
            int right = ClientSize.Width - (ScrollbarVisible ? ScrollbarWidth + LogicalToDeviceUnits(2) : 0);
            int y = (int)Math.Round(Gap + index * RowHeight - _offset);
            return new Rectangle(Gap, y, Math.Max(0, right - Gap * 2), RowHeight);
        }

        private int IndexAt(Point p)
        {
            if (ScrollbarVisible && p.X >= ClientSize.Width - ScrollbarWidth - LogicalToDeviceUnits(2)) return -1;
            float y = p.Y + _offset - Gap;
            if (y < 0) return -1;
            int index = (int)(y / RowHeight);
            return index >= 0 && index < _items.Count ? index : -1;
        }

        private RectangleF SwitchRect(Rectangle row)
        {
            float h = row.Height * 0.4f;
            float w = h * 2f;
            return new RectangleF(row.Right - w - row.Height * 0.35f, row.Y + (row.Height - h) / 2f, w, h);
        }

        private bool IsOnSwitch(Point p, int index)
        {
            if (index < 0) return false;
            Rectangle row = RowRect(index);
            RectangleF hit = SwitchRect(row);
            hit.Inflate(row.Height * 0.2f, row.Height * 0.2f);
            return hit.Contains(p);
        }

        private void Toggle(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            var item = _items[index];
            item.Hdr = !item.Hdr;
            Invalidate();
            HdrToggled?.Invoke(this, item);
        }

        // ------------------------------------------------------------------
        // Smooth scrolling
        // ------------------------------------------------------------------
        private void ScrollTo(float target, bool animate = true)
        {
            _target = Math.Clamp(target, 0, MaxOffset);
            if (!animate)
            {
                _offset = _target;
                _animation.Stop();
                UpdateHoverFromCursor();
                Invalidate();
                return;
            }
            if (!_animation.IsRunning)
            {
                _frameClock.Restart();
                _statClock.Restart();
                _statFrames = 0;
                _statPaintMs = 0;
                _animation.Start();
            }
        }

        private void ReportStats()
        {
            double seconds = _statClock.Elapsed.TotalSeconds;
            if (_statFrames < 15 || seconds <= 0) return;
            long now = Environment.TickCount64;
            if (now - _lastStatsLogMs < 10000) return;   // at most every 10 s
            _lastStatsLogMs = now;
            AnimationStats?.Invoke(this,
                $"Scrolling: {_statFrames / seconds:0} frames/s, drawing {_statPaintMs / _statFrames:0.0} ms per frame");
        }

        private void AnimateScroll()
        {
            float dt = (float)_frameClock.Elapsed.TotalSeconds;
            _frameClock.Restart();
            dt = Math.Clamp(dt, 0.001f, 0.05f);

            // Exponential ease towards the target: immediate response, long soft landing, frame-rate independent
            float k = 1f - (float)Math.Exp(-dt * GlideRate);
            _offset += (_target - _offset) * k;
            bool finished = Math.Abs(_target - _offset) < 0.3f;
            if (finished)
            {
                _offset = _target;
                _animation.Stop();
            }
            UpdateHoverFromCursor();
            Invalidate();
            Update();   // paint now, inside this display frame
            _statFrames++;
            if (finished) ReportStats();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // Touchpads send many small deltas, mouse wheels steps of 120: both scroll proportionally
            int lines = SystemInformation.MouseWheelScrollLines;
            float perNotch = lines < 0 ? ClientSize.Height * 0.9f : Math.Max(RowHeight * 1.2f, lines * Font.Height * 1.6f);
            ScrollTo(_target - e.Delta / 120f * perNotch);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            base.OnMouseWheel(e);
        }

        private void EnsureVisible(int index)
        {
            if (index < 0) return;
            float top = Gap + index * RowHeight;
            if (top < _target) ScrollTo(top - Gap);
            else if (top + RowHeight > _target + ClientSize.Height) ScrollTo(top + RowHeight - ClientSize.Height + Gap);
        }

        // ------------------------------------------------------------------
        // Overlay scrollbar
        // ------------------------------------------------------------------
        private RectangleF ThumbRect()
        {
            float track = ClientSize.Height - LogicalToDeviceUnits(8);
            float thumbH = Math.Max(LogicalToDeviceUnits(36), track * ClientSize.Height / Math.Max(1f, ContentHeight));
            float y = LogicalToDeviceUnits(4) + (MaxOffset <= 0 ? 0 : (track - thumbH) * (_offset / MaxOffset));
            float w = _thumbHover || _draggingThumb ? ScrollbarWidth - LogicalToDeviceUnits(2) : ScrollbarWidth * 0.5f;
            return new RectangleF(ClientSize.Width - LogicalToDeviceUnits(3) - w, y, w, thumbH);
        }

        private bool OnScrollbar(Point p) => ScrollbarVisible && p.X >= ClientSize.Width - ScrollbarWidth - LogicalToDeviceUnits(4);

        // ------------------------------------------------------------------
        // Mouse & keyboard
        // ------------------------------------------------------------------
        private void UpdateHoverFromCursor()
        {
            Point p = PointToClient(Cursor.Position);
            int h = ClientRectangle.Contains(p) && !_draggingThumb ? IndexAt(p) : -1;
            if (h != _hover) _hover = h;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_draggingThumb)
            {
                float track = ClientSize.Height - LogicalToDeviceUnits(8) - ThumbRect().Height;
                float perPixel = track <= 0 ? 0 : MaxOffset / track;
                ScrollTo(_dragStartOffset + (e.Y - _dragStartY) * perPixel, animate: false);
            }
            else
            {
                bool thumbHover = OnScrollbar(e.Location);
                int h = IndexAt(e.Location);
                if (h != _hover || thumbHover != _thumbHover)
                {
                    _hover = h;
                    _thumbHover = thumbHover;
                    Invalidate();
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (!_draggingThumb)
            {
                _hover = -1;
                _thumbHover = false;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left)
            {
                if (OnScrollbar(e.Location))
                {
                    RectangleF thumb = ThumbRect();
                    if (e.Y >= thumb.Top && e.Y <= thumb.Bottom)
                    {
                        _draggingThumb = true;
                        _dragStartY = e.Y;
                        _dragStartOffset = _offset;
                        Capture = true;
                    }
                    else
                    {
                        // click on the track: jump one page towards the click
                        ScrollTo(_target + (e.Y < thumb.Top ? -1 : 1) * ClientSize.Height * 0.9f);
                    }
                    Invalidate();
                }
                else
                {
                    _pressed = IndexAt(e.Location);
                    _pressedOnSwitch = IsOnSwitch(e.Location, _pressed);
                    if (_pressed >= 0) _selected = _pressed;
                    Invalidate();
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_draggingThumb)
            {
                _draggingThumb = false;
                Capture = false;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Left && _pressed >= 0 && _pressedOnSwitch)
            {
                int index = IndexAt(e.Location);
                if (index == _pressed && IsOnSwitch(e.Location, index)) Toggle(index);
            }
            _pressed = -1;
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            // Double-click on the name toggles too (the switch itself toggles on every click)
            int index = IndexAt(e.Location);
            if (e.Button == MouseButtons.Left && index >= 0 && !IsOnSwitch(e.Location, index) && !OnScrollbar(e.Location))
                Toggle(index);
            base.OnMouseDoubleClick(e);
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End or Keys.Space
            || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            int visibleRows = Math.Max(1, ClientSize.Height / RowHeight - 1);
            int next = e.KeyCode switch
            {
                Keys.Up => Math.Max(0, _selected - 1),
                Keys.Down => Math.Min(_items.Count - 1, _selected + 1),
                Keys.PageUp => Math.Max(0, _selected - visibleRows),
                Keys.PageDown => Math.Min(_items.Count - 1, _selected + visibleRows),
                Keys.Home => 0,
                Keys.End => _items.Count - 1,
                _ => int.MinValue
            };

            if (next != int.MinValue && _items.Count > 0)
            {
                _selected = next;
                EnsureVisible(next);
                Invalidate();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Space && _selected >= 0)
            {
                Toggle(_selected);
                e.Handled = e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _target = Math.Clamp(_target, 0, MaxOffset);
            _offset = Math.Clamp(_offset, 0, MaxOffset);
            int width = ClientSize.Width;
            if (width != _lastWidth && _lastWidth > 0 && IsHandleCreated && Visible)
            {
                _resizing = true;
                _resizeSettle.Stop();
                _resizeSettle.Start();
            }
            _lastWidth = width;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        // ------------------------------------------------------------------
        // Painting
        // ------------------------------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, "No games found. Use “Scan games” or “Add program”.", Font, ClientRectangle, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            long paintStart = System.Diagnostics.Stopwatch.GetTimestamp();
            int first = Math.Max(0, (int)((_offset - Gap) / RowHeight));
            int last = Math.Min(_items.Count - 1, (int)((_offset + ClientSize.Height) / RowHeight) + 1);

            if (_resizing)
            {
                for (int i = first; i <= last; i++) DrawRow(g, i, RowRect(i));
                g.InterpolationMode = InterpolationMode.Default;
            }
            else
            {
                g.CompositingMode = CompositingMode.SourceCopy;          // rows are opaque: plain copy, fastest path
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                for (int i = first; i <= last; i++)
                {
                    Rectangle row = RowRect(i);
                    var cache = GetRowBitmap(i, row.Width, row.Height);
                    if (cache != null)
                        g.DrawImage(cache.Bitmap, new Rectangle(row.X, row.Y, cache.Width, cache.Height),
                            0, 0, cache.Width, cache.Height, GraphicsUnit.Pixel);
                }
                g.CompositingMode = CompositingMode.SourceOver;
                g.PixelOffsetMode = PixelOffsetMode.Default;
                TrimRowCache(first, last);
            }

            if (ScrollbarVisible)
            {
                RectangleF thumb = ThumbRect();
                Color color = _draggingThumb ? Theme.Text : _thumbHover ? Theme.Muted : Theme.Border;
                using var path = Theme.RoundRect(thumb, thumb.Width / 2f);
                using var brush = new SolidBrush(Color.FromArgb(_thumbHover || _draggingThumb ? 220 : 170, color));
                g.FillPath(brush, path);
            }

            if (_animation.IsRunning)
                _statPaintMs += (System.Diagnostics.Stopwatch.GetTimestamp() - paintStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>Everything that changes a row's look; the cached bitmap is re-drawn when this changes.</summary>
        private int RowState(int index)
        {
            var item = _items[index];
            int state = 0;
            if (index == _selected) state |= 1;
            if (index == _hover) state |= 2;
            if (index == _selected && Focused && ShowFocusCues) state |= 4;
            if (item.Hdr) state |= 8;
            if (item.HasOptions) state |= 16;
            if (!string.IsNullOrEmpty(item.ExePath) && _icons.TryGetValue(item.ExePath, out var icon) && icon != null) state |= 32;
            return state;
        }

        private RowCache? GetRowBitmap(int index, int width, int height)
        {
            if (width <= 0 || height <= 0) return null;
            var item = _items[index];
            int state = RowState(index);

            if (_rowCache.TryGetValue(item, out var cache) && cache.Width == width && cache.Height == height && cache.State == state)
                return cache;

            cache?.Bitmap.Dispose();
            // 32bpp RGB (no alpha): GDI text (TextRenderer) renders correctly into it; the row is opaque anyway
            var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                DrawRow(g, index, new Rectangle(0, 0, width, height));
            }

            cache = new RowCache { Bitmap = bmp, Width = width, Height = height, State = RowState(index) };
            _rowCache[item] = cache;
            return cache;
        }

        /// <summary>Keeps bitmaps only for rows near the visible area (memory stays small for long lists).</summary>
        private void TrimRowCache(int first, int last)
        {
            if (_rowCache.Count <= (last - first + 1) + 40) return;
            var keep = new HashSet<GameItem>();
            for (int i = Math.Max(0, first - 20); i <= Math.Min(_items.Count - 1, last + 20); i++) keep.Add(_items[i]);
            foreach (var item in new List<GameItem>(_rowCache.Keys))
            {
                if (keep.Contains(item)) continue;
                _rowCache[item].Bitmap.Dispose();
                _rowCache.Remove(item);
            }
        }

        private void ClearRowCache()
        {
            foreach (var cache in _rowCache.Values) cache.Bitmap.Dispose();
            _rowCache.Clear();
        }

        private void DrawRow(Graphics g, int index, Rectangle row)
        {
            var item = _items[index];
            bool selected = index == _selected;
            bool hover = index == _hover;

            if (selected || hover)
            {
                using var path = Theme.RoundRect(new RectangleF(row.X, row.Y + 1, row.Width, row.Height - 2), row.Height * 0.16f);
                using var brush = new SolidBrush(selected ? Theme.ButtonBack : Theme.SurfaceHover);
                g.FillPath(brush, path);
                if (selected && Focused && ShowFocusCues)
                {
                    using var pen = new Pen(Color.FromArgb(90, Theme.Text));
                    g.DrawPath(pen, path);
                }
            }

            // Icon (exe icon, or a coloured letter) — dimmed while HDR is off for this game
            float iconSize = row.Height * 0.6f;
            var iconRect = new RectangleF(row.X + row.Height * 0.25f, row.Y + (row.Height - iconSize) / 2f, iconSize, iconSize);
            var icon = GetIcon(item.ExePath);
            if (icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (item.Hdr)
                {
                    g.DrawImage(icon, iconRect);
                }
                else
                {
                    using var attributes = new System.Drawing.Imaging.ImageAttributes();
                    var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.55f };   // 55 % opacity
                    attributes.SetColorMatrix(matrix);
                    g.DrawImage(icon, Rectangle.Round(iconRect), 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attributes);
                }
            }
            else
            {
                DrawLetterIcon(g, iconRect, item.Name);
            }

            // Name + source line ("Steam · cyberpunk2077.exe")
            RectangleF sw = SwitchRect(row);
            int textX = (int)(iconRect.Right + row.Height * 0.25f);
            int textWidth = Math.Max(0, (int)sw.X - textX - 8);
            int nameHeight = Font.Height;
            int subHeight = Theme.Small.Height;
            int top = row.Y + (row.Height - nameHeight - subHeight) / 2;
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

            TextRenderer.DrawText(g, item.Name, Font, new Rectangle(textX, top, textWidth, nameHeight),
                item.Hdr ? Theme.Text : Theme.Blend(Theme.Text, Theme.Muted, 0.6f), flags);

            string subtitle = item.HasOptions ? item.Subtitle + "  ·  game options" : item.Subtitle;
            TextRenderer.DrawText(g, subtitle, Theme.Small, new Rectangle(textX, top + nameHeight, textWidth, subHeight), Theme.Muted, flags);

            ToggleSwitch.DrawSwitch(g, sw, item.Hdr ? 1f : 0f, hover, true);
        }

        // ------------------------------------------------------------------
        // Icons
        // ------------------------------------------------------------------
        /// <summary>
        /// Program icon from the exe. Loaded in the background the first time a row is shown (a sleeping disk must not
        /// freeze the window); the row shows the letter icon until then and repaints when the icon is ready.
        /// </summary>
        private Bitmap? GetIcon(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_icons.TryGetValue(path, out var cached)) return cached;
            if (!IsHandleCreated || !_iconsLoading.Add(path)) return null;

            string file = path;
            System.Threading.Tasks.Task.Run(() =>
            {
                Bitmap? bmp = null;
                try
                {
                    if (System.IO.File.Exists(file))
                    {
                        using var icon = Icon.ExtractAssociatedIcon(file);
                        if (icon != null) bmp = icon.ToBitmap();
                    }
                }
                catch { }

                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed) { bmp?.Dispose(); return; }
                        _icons[file] = bmp;
                        _iconsLoading.Remove(file);
                        Invalidate();
                    }));
                }
                catch
                {
                    bmp?.Dispose();   // the list was closed meanwhile
                }
            });
            return null;
        }

        private static readonly Color[] LetterColors =
        {
            Color.FromArgb(124, 72, 214), Color.FromArgb(215, 96, 132), Color.FromArgb(230, 140, 60),
            Color.FromArgb(60, 150, 210), Color.FromArgb(70, 170, 120), Color.FromArgb(150, 110, 200)
        };

        /// <summary>Fallback when there is no exe icon: rounded square with the first letter of the name.</summary>
        internal static void DrawLetterIcon(Graphics g, RectangleF r, string name)
        {
            int hash = 0;
            foreach (char c in name) hash = hash * 31 + c;
            Color color = LetterColors[(hash & 0x7fffffff) % LetterColors.Length];
            using (var path = Theme.RoundRect(r, r.Height * 0.24f))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, path);

            string letter = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "?";
            TextRenderer.DrawText(g, letter, Theme.Semibold, Rectangle.Round(r), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animation.Dispose();
                _resizeSettle.Dispose();
                ClearRowCache();
                foreach (var bmp in _icons.Values) bmp?.Dispose();
                _icons.Clear();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// TableLayoutPanel with smooth, animated mouse-wheel scrolling (used for the Settings page).
    /// The native scrollbar still works for dragging; the animation picks up from wherever it is.
    /// </summary>
    public sealed class SmoothScrollTable : TableLayoutPanel
    {
        private readonly FrameLoop _animation;
        private readonly System.Diagnostics.Stopwatch _frameClock = new();
        private const double GlideRate = 8.5;
        private float _current;
        private float _target;

        public SmoothScrollTable()
        {
            DoubleBuffered = true;
            _animation = new FrameLoop(this, Animate);
            Scroll += (_, _) =>
            {
                if (!_animation.IsRunning) _current = _target = -AutoScrollPosition.Y;
            };
        }

        private float MaxScroll => Math.Max(0, DisplayRectangle.Height - ClientSize.Height);

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (!VerticalScroll.Visible)
            {
                base.OnMouseWheel(e);
                return;
            }
            if (!_animation.IsRunning) _current = _target = -AutoScrollPosition.Y;

            int lines = SystemInformation.MouseWheelScrollLines;
            float perNotch = lines < 0 ? ClientSize.Height * 0.9f : Math.Max(60, lines * Font.Height * 2.2f);
            _target = Math.Clamp(_target - e.Delta / 120f * perNotch, 0, MaxScroll);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;

            if (!_animation.IsRunning)
            {
                _frameClock.Restart();
                _animation.Start();
            }
            // no base call: the base class would jump immediately
        }

        private void Animate()
        {
            float dt = Math.Clamp((float)_frameClock.Elapsed.TotalSeconds, 0.001f, 0.05f);
            _frameClock.Restart();
            float k = 1f - (float)Math.Exp(-dt * GlideRate);
            _current += (_target - _current) * k;
            if (Math.Abs(_target - _current) < 0.3f)
            {
                _current = _target;
                _animation.Stop();
            }
            AutoScrollPosition = new Point(0, (int)Math.Round(_current));
            Update();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _animation.Dispose();
            base.Dispose(disposing);
        }
    }
}
