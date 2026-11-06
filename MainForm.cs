using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartHdrManager
{
    /// <summary>
    /// The settings window: a status card on top, a segmented tab bar, and four pages (Games, Apps, Settings, Log).
    /// Layout values are in 96-DPI pixels; WinForms scales them for the actual display DPI.
    /// Closing or minimizing hides it to the tray; the tray context disposes it a few seconds later to free memory.
    /// </summary>
    public sealed class MainForm : Form
    {
        private const int CardWidth = 680;   // initial width of full-width cards (the form is 720 wide with 20 px padding)

        private readonly HdrEngine _engine;
        private readonly Action<bool> _setPaused;
        private readonly Action<HdrOverride, TimeSpan?> _setOverride;
        private readonly Action _applyHotkeys;
        private readonly Action _exitApp;
        private readonly SegmentedTabs _hotkeyChoice = new() { Size = new Size(240, 34) };
        private static readonly string[] HotkeyModes = { HotkeySettings.WinAlt, HotkeySettings.CtrlAlt, HotkeySettings.Off };
        private readonly Icon _windowIcon = AppIcons.Create(IconStyle.Vivid, 48);
        private readonly ToolTip _toolTip = new();

        // Status card
        private readonly StatusCard _statusCard = new() { Radius = 12, Dock = DockStyle.Fill, Margin = new Padding(0) };
        private readonly Label _hdrLabel = new() { AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text };
        private readonly Label _detailLabel = new() { AutoSize = false, AutoEllipsis = true, Font = Theme.Body, ForeColor = Theme.Muted };
        private readonly ModernButton _pauseButton = new() { Glyph = Theme.GlyphPause, Text = "Pause automation" };

        // Tabs
        private readonly SegmentedTabs _tabs = new() { Size = new Size(330, 40), Anchor = AnchorStyles.Top | AnchorStyles.Left, Margin = new Padding(0, 16, 0, 0) };
        private readonly Panel _content = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };

        // Content column: never wider than this (96-DPI pixels). A maximized window on a 4K screen would otherwise
        // stretch every card to the full screen width – harder to read, and much more to repaint while scrolling.
        private const int MaxContentWidth = 900;
        private TableLayoutPanel? _root;
        private readonly List<Control> _pages = new();

        // Games page
        private readonly TextBox _search = new() { BorderStyle = BorderStyle.None, PlaceholderText = "Search games…" };
        private readonly GameList _games = new() { Dock = DockStyle.Fill };
        private readonly Label _gamesCount = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(12, 0, 2, 0) };
        private readonly ModernButton _scanButton = new() { Primary = true, Glyph = Theme.GlyphRefresh, Text = "Scan games", AutoSize = true };

        // Settings page
        private readonly ToggleSwitch _swAutostart = new();
        // Automatic drive scan: off / at every start / daily / weekly / monthly (2.6)
        private readonly Stepper _scanInterval = new()
        {
            Minimum = -1, Maximum = 30, Size = new Size(150, 34),
            Stops = new[] { -1, 0, 1, 7, 30 },
            Format = v => v switch { < 0 => "Off", 0 => "Each start", 1 => "Daily", 7 => "Weekly", 30 => "Monthly", _ => $"{v} days" }
        };
        private Label? _scanIntervalDesc;
        private Label? _neverHdrDesc;
        private readonly ToggleSwitch _swShowOthers = new();
        private readonly Stepper _minimumHdr = new() { Minimum = 0, Maximum = 120, Step = 5, ZeroText = "Off" };
        private readonly List<(MonitorInfo Monitor, ToggleSwitch Switch)> _monitorSwitches = new();
        private readonly ToggleSwitch _swBrowserWindowed = new();
        private readonly ToggleSwitch _swKeepBrowserHdr = new();
        private readonly ToggleSwitch _swColorProfile = new();
        private readonly ToggleSwitch _swRefreshApps = new();
        private readonly ToggleSwitch _swKeepDesktopSdr = new();
        private readonly ToggleSwitch _swAutoUpdate = new();
        private ModernButton? _checkUpdatesButton;
        private Label? _updateDesc;
        private readonly Stepper _fallback = new() { Minimum = 0, Maximum = 30 };

        // Video & RTX HDR
        private readonly ToggleSwitch _swVideoAuto = new();
        private readonly SegmentedTabs _videoMode = new() { Size = new Size(240, 34) };
        private static readonly string[] VideoModes = { VideoHdrModes.Smart, VideoHdrModes.NativeOnly, VideoHdrModes.Always };
        private readonly SegmentedTabs _videoTrigger = new() { Size = new Size(200, 34) };
        private static readonly string[] VideoTriggerModes = { VideoTriggers.Fullscreen, VideoTriggers.Any };
        private readonly ToggleSwitch _swVideoBrowsers = new();
        private readonly ToggleSwitch _swVideoPlayers = new();
        // 0 s … 5 min: small steps first, then 1 / 2 / 3 / 5 minutes (keeps HDR through a pause)
        private readonly Stepper _videoRevert = new()
        {
            Minimum = 0, Maximum = HdrEngine.MaxVideoRevertSeconds,
            Stops = new[] { 0, 1, 2, 3, 5, 10, 15, 20, 30, 60, 120, 180, 300 },
            Format = v => v >= 60 && v % 60 == 0 ? $"{v / 60} min" : $"{v} s"
        };
        private readonly ToggleSwitch _swVideoBattery = new();

        // Log page
        private readonly TextBox _log = new()
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = false,
            BorderStyle = BorderStyle.None
        };

        private bool _loadingSettings;

        // Settings page parts that change at runtime
        private SmoothScrollTable? _settingsPage;
        private SmoothScrollTable? _appsPage;
        private readonly List<(int Row, int Height, Control Control)> _videoDetailRows = new();
        private int _videoMasterRow;
        private Label? _scriptStatusLabel, _browserWindowedDesc, _keepBrowserDesc;
        private bool? _lastScriptSeen;
        private bool _reallyExit;
        private int _logCursor;
        private int _lastState = -1;
        private bool _scanInFlight;

        public event EventHandler? HiddenToTray;

        public MainForm(HdrEngine engine, Action<bool> setPaused, Action<HdrOverride, TimeSpan?> setOverride, Action applyHotkeys, Action exitApp)
        {
            _engine = engine;
            _setPaused = setPaused;
            _setOverride = setOverride;
            _applyHotkeys = applyHotkeys;
            _exitApp = exitApp;
            _hotkeyChoice.AddTab("Win+Alt");
            _hotkeyChoice.AddTab("Ctrl+Alt");
            _hotkeyChoice.AddTab("Off");
            _videoMode.AddTab("Smart");
            _videoMode.AddTab("HDR only");
            _videoMode.AddTab("Always");
            _videoTrigger.AddTab("Full screen");
            _videoTrigger.AddTab("Any");

            SuspendLayout();

            Text = "Smart HDR Manager";
            Icon = _windowIcon;
            Font = Theme.Body;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(20) };
            _root = root;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            BuildStatusCard();
            root.Controls.Add(_statusCard, 0, 0);

            _tabs.AddTab("Games");
            _tabs.AddTab("Apps");
            _tabs.AddTab("Settings");
            _tabs.AddTab("Log");
            root.Controls.Add(_tabs, 0, 1);

            AddPage(BuildGamesPage());
            AddPage(BuildAppsPage());
            AddPage(BuildSettingsPage());
            AddPage(BuildLogPage());
            root.Controls.Add(_content, 0, 2);

            // Designer-style auto scaling: everything above is in 96-DPI pixels
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 660);
            MinimumSize = new Size(660, 560);
            Controls.Add(root);

            ResumeLayout(false);
            PerformLayout();

            LayoutStatusCard();
            ShowPage(0);
            WireEvents();
            LoadSettings();
            RebuildGames();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleWindow(Handle);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _engine.LibraryChanged -= OnLibraryChanged;
            base.Dispose(disposing);
            if (disposing)
            {
                _toolTip.Dispose();
                _windowIcon.Dispose();
            }
        }

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------
        private void BuildStatusCard()
        {
            const int h = 104;
            _statusCard.Size = new Size(CardWidth, h);
            _statusCard.IndicatorX = 28;

            // Initial bounds (96-DPI pixels); the real positions are set in LayoutStatusCard on every resize,
            // because edge anchoring combined with DPI scaling can stretch the label over the buttons.
            _hdrLabel.Location = new Point(52, 18);
            _pauseButton.SetBounds(CardWidth - 18 - 196, (h - 34) / 2, 196, 34);
            _detailLabel.SetBounds(54, 60, _pauseButton.Left - 16 - 54, 22);

            // Transparent so the animated HDR sweep on the card shows behind the text and around the buttons
            _hdrLabel.BackColor = Color.Transparent;
            _detailLabel.BackColor = Color.Transparent;
            _pauseButton.BackColor = Color.Transparent;

            // Button first = on top of the labels (minimize / close in the title bar hide the window to the tray)
            _statusCard.Controls.Add(_pauseButton);
            _statusCard.Controls.Add(_hdrLabel);
            _statusCard.Controls.Add(_detailLabel);
            // Layout (not Resize): runs after WinForms has finished DPI-scaling the child controls,
            // so the positions set here are not scaled a second time.
            _statusCard.Layout += (_, _) => LayoutStatusCard();
        }

        private void LayoutStatusCard()
        {
            int S(int v) => _statusCard.LogicalToDeviceUnits(v);
            int w = _statusCard.ClientSize.Width;
            int h = _statusCard.ClientSize.Height;

            _pauseButton.Location = new Point(w - S(18) - _pauseButton.Width, (h - _pauseButton.Height) / 2);

            // Title + detail as one block, vertically centred
            int detailHeight = _detailLabel.PreferredHeight;
            int top = Math.Max(0, (h - _hdrLabel.Height - S(2) - detailHeight) / 2);
            int left = S(52);
            _hdrLabel.Location = new Point(left, top);
            _detailLabel.SetBounds(left + S(2), _hdrLabel.Bottom + S(2), Math.Max(0, _pauseButton.Left - S(16) - left), detailHeight);
        }

        private void AddPage(Control page)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _pages.Add(page);
            _content.Controls.Add(page);
        }

        private void ShowPage(int index)
        {
            for (int i = 0; i < _pages.Count; i++)
                _pages[i].Visible = i == index;
        }

        private Control BuildGamesPage()
        {
            var page = new TableLayoutPanel { ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // hint + count
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));    // search
            page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // list
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));    // buttons

            // Hint + count
            var hintRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = new Padding(0) };
            hintRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            hintRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            hintRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            hintRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var hint = new Label
            {
                Text = "Turn a game on to use HDR while it runs",
                Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(2, 0, 0, 0)
            };
            var othersLabel = new Label
            {
                Text = "Other programs", Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(16, 0, 6, 0)
            };
            _swShowOthers.Anchor = AnchorStyles.Right;
            _swShowOthers.Margin = new Padding(0);
            _toolTip.SetToolTip(_swShowOthers, "Also list programs found by the drive scan that are not in a game library");
            _toolTip.SetToolTip(othersLabel, "Also list programs found by the drive scan that are not in a game library");
            hintRow.Controls.Add(hint, 0, 0);
            hintRow.Controls.Add(_gamesCount, 1, 0);
            hintRow.Controls.Add(othersLabel, 2, 0);
            hintRow.Controls.Add(_swShowOthers, 3, 0);
            page.Controls.Add(hintRow, 0, 0);

            // Search box: rounded field with a search icon
            var searchCard = new RoundedPanel
            {
                Radius = 8, BackColor = Theme.Input, BorderColor = Theme.Border,
                Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 10), Size = new Size(CardWidth, 34)
            };
            var searchIcon = new Label
            {
                Text = Theme.GlyphSearch, Font = Theme.IconFont, ForeColor = Theme.Muted, BackColor = Theme.Input,
                TextAlign = ContentAlignment.MiddleCenter, AutoSize = false
            };
            searchIcon.SetBounds(8, 0, 26, 34);
            _search.BackColor = Theme.Input;
            _search.ForeColor = Theme.Text;
            _search.Font = Theme.Body;
            _search.SetBounds(38, (34 - _search.PreferredHeight) / 2, CardWidth - 50, _search.PreferredHeight);
            searchCard.Controls.Add(searchIcon);
            searchCard.Controls.Add(_search);
            searchCard.Layout += (_, _) =>   // after DPI scaling, see LayoutStatusCard
            {
                // explicit layout instead of anchoring (DPI-safe), text box vertically centred
                int S(int v) => searchCard.LogicalToDeviceUnits(v);
                int ch = searchCard.ClientSize.Height;
                searchIcon.SetBounds(S(8), S(1), S(26), Math.Max(0, ch - S(2)));
                int x = S(40);
                _search.SetBounds(x, Math.Max(0, (ch - _search.Height) / 2), Math.Max(0, searchCard.ClientSize.Width - x - S(12)), _search.Height);
            };
            page.Controls.Add(searchCard, 0, 1);

            // Game list card
            var listCard = new RoundedPanel { Radius = 12, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(6) };
            listCard.Controls.Add(_games);
            page.Controls.Add(listCard, 0, 2);

            // Actions
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 12, 0, 0), Padding = new Padding(0) };
            var addExe = new ModernButton { Glyph = Theme.GlyphAdd, Text = "Add program", AutoSize = true };
            var addRunning = new ModernButton { Glyph = Theme.GlyphAdd, Text = "Add running", AutoSize = true };
            var exclude = new ModernButton { Glyph = Theme.GlyphCancel, Text = "Never HDR", AutoSize = true };
            var options = new ModernButton { Glyph = Theme.GlyphSettings, Text = "Options", AutoSize = true };
            _toolTip.SetToolTip(options, "Per-game options for the selected game: delay before HDR, keep HDR after it closes");
            options.Click += (_, _) => OnGameOptions();
            _toolTip.SetToolTip(addExe, "Pick a game's .exe file");
            _toolTip.SetToolTip(addRunning, "Pick from programs that are running right now");
            _toolTip.SetToolTip(exclude, "Never use HDR for the selected game. Undo: Settings › Never HDR games, or search for the game here");
            addExe.Click += (_, _) => OnAddExe();
            addRunning.Click += (_, _) => OnAddRunning();
            exclude.Click += (_, _) => OnExclude();
            _scanButton.Click += OnScan;

            _toolTip.SetToolTip(_scanButton, "Re-read the libraries of your game launchers (Steam, Epic, GOG, Xbox and more) and scan your game folders");
            foreach (var b in new Control[] { _scanButton, addExe, addRunning, options, exclude })
            {
                b.Margin = new Padding(0, 0, 8, 0);
                buttons.Controls.Add(b);
            }
            page.Controls.Add(buttons, 0, 3);

            return page;
        }

        private Control BuildSettingsPage()
        {
            var page = new SmoothScrollTable { ColumnCount = 1, Margin = new Padding(0), AutoScroll = true, Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Theme.UseDarkScrollbars(page);
            _settingsPage = page;

            int Add(Control c, int height)
            {
                page.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
                int row = page.RowStyles.Count - 1;
                page.Controls.Add(c, 0, row);
                return row;
            }
            void Section(string title, string description, bool first = false)
            {
                Add(SectionHeader(title), first ? 34 : 46);
                Add(SectionDescription(description), 30);
            }

            // ---- General ----
            Section("General", "Startup, game search and keyboard shortcuts.", first: true);
            Add(SettingRow("Start with Windows", "Starts hidden in the tray when you sign in.", _swAutostart), 68);
            var scanRow = SettingRow("Look for new games", "Searches your drives for newly installed games.", _scanInterval);
            _scanIntervalDesc = RowDescription(scanRow);
            Add(scanRow, 68);
            Add(SettingRow("Keyboard shortcuts", "+ H: HDR for this game  ·  + I: never HDR  ·  + O: force HDR / SDR", _hotkeyChoice), 68);
            var neverButton = new ModernButton { Text = "Show list", AutoSize = true };
            neverButton.Click += (_, _) => OnManageNeverHdr();
            var neverRow = SettingRow("Never HDR games", "Games you excluded – undo it here.", neverButton);
            _neverHdrDesc = RowDescription(neverRow);
            Add(neverRow, 68);

            // ---- Monitors ----
            var monitors = _engine.GetMonitors();
            int hdrMonitors = monitors.Count(m => m.HdrSupported);
            Section("Monitors", hdrMonitors > 1
                ? "Only these displays switch. Playing on another screen? Turn that one on too."
                : "Which displays switch to HDR.");
            foreach (var monitor in monitors)
            {
                var sw = new ToggleSwitch { Enabled = monitor.HdrSupported };
                string title = monitor.IsPrimary ? $"{monitor.Name}  (primary)" : monitor.Name;
                string desc = !monitor.HdrSupported
                    ? "This display does not support HDR."
                    : $"HDR is {(monitor.HdrEnabled ? "on" : "off")} right now";
                _monitorSwitches.Add((monitor, sw));
                Add(SettingRow(title, desc, sw), 68);
            }

            // ---- Switching back ----
            Section("Back to SDR", "The app only switches back HDR that it turned on. HDR you turned on stays on.");
            Add(SettingRow("Wait before switching back", "After a game or video ends.", _fallback), 68);
            Add(SettingRow("Minimum time in HDR", "Fewer black screens when things start and stop quickly.", _minimumHdr), 68);
            Add(SettingRow("Keep the desktop in SDR", "Also switch back HDR that you or other programs turned on.", _swKeepDesktopSdr), 68);

            // ---- YouTube & browser ----
            Section("YouTube in browser", "HDR for YouTube's HDR videos – needs the browser script (Tampermonkey).");
            var guide = new ModernButton { Glyph = Theme.GlyphDocument, Text = "Installation guide", AutoSize = true };
            guide.Click += (_, _) => OpenScriptGuide();
            var scriptRow = SettingRow("Browser script", "Checking …", guide);
            _scriptStatusLabel = RowDescription(scriptRow);
            Add(scriptRow, 68);
            var windowedRow = SettingRow("Also in a window", "Not only when the video is full screen.", _swBrowserWindowed);
            _browserWindowedDesc = RowDescription(windowedRow);
            Add(windowedRow, 68);
            var keepRow = SettingRow("Keep HDR in the background", "While the HDR video plays and you use another window.", _swKeepBrowserHdr);
            _keepBrowserDesc = RowDescription(keepRow);
            Add(keepRow, 68);

            // ---- Videos & RTX Video HDR ----
            Section("Videos / RTX Video HDR", "HDR while videos play – SDR videos too, upgraded by NVIDIA RTX Video HDR.");
            _videoMasterRow = Add(SettingRow("HDR for videos", "On while a video plays, off again afterwards.", _swVideoAuto), 68);
            void Detail(Control c, int height)
            {
                int row = Add(c, height);
                _videoDetailRows.Add((row, height, c));
            }
            Detail(SettingRow("Which videos", "Smart: HDR videos, and SDR ones when RTX Video HDR is on.", _videoMode), 68);
            Detail(SettingRow("When", "Only full screen, or also in a window.", _videoTrigger), 68);
            Detail(SettingRow("In browsers", "Needs the browser script  ·  per browser: Apps tab.", _swVideoBrowsers), 68);
            Detail(SettingRow("In video players", "Per player: Apps tab.", _swVideoPlayers), 68);
            Detail(SettingRow("Wait after the video stops", "Longer = no switch when you pause or skip.", _videoRevert), 68);
            Detail(SettingRow("Not on battery", "Laptops: no video HDR while unplugged.", _swVideoBattery), 68);

            var videoCard = new RoundedPanel { Radius = 10, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(16, 12, 16, 12) };
            videoCard.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, Font = Theme.Small, ForeColor = Theme.Muted,
                Text = RtxVideoStatus.Query().Describe() + " Only the monitor with the video switches, and games always " +
                       "come first. One video should stay in SDR? Tray menu › SDR for this video."
            });
            Detail(videoCard, 84);

            // ---- Display fixes ----
            Section("Display fixes", "Repairs side effects of leaving HDR.");
            Add(SettingRow("Reload color profile", "Re-applies your calibration after HDR (may need admin rights).", _swColorProfile), 68);
            Add(SettingRow("Refresh apps", "Fixes Chrome & co. looking dim after HDR.", _swRefreshApps), 68);

            // ---- Updates ----
            Section("Updates", "Connects to GitHub only to check for updates. Nothing about you is sent.");
            _checkUpdatesButton = new ModernButton { Glyph = Theme.GlyphRefresh, Text = "Check for updates", AutoSize = true };
            _checkUpdatesButton.Click += async (_, _) => await OnCheckForUpdatesAsync();
            var updateRow = SettingRow($"Version {Updater.CurrentVersionText}  ({Updater.BuildDescription})", "Installs a new version with one click.", _checkUpdatesButton);
            _updateDesc = RowDescription(updateRow);
            Add(updateRow, 68);
            Add(SettingRow("Check automatically", "Once a week – only tells you, never installs by itself.", _swAutoUpdate), 68);

            // ---- Advanced ----
            Section("Advanced", "Settings file, log and reset.");
            var advanced = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
            var openConfig = new ModernButton { Glyph = Theme.GlyphDocument, Text = "Open config.json", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var reload = new ModernButton { Glyph = Theme.GlyphRefresh, Text = "Reload config.json", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var openLog = new ModernButton { Glyph = Theme.GlyphFolder, Text = "Open log file", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var reset = new ModernButton { Text = "Reset to defaults", AutoSize = true, Margin = new Padding(0) };
            openConfig.Click += (_, _) => OpenInNotepad(HdrEngine.ConfigPath);
            openLog.Click += (_, _) => OpenInNotepad(HdrEngine.LogPath);
            reload.Click += (_, _) =>
            {
                if (_engine.ReloadConfig()) { LoadSettings(); RebuildGames(); }
                else MessageBox.Show(this, "config.json could not be read. See the Log tab.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            reset.Click += (_, _) => OnResetSettings();
            advanced.Controls.Add(openConfig);
            advanced.Controls.Add(reload);
            advanced.Controls.Add(openLog);
            advanced.Controls.Add(reset);
            Add(advanced, 52);

            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // filler
            return page;
        }

        private static Label SectionDescription(string text) => new()
        {
            UseMnemonic = false,   // "&" must show as "&", not underline the next letter
            Text = text, Font = Theme.Small, ForeColor = Theme.Muted, Dock = DockStyle.Fill, AutoEllipsis = true,
            TextAlign = ContentAlignment.TopLeft, Margin = new Padding(2, 0, 0, 6), AutoSize = false
        };

        /// <summary>The description line of a <see cref="SettingRow"/> (its last label).</summary>
        private static Label RowDescription(RoundedPanel row) => row.Controls.OfType<Label>().Last();

        /// <summary>Shows or hides the detail rows of "Videos / RTX Video HDR" (only shown while the main switch is on).</summary>
        private void SetVideoDetailsVisible(bool visible)
        {
            if (_settingsPage == null) return;
            _settingsPage.SuspendLayout();
            foreach (var (row, height, control) in _videoDetailRows)
            {
                // Same scale as the "Switch to HDR for videos" row, which is never hidden: WinForms DPI-scales row
                // heights (68 -> 102 px at 150 %), so restoring the plain value made these rows smaller than the rest.
                float scale = _settingsPage.RowStyles[_videoMasterRow].Height / 68f;
                _settingsPage.RowStyles[row].Height = visible ? height * scale : 0;
                control.Visible = visible;
            }
            _settingsPage.ResumeLayout(true);
        }

        private void UpdateScriptStatus()
        {
            bool seen = _engine.BrowserScriptSeen;
            if (_scriptStatusLabel == null || _lastScriptSeen == seen) return;
            _lastScriptSeen = seen;
            _scriptStatusLabel.Text = seen
                ? "Detected ✓"
                : "Not detected yet – play a YouTube video, or see the guide.";
            _scriptStatusLabel.ForeColor = seen ? Theme.Muted : Theme.Warn;
        }

        private void OpenScriptGuide()
        {
            const string url = "https://github.com/VantaCode420/SmartHdrManager#browser-script-needed-for-youtube-hdr-and-browser-videos";
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch
            {
                string readme = Path.Combine(AppContext.BaseDirectory, "README.md");
                OpenInNotepad(readme);
            }
        }

        private void OnResetSettings()
        {
            var answer = MessageBox.Show(this,
                "Reset all settings to their defaults?\n\nYour games, their HDR switches and per-game options are kept. " +
                "\"Start with Windows\" is not changed.",
                "Reset to defaults", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            _engine.ResetSettings();
            _applyHotkeys();
            LoadSettings();
            RebuildGames();
        }

        private static Label SectionHeader(string text) => new()
        {
            UseMnemonic = false,   // "&" must show as "&", not underline the next letter
            Text = text, Font = Theme.Section, ForeColor = Theme.Text, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft, Margin = new Padding(2, 0, 0, 2), AutoSize = false
        };

        private static RoundedPanel SettingRow(string title, string description, Control control)
        {
            const int w = CardWidth - 8;   // a little narrower: room for the scrollbar
            const int h = 60;

            var row = new RoundedPanel { Radius = 10, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Size = new Size(w, h) };

            int textWidth = w - 18 - control.Width - 34;
            var titleLabel = new Label { UseMnemonic = false, Text = title, Font = Theme.Semibold, ForeColor = Theme.Text, AutoSize = false, AutoEllipsis = true };
            titleLabel.SetBounds(18, 10, textWidth, 22);

            var descLabel = new Label { UseMnemonic = false, Text = description, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, AutoEllipsis = true };
            descLabel.SetBounds(18, 32, textWidth, 20);

            control.Location = new Point(w - 18 - control.Width, (h - control.Height) / 2);

            row.Controls.Add(control);
            row.Controls.Add(titleLabel);
            row.Controls.Add(descLabel);

            // Explicit layout (DPI-safe): control on the right, text uses the remaining width
            row.Layout += (_, _) =>   // after DPI scaling, see LayoutStatusCard
            {
                int S(int v) => row.LogicalToDeviceUnits(v);
                int rw = row.ClientSize.Width;
                int rh = row.ClientSize.Height;
                control.Location = new Point(rw - S(18) - control.Width, (rh - control.Height) / 2);

                int textWidth2 = Math.Max(0, control.Left - S(16) - S(18));
                int titleHeight = titleLabel.PreferredHeight;
                int descHeight = descLabel.PreferredHeight;
                int top = Math.Max(0, (rh - titleHeight - descHeight) / 2);
                titleLabel.SetBounds(S(18), top, textWidth2, titleHeight);
                descLabel.SetBounds(S(18), top + titleHeight, textWidth2, descHeight);
            };
            return row;
        }

        private Control BuildLogPage()
        {
            var card = new RoundedPanel { Radius = 12, BackColor = Theme.Surface, Padding = new Padding(14, 12, 6, 12), Margin = new Padding(0) };
            _log.BackColor = Theme.Surface;
            _log.ForeColor = Theme.Text;
            _log.Font = Theme.Mono;
            Theme.UseDarkScrollbars(_log);
            card.Controls.Add(_log);
            return card;
        }

        // ------------------------------------------------------------------
        // Events
        // ------------------------------------------------------------------
        private void WireEvents()
        {
            _tabs.SelectedIndexChanged += (_, _) => ShowPage(_tabs.SelectedIndex);
            _pauseButton.Click += (_, _) =>
            {
                // While a manual override is active this button ends it; otherwise it pauses/resumes automation
                if (_engine.OverrideMode != HdrOverride.None) _setOverride(HdrOverride.None, null);
                else _setPaused(!_engine.Paused);
                RefreshView();
            };
            _search.TextChanged += (_, _) => RebuildGames();

            _games.HdrToggled += (_, item) =>
            {
                _engine.SetGameHdr(item.Processes, item.Hdr, item.Name);   // turning it on also undoes "Never HDR"
                if (item.Excluded)
                {
                    BeginInvoke(new Action(RebuildGames));   // not inside the list's own click handler
                    UpdateNeverHdrDescription();
                }
                UpdateGamesCount();
            };
            _engine.LibraryChanged += OnLibraryChanged;
            // _games.AnimationStats reports the scroll frame rate (diagnostics) – not logged in releases: it filled the log
            _swShowOthers.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                _engine.Update(c => c.ShowOtherPrograms = _swShowOthers.Checked);
                RebuildGames();
            };
            _minimumHdr.ValueChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.MinimumHdrSeconds = _minimumHdr.Value); };
            _hotkeyChoice.SelectedIndexChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                string mode = HotkeyModes[Math.Clamp(_hotkeyChoice.SelectedIndex, 0, HotkeyModes.Length - 1)];
                _engine.Update(c => c.HotkeyModifier = mode);
                _applyHotkeys();
            };
            foreach (var (_, sw) in _monitorSwitches)
                sw.CheckedChanged += (_, _) => OnMonitorToggled(sw);

            _swAutostart.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                if (!TrayAndStartupManager.SetAutostart(_swAutostart.Checked))
                {
                    MessageBox.Show(this, "Could not update the Windows startup entry.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _loadingSettings = true;
                    _swAutostart.Checked = TrayAndStartupManager.IsAutostartEnabled();
                    _loadingSettings = false;
                }
            };
            _scanInterval.ValueChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                int days = _scanInterval.Value;
                _engine.Update(c => c.DriveScanIntervalDays = days);
                UpdateScanDescription();
            };
            _swBrowserWindowed.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.EnableHdrForBrowserInWindowedMode = _swBrowserWindowed.Checked); };
            _swKeepBrowserHdr.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.KeepBrowserHdrWhenNotFocused = _swKeepBrowserHdr.Checked); };
            _swColorProfile.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.ReloadColorProfileAfterSdr = _swColorProfile.Checked); };
            _swRefreshApps.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.RefreshAppsAfterSdr = _swRefreshApps.Checked); };
            _fallback.ValueChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.SdrFallbackDelaySeconds = _fallback.Value); };

            _swVideoAuto.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                _engine.Update(c => c.VideoHdrAutomation = _swVideoAuto.Checked);
                UpdateVideoDependents();
            };
            _videoMode.SelectedIndexChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                string mode = VideoModes[Math.Clamp(_videoMode.SelectedIndex, 0, VideoModes.Length - 1)];
                _engine.Update(c => c.VideoHdrMode = mode);
            };
            _videoTrigger.SelectedIndexChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                string trigger = VideoTriggerModes[Math.Clamp(_videoTrigger.SelectedIndex, 0, VideoTriggerModes.Length - 1)];
                _engine.Update(c => c.VideoHdrTrigger = trigger);
            };
            _swVideoBrowsers.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                _engine.Update(c => c.VideoHdrInBrowsers = _swVideoBrowsers.Checked);
                UpdateVideoDependents();
            };
            _swVideoPlayers.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.VideoHdrInPlayers = _swVideoPlayers.Checked); };
            _videoRevert.ValueChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.VideoHdrRevertSeconds = _videoRevert.Value); };
            _swVideoBattery.CheckedChanged += (_, _) => { if (!_loadingSettings) _engine.Update(c => c.VideoHdrDisableOnBattery = _swVideoBattery.Checked); };
            _swKeepDesktopSdr.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                bool on = _swKeepDesktopSdr.Checked;
                _engine.Update(c => c.KeepDesktopSdr = on);
                _engine.Log("CONFIG", on
                    ? "Keep the desktop in SDR: on – HDR turned on by you or another program is switched back to SDR too."
                    : "Keep the desktop in SDR: off – only HDR that this app turned on is switched back.");
            };
            _swAutoUpdate.CheckedChanged += (_, _) =>
            {
                if (_loadingSettings) return;
                bool on = _swAutoUpdate.Checked;
                _engine.Update(c => c.CheckForUpdatesAutomatically = on);
                _engine.Log("UPDATE", on ? "Weekly update check turned on." : "Weekly update check turned off.");
            };

            Resize += (_, _) =>
            {
                // Minimize = hide to tray (no taskbar button, not in Alt-Tab)
                if (WindowState == FormWindowState.Minimized)
                    BeginInvoke(new Action(HideToTray));
                else
                    UpdateContentWidth();
            };

            FormClosing += (_, e) =>
            {
                if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    HideToTray();
                }
            };
        }

        /// <summary>Centres the content in a column of at most <see cref="MaxContentWidth"/> (wide / maximized windows).</summary>
        private void UpdateContentWidth()
        {
            if (_root == null) return;
            int edge = LogicalToDeviceUnits(20);
            int side = Math.Max(edge, (ClientSize.Width - LogicalToDeviceUnits(MaxContentWidth)) / 2);
            var padding = new Padding(side, edge, side, edge);
            if (_root.Padding != padding) _root.Padding = padding;
        }

        // ------------------------------------------------------------------
        // Show / hide
        // ------------------------------------------------------------------
        public void ShowFromTray()
        {
            if (!Visible)
            {
                LoadSettings();
                RebuildGames();
                Show();
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            TopMost = true; TopMost = false;   // reliably bring to front from a background process
            Activate();
            RefreshView();
        }

        public void HideToTray()
        {
            if (IsDisposed) return;
            // Hide first: restoring a minimized window while it is still visible made it flash up before it vanished
            Hide();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }

        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;

        /// <summary>
        /// The title bar's minimize button hides the window straight to the tray – no minimize animation to a taskbar
        /// button that disappears right after. (Win+D / Win+M still minimize; Resize then hides the window.)
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
            {
                HideToTray();
                return;
            }
            base.WndProc(ref m);
        }

        public void AllowExit() => _reallyExit = true;

        // ------------------------------------------------------------------
        // Refresh (called every 500 ms by the tray context while the window is visible)
        // ------------------------------------------------------------------
        public void RefreshView()
        {
            var s = _engine.Status;
            UpdateScriptStatus();

            // Visual state: 0 off, 1 on, 2 paused; +10 / +20 while a manual override (HDR / SDR) is active
            int state = (s.Paused && s.Override == HdrOverride.None ? 2 : s.HdrOn ? 1 : 0) + (int)s.Override * 10;
            if (state != _lastState)
            {
                _lastState = state;

                _statusCard.SetState(state == 2 ? HdrVisualState.Paused : s.HdrOn ? HdrVisualState.On : HdrVisualState.Off);
                _hdrLabel.Text = s.Override switch
                {
                    HdrOverride.ForceHdr => "HDR forced on",
                    HdrOverride.ForceSdr => "SDR forced",
                    _ => state == 2 ? (s.HdrOn ? "HDR on · paused" : "Automation paused")
                         : s.HdrOn ? "HDR is on" : "HDR is off"
                };

                if (s.Override != HdrOverride.None)
                {
                    _pauseButton.Text = "Back to automatic";
                    _pauseButton.Glyph = Theme.GlyphPlay;
                    _pauseButton.Primary = true;
                }
                else if (state == 2)
                {
                    _pauseButton.Text = "Resume automation";
                    _pauseButton.Glyph = Theme.GlyphPlay;
                    _pauseButton.Primary = true;
                }
                else
                {
                    _pauseButton.Text = "Pause automation";
                    _pauseButton.Glyph = Theme.GlyphPause;
                    _pauseButton.Primary = false;
                }
            }

            string detail;
            if (s.Override != HdrOverride.None)
            {
                string length = s.OverrideRemainingMs >= 0
                    ? $"{Math.Max(1, (int)Math.Ceiling(s.OverrideRemainingMs / 60000.0))} min left"
                    : "until you turn it back";
                string shortcut = HotkeySettings.Describe(_engine.Read(c => c.HotkeyModifier), 'O');
                detail = shortcut.Length > 0 ? $"Manual override · {length}  ({shortcut})" : $"Manual override · {length}";
            }
            else if (s.Paused)
            {
                detail = "HDR is not switched automatically until you resume.";
            }
            else if (s.BlockedBy != null)
            {
                detail = $"HDR blocked – {s.BlockedBy} is running (Apps › Recording & streaming)";
            }
            else if (s.LaunchHold)
            {
                detail = s.RunningGame != null ? $"Launch with HDR · {s.RunningGame}" : "Launch with HDR · starting …";
            }
            else if (s.WaitingForGame != null)
            {
                detail = $"{s.WaitingForGame} started · HDR in {s.WaitingSecondsLeft} s (game option)";
            }
            else if (s.RunningGame != null)
            {
                detail = $"HDR game running: {s.RunningGame}";
            }
            else if (s.FollowingLauncher != null)
            {
                detail = $"{s.FollowingLauncher} closed · waiting for the game it starts …";
            }
            else if (s.Video != null)
            {
                detail = s.HoldSecondsLeft > 0 ? $"Video stopped · back to SDR in {FormatSeconds(s.HoldSecondsLeft)}" : $"Playing: {s.Video}";
            }
            else if (s.HoldSecondsLeft > 0)
            {
                detail = $"Staying in HDR for {s.HoldSecondsLeft} s more";
            }
            else
            {
                detail = string.IsNullOrEmpty(s.ActiveProcess)
                    ? "Waiting …"
                    : $"Active window: {s.ActiveProcess}{(s.IsFullscreen ? " (fullscreen)" : "")}";
            }
            if (_detailLabel.Text != detail) _detailLabel.Text = detail;

            bool scanning = _scanInFlight || _engine.IsScanning;
            if (_scanButton.Enabled == scanning)
            {
                _scanButton.Enabled = !scanning;
                _scanButton.Text = scanning ? "Scanning…" : "Scan games";
            }

            var lines = _engine.ReadLog(ref _logCursor);
            if (lines.Length > 0)
            {
                _log.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);
                if (_log.TextLength > 200_000)
                    _log.Text = _log.Text[^100_000..];
            }
        }

        // ------------------------------------------------------------------
        // Games page
        // ------------------------------------------------------------------
        /// <summary>45 -> "45 s", 299 -> "4:59 min" (long video delays since 2.5.2).</summary>
        private static string FormatSeconds(int seconds) =>
            seconds < 60 ? $"{seconds} s" : $"{seconds / 60}:{seconds % 60:00} min";

        private void RebuildGames()
        {
            string filter = _search.Text.Trim();
            bool showOthers = _engine.Read(c => c.ShowOtherPrograms);
            // "Never HDR" games are hidden from the list, but a search finds them so they can be turned back on
            var items = _engine.GetGameRows(showOthers, includeExcluded: filter.Length > 0)
                .Where(r => filter.Length == 0 ||
                            r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            r.Subtitle.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(r => new GameItem(r))
                .ToArray();

            // Keeps the selection and scroll position
            _games.SetItems(items, _games.SelectedItem?.Key);
            UpdateGamesCount();
        }

        private void UpdateGamesCount()
        {
            if (IsDisposed) return;
            int enabled = _games.Items.Count(i => i.Hdr);
            _gamesCount.Text = $"{enabled} with HDR · {_games.Items.Count} shown";
        }

        private async void OnScan(object? sender, EventArgs e)
        {
            _scanInFlight = true;
            _scanButton.Enabled = false;
            _scanButton.Text = "Scanning…";
            try { await Task.Run(() => _engine.ScanDrives()); }
            finally
            {
                _scanInFlight = false;
                if (!IsDisposed)
                {
                    _scanButton.Text = "Scan games";
                    _scanButton.Enabled = true;
                    RebuildGames();
                }
            }
        }

        private void OnAddExe()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select the game or program (.exe) that should use HDR",
                Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
                Multiselect = true,
                InitialDirectory = _engine.GetInitialDirectory() ?? string.Empty
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            foreach (var file in dlg.FileNames)
                _engine.AddGame(HdrEngine.CleanExeInput(file), fromHotkey: false, exePath: file);
            RebuildGames();
        }

        private void OnAddRunning()
        {
            var picked = PickRunningProcess();
            if (picked == null) return;
            _engine.AddGame(picked.Value.Name, fromHotkey: false, exePath: picked.Value.Path);
            RebuildGames();
        }

        private void OnExclude()
        {
            if (_games.SelectedItem is not GameItem item)
            {
                MessageBox.Show(this, "Select a game in the list first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _engine.IgnoreProcesses(item.Processes, item.Name);
            RebuildGames();
            UpdateNeverHdrDescription();
        }

        /// <summary>
        /// "Never HDR" games with a way to undo it (2.6.1 – before, excluded games could only be undone in config.json).
        /// The same list as the Games tab: icon, name, source and a switch. Switch on = HDR for the game (like in the
        /// Games tab, it is no longer excluded); Restore = no longer excluded, HDR stays off.
        /// </summary>
        private void OnManageNeverHdr()
        {
            const string prefix = "Never HDR  ·  ";
            var items = _engine.GetExcludedRows()
                .Select(r => new GameItem(new GameRow
                {
                    Key = r.Key, Name = r.Name, Processes = r.Processes, ExePath = r.ExePath, Excluded = true,
                    Subtitle = r.Subtitle.StartsWith(prefix, StringComparison.Ordinal) ? r.Subtitle[prefix.Length..] : r.Subtitle
                }))
                .ToList();

            using var dlg = new Form
            {
                Text = "Never HDR games",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                BackColor = Theme.Back, ForeColor = Theme.Text, Font = Theme.Body,
                Icon = _windowIcon, KeyPreview = true,
                Padding = new Padding(16)
            };
            dlg.HandleCreated += (_, _) => Theme.StyleWindow(dlg.Handle);
            dlg.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dlg.DialogResult = DialogResult.Cancel;
                    e.Handled = e.SuppressKeyPress = true;
                }
            };

            dlg.SuspendLayout();
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));    // title + count
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));    // description
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // the list
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));    // buttons

            // Title on the left, "2 games" on the right (like "6 with HDR · 36 shown" in the Games tab)
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(new Label
            {
                Text = "Never HDR games", Font = Theme.Section, ForeColor = Theme.Text, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(2, 0, 0, 0)
            }, 0, 0);
            var count = new Label { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Margin = new Padding(12, 0, 2, 0) };
            header.Controls.Add(count, 1, 0);
            table.Controls.Add(header, 0, 0);

            table.Controls.Add(new Label
            {
                Text = "Turn a game on to use HDR for it again, or select it and click Restore.",
                Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, Dock = DockStyle.Fill, UseMnemonic = false,
                TextAlign = ContentAlignment.TopLeft, AutoEllipsis = true, Margin = new Padding(2, 0, 2, 6)
            }, 0, 1);

            // The list, in the same rounded card as the Games tab
            var listCard = new RoundedPanel { Radius = 12, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(6) };
            var list = new GameList { Dock = DockStyle.Fill };
            var empty = new Label
            {
                Text = "No games are excluded.\n\"Never HDR\" in the Games tab adds games here.",
                Font = Theme.Body, ForeColor = Theme.Muted, BackColor = Theme.Surface, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter, UseMnemonic = false
            };
            listCard.Controls.Add(list);
            listCard.Controls.Add(empty);
            table.Controls.Add(listCard, 0, 2);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
            var close = new ModernButton { Text = "Close", Primary = true, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
            var restore = new ModernButton { Glyph = Theme.GlyphRefresh, Text = "Restore", AutoSize = true, Margin = new Padding(0) };
            // Own tooltip for this dialog: the main window's tooltip would keep the closed dialog's button alive
            using var tips = new ToolTip();
            tips.SetToolTip(restore, "Remove the selected game from this list – HDR stays off until you turn it on in the Games tab");
            buttons.Controls.Add(close);
            buttons.Controls.Add(restore);
            table.Controls.Add(buttons, 0, 3);

            void ShowItems(List<GameItem> rows)
            {
                list.SetItems(rows, list.SelectedItem?.Key);
                list.Visible = rows.Count > 0;
                empty.Visible = rows.Count == 0;
                restore.Enabled = rows.Count > 0;
                int on = rows.Count(i => i.Hdr);
                count.Text = rows.Count == 1 ? "1 game" : $"{rows.Count} games";
                if (on > 0) count.Text += $" · {on} with HDR again";
            }

            // Switch on: HDR for the game (this also undoes "Never HDR"). Switch off again: back to "Never HDR".
            list.HdrToggled += (_, item) =>
            {
                if (item.Hdr)
                    _engine.SetGameHdr(item.Processes, true, item.Name);
                else
                    _engine.IgnoreProcesses(item.Processes, item.Name);
                int on = list.Items.Count(i => i.Hdr);
                count.Text = (list.Items.Count == 1 ? "1 game" : $"{list.Items.Count} games") + (on > 0 ? $" · {on} with HDR again" : "");
            };
            restore.Click += (_, _) =>
            {
                if (list.SelectedItem is not GameItem item)
                {
                    MessageBox.Show(dlg, "Select a game in the list first.", dlg.Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (!item.Hdr)   // switched on already = not excluded any more
                    _engine.RestoreProcesses(item.Processes, item.Name);
                ShowItems(list.Items.Where(i => i.Key != item.Key).ToList());
            };
            close.Click += (_, _) => dlg.DialogResult = DialogResult.OK;

            dlg.Controls.Add(table);
            dlg.AutoScaleDimensions = new SizeF(96F, 96F);
            dlg.AutoScaleMode = AutoScaleMode.Dpi;
            dlg.ClientSize = new Size(560, 500);
            dlg.ResumeLayout(false);
            ShowItems(items);
            dlg.ShowDialog(this);

            RebuildGames();
            UpdateNeverHdrDescription();
        }

        /// <summary>Settings › Never HDR games: "2 games excluded – undo it here."</summary>
        private void UpdateNeverHdrDescription()
        {
            if (_neverHdrDesc == null || IsDisposed) return;
            int count = _engine.GetExcludedRows().Count;
            _neverHdrDesc.Text = count == 0 ? "None. \"Never HDR\" in the Games tab adds games here."
                : count == 1 ? "1 game excluded – undo it here."
                : $"{count} games excluded – undo it here.";
        }

        private void OnLibraryChanged(object? sender, EventArgs e)
        {
            // Raised on a background thread
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => { if (!IsDisposed) RebuildGames(); })); } catch { }
        }

        /// <summary>Per-game options dialog: delay before HDR turns on, keep HDR on after the game closes.</summary>
        private void OnGameOptions()
        {
            if (_games.SelectedItem is not GameItem item)
            {
                MessageBox.Show(this, "Select a game in the list first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var current = _engine.GetGameOptions(item.Processes);
            int quickExits = _engine.GetQuickExitCount(item.Processes);   // crash hint (2.6)
            int hintHeight = quickExits > 0 ? 56 : 0;
            var delay = new Stepper { Minimum = 0, Maximum = 120, Step = 5, ZeroText = "Off" };
            var keep = new Stepper { Minimum = 0, Maximum = 600, Step = 15, ZeroText = "Off" };
            delay.Value = current.HdrDelaySeconds;
            keep.Value = current.KeepHdrAfterExitSeconds;

            using var dlg = new Form
            {
                Text = $"Game options – {item.Name}",
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                BackColor = Theme.Back, ForeColor = Theme.Text, Font = Theme.Body,
                Icon = _windowIcon, KeyPreview = true,
                Padding = new Padding(16)
            };
            dlg.HandleCreated += (_, _) => Theme.StyleWindow(dlg.Handle);
            dlg.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    dlg.DialogResult = DialogResult.Cancel;
                    e.Handled = e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Enter && dlg.ActiveControl is not ModernButton)
                {
                    dlg.DialogResult = DialogResult.OK;
                    e.Handled = e.SuppressKeyPress = true;
                }
            };

            dlg.SuspendLayout();
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Margin = new Padding(0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, hintHeight));   // "closed right after HDR came on" hint
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));   // hint text for the delay (wraps; scales with DPI)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));   // "Launch with HDR" buttons
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));   // "Launch with HDR" hint
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            table.Controls.Add(new Label
            {
                Text = item.Name, Font = Theme.Section, ForeColor = Theme.Text, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = new Padding(2, 0, 0, 8)
            }, 0, 0);
            if (quickExits > 0)
            {
                table.Controls.Add(new Label
                {
                    Text = $"This game closed right after HDR came on ({quickExits}×). If it crashed: turn off RTX HDR (NVIDIA App › " +
                           "Graphics › Global Settings), or use \"Launch with HDR\" below.",
                    Font = Theme.Small, ForeColor = Theme.Warn, AutoSize = false, Dock = DockStyle.Fill, UseMnemonic = false,
                    TextAlign = ContentAlignment.TopLeft, Margin = new Padding(18, 0, 18, 8)
                }, 0, 1);
            }
            table.Controls.Add(SettingRow("Delay before HDR turns on",
                "Waits after the game starts before HDR comes on.", delay), 0, 2);
            table.Controls.Add(new Label
            {
                Text = "Leave it Off for most games. Use 3–5 s only if a game flickers or crashes while the display " +
                       "switches – or better, Launch with HDR below.",
                Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft, Margin = new Padding(18, 0, 18, 8)
            }, 0, 3);
            table.Controls.Add(SettingRow("Keep HDR on after the game closes",
                "No second black screen when you restart the game quickly.", keep), 0, 4);

            // "Launch with HDR": HDR on first, then the game starts (for games that only check HDR at startup)
            var launch = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(14, 8, 0, 0) };
            var copySteam = new ModernButton { Text = "Copy Steam launch option", AutoSize = true, Margin = new Padding(0, 0, 8, 0) };
            var makeShortcut = new ModernButton { Text = "Create HDR desktop shortcut", AutoSize = true, Margin = new Padding(0), Enabled = !string.IsNullOrEmpty(item.ExePath) };
            copySteam.Click += (_, _) => CopySteamLaunchOption(dlg);
            makeShortcut.Click += (_, _) => CreateHdrShortcut(dlg, item);
            launch.Controls.Add(copySteam);
            launch.Controls.Add(makeShortcut);
            table.Controls.Add(launch, 0, 5);
            table.Controls.Add(new Label
            {
                Text = "Launch with HDR: HDR comes on first, then the game starts – for games that only offer HDR (or " +
                       "don't crash) when HDR is already on. Steam: paste into Properties › Launch options.",
                Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft, Margin = new Padding(18, 4, 18, 0)
            }, 0, 6);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
            var save = new ModernButton { Text = "Save", Primary = true, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
            var cancel = new ModernButton { Text = "Cancel", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
            var reset = new ModernButton { Text = "Reset", AutoSize = true, Margin = new Padding(0) };
            save.Click += (_, _) => dlg.DialogResult = DialogResult.OK;
            cancel.Click += (_, _) => dlg.DialogResult = DialogResult.Cancel;
            reset.Click += (_, _) => { delay.Value = 0; keep.Value = 0; };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(reset);
            table.Controls.Add(buttons, 0, 7);

            dlg.Controls.Add(table);
            dlg.AutoScaleDimensions = new SizeF(96F, 96F);
            dlg.AutoScaleMode = AutoScaleMode.Dpi;
            dlg.ClientSize = new Size(620, 436 + hintHeight);
            dlg.ResumeLayout(false);

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _engine.SetGameOptions(item.Processes,
                new GameOptions { HdrDelaySeconds = delay.Value, KeepHdrAfterExitSeconds = keep.Value }, item.Name);
            RebuildGames();
        }

        // ---- Launch with HDR ----
        private static string LauncherExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SmartHdrManager.exe");

        private static void CopySteamLaunchOption(IWin32Window owner)
        {
            string option = $"\"{LauncherExe}\" --launch %command%";
            try
            {
                Clipboard.SetText(option);
                MessageBox.Show(owner,
                    "Copied:\n\n" + option + "\n\nIn Steam: right-click the game › Properties › General › Launch options, and paste it there.\n\n" +
                    "Keep Smart HDR Manager in this folder – if you move it, copy the launch option again.",
                    "Launch with HDR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Could not copy to the clipboard ({ex.Message}).\n\n{option}", "Launch with HDR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static void CreateHdrShortcut(IWin32Window owner, GameItem item)
        {
            if (string.IsNullOrEmpty(item.ExePath) || !File.Exists(item.ExePath))
            {
                MessageBox.Show(owner, "The game's exe file is not known. Start the game once, or add it with \"Add program\".",
                    "Launch with HDR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            CreateLaunchShortcut(owner, item.Name, item.ExePath);
        }

        /// <summary>
        /// Desktop shortcut "&lt;name&gt; (HDR)" that runs SmartHdrManager.exe --launch "&lt;exe&gt;": HDR on first, then the program.
        /// Files dropped onto the shortcut are passed on to the program (e.g. a video onto a player shortcut).
        /// </summary>
        private static void CreateLaunchShortcut(IWin32Window owner, string displayName, string exePath, bool videoPlayer = false)
        {
            string name = string.Concat(displayName.Split(Path.GetInvalidFileNameChars())).Trim();
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(exePath);
            string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{name} (HDR).lnk");
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available");
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(link);
                shortcut.TargetPath = LauncherExe;
                shortcut.Arguments = $"{(videoPlayer ? "--launch-video" : "--launch")} \"{exePath}\"";
                shortcut.WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
                shortcut.IconLocation = exePath + ",0";
                shortcut.Description = $"Start {displayName} with HDR on (Smart HDR Manager)";
                shortcut.Save();
                MessageBox.Show(owner, $"Shortcut created on the desktop:\n{Path.GetFileName(link)}", "Launch with HDR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"The shortcut could not be created ({ex.Message}).", "Launch with HDR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ------------------------------------------------------------------
        // Apps tab (2.6): every program that is not a game but that the app reacts to, one mode per program.
        // Video players (also: start them with HDR already on, for NVIDIA RTX Video HDR), web browsers, creative apps
        // that need SDR, recording / streaming apps. Programs that are not listed are never touched.
        // ------------------------------------------------------------------
        private Control BuildAppsPage()
        {
            var page = new SmoothScrollTable { ColumnCount = 1, Margin = new Padding(0), AutoScroll = true, Padding = new Padding(0, 0, SystemInformation.VerticalScrollBarWidth, 0) };
            page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Theme.UseDarkScrollbars(page);
            _appsPage = page;
            FillAppsPage();
            return page;
        }

        private void FillAppsPage()
        {
            var page = _appsPage;
            if (page == null || page.IsDisposed) return;

            // No drawing while the rows are rebuilt: otherwise the half-built page flashes for a moment
            bool redrawOff = page.IsHandleCreated;
            if (redrawOff) SendMessage(page.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            page.SuspendLayout();
            foreach (Control c in page.Controls.Cast<Control>().ToList())
            {
                ForgetToolTips(c);   // the shared ToolTip would otherwise keep the old buttons alive
                c.Dispose();
            }
            page.Controls.Clear();
            page.RowStyles.Clear();

            // Row heights in the window's current scale: before WinForms has DPI-scaled the window (first build in
            // the constructor) the plain values are used and scaled with everything else; on later rebuilds the
            // window is already scaled, so the rows have to be too. The tab bar (designed 40 px high) tells which.
            float scale = _tabs.Height / 40f;
            int S(int v) => (int)Math.Round(v * scale);
            void Add(Control c, int height)
            {
                page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(height)));
                page.Controls.Add(c, 0, page.RowStyles.Count - 1);
            }
            RoundedPanel Note(string text)
            {
                var card = new RoundedPanel { Radius = 10, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Padding = new Padding(16, 12, 16, 12) };
                card.Controls.Add(new Label { Dock = DockStyle.Fill, Font = Theme.Small, ForeColor = Theme.Muted, UseMnemonic = false, Text = text });
                return card;
            }
            // A row's control with a "Remove" button in front of it (programs added by hand)
            Control WithRemove(Control main, string process)
            {
                var panel = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
                var remove = new ModernButton { Glyph = Theme.GlyphCancel, Text = "Remove", AutoSize = true, Margin = new Padding(0, 0, 10, 0) };
                _toolTip.SetToolTip(remove, "Remove this program from the list");
                remove.Click += (_, _) =>
                {
                    _engine.RemoveApp(process);
                    BeginInvoke(new Action(FillAppsPage));   // not while this button's click is still running
                };
                remove.Size = remove.GetPreferredSize(Size.Empty);
                main.Margin = new Padding(main.Margin.Left, Math.Max(0, (remove.Height - main.Height) / 2), 0, 0);
                panel.Controls.Add(remove);
                panel.Controls.Add(main);
                panel.Size = panel.PreferredSize;
                return panel;
            }
            FlowLayoutPanel Actions(params Control[] buttons)
            {
                var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
                foreach (var b in buttons) { b.Margin = new Padding(0, 0, 8, 0); row.Controls.Add(b); }
                return row;
            }

            // Installed programs: new ones join the list with their suggested mode (a listed program keeps its mode)
            List<PlayerInfo> players;
            List<FoundApp> found;
            try { players = VideoPlayers.Find(_engine.Read(c => c.HdrPlayerPaths.ToList())); }
            catch { players = new List<PlayerInfo>(); }
            try { found = AppCatalog.Find(); }
            catch { found = new List<FoundApp>(); }
            _engine.AddFoundApps(players.Where(p => !p.Custom).Select(p => (AppCatalog.ProcessOf(p.ExePath), AppCategories.Player, AppModes.Auto))
                .Concat(found.Select(f => (f.Process, f.Category, f.DefaultMode))));
            var rules = _engine.GetAppRules();
            string ModeOf(string process, string category) =>
                rules.TryGetValue(process, out var r) && r.Category == category ? r.Mode : AppModes.Normalize(category, null);

            // Rows of one group: installed catalogue programs + programs added by hand
            List<(string Process, string Name, string Path, bool Added)> GroupRows(string category)
            {
                var list = found.Where(f => f.Category == category)
                                .Select(f => (f.Process, f.Name, f.ExePath, rules.TryGetValue(f.Process, out var r) && r.AddedByUser))
                                .ToList();
                // Added by hand – also a known program picked with "Add app" that was not found automatically (e.g. an
                // unusual install folder): it has an exe path then, and must show up too
                foreach (var kv in rules.Where(x => x.Value.Category == category && (x.Value.AddedByUser || !string.IsNullOrEmpty(x.Value.ExePath))))
                {
                    if (list.Any(x => x.Process.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add((kv.Key, AppCatalog.NameFor(kv.Key, kv.Value.ExePath), kv.Value.ExePath ?? $"{kv.Key}.exe", true));
                }
                return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }

            // ---- Video players ----
            Add(SectionHeader("Video players"), 34);
            Add(SectionDescription("Auto: as in Settings › Videos  ·  HDR only: only HDR videos  ·  Off: not touched (e.g. madVR)."), 30);
            if (players.Count == 0)
                Add(Note("No video player found. Use \"Add player\" below to pick its .exe file."), 60);

            foreach (var player in players)
            {
                var p = player;
                string process = AppCatalog.ProcessOf(p.ExePath);
                var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
                bool isApp = VideoPlayers.IsRegisteredAsApp(p.ExePath);
                bool inMenu = VideoPlayers.IsInContextMenu(p.ExePath);

                var mode = ModeButton(process, AppCategories.Player, ModeOf(process, AppCategories.Player), p.Name, removable: false);

                var setDefault = new ModernButton { Text = "Default app…", AutoSize = true, Primary = true, Margin = new Padding(0, 0, 8, 0) };
                _toolTip.SetToolTip(setDefault, $"Make \"{VideoPlayers.AppName(p)}\" the app for videos: double-click any video and it plays with HDR");
                setDefault.Click += (_, _) => OnSetDefaultPlayer(p);

                var more = new ModernButton { Text = "More ▾", AutoSize = true, Margin = new Padding(0) };
                more.Click += (_, _) =>
                {
                    var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), BackColor = Theme.Surface, ForeColor = Theme.Text, ShowImageMargin = false };
                    menu.Items.Add("Create desktop shortcut", null, (_, _) => CreateLaunchShortcut(this, p.Name, p.ExePath, videoPlayer: true));
                    menu.Items.Add(inMenu ? "Remove right-click entry" : "Add right-click entry (\"Play with HDR\")", null, (_, _) => ToggleContextMenu(p));
                    if (isApp)
                        menu.Items.Add($"Remove \"{VideoPlayers.AppName(p)}\" from Open with", null, (_, _) => OnUnregisterPlayer(p));
                    if (p.Custom)
                    {
                        menu.Items.Add(new ToolStripSeparator());
                        menu.Items.Add("Remove player from this list", null, (_, _) => RemovePlayer(p));
                    }
                    menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
                    menu.Show(more, new Point(0, more.Height));
                };
                buttons.Controls.Add(mode);
                buttons.Controls.Add(setDefault);
                buttons.Controls.Add(more);
                buttons.Size = buttons.PreferredSize;

                var status = new List<string> { p.Custom ? "Added by you" : "Found automatically" };
                if (isApp) status.Add("Open with ✓");
                if (inMenu) status.Add("Right-click ✓");
                Add(SettingRow(p.Name, $"{string.Join("  ·  ", status)}  ·  {p.ExePath}", buttons), 68);
            }

            // Player names typed into config.json by hand (no exe known): mode and "remove" only
            var shownPlayers = new HashSet<string>(players.Select(x => AppCatalog.ProcessOf(x.ExePath)), StringComparer.OrdinalIgnoreCase);
            foreach (var kv in rules.Where(x => x.Value.Category == AppCategories.Player && x.Value.AddedByUser && !shownPlayers.Contains(x.Key))
                                    .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                string name = AppCatalog.NameFor(kv.Key, kv.Value.ExePath);
                var mode = ModeButton(kv.Key, AppCategories.Player, kv.Value.Mode, name, removable: false);
                mode.Margin = new Padding(0);
                Add(SettingRow(name, $"Added by you  ·  {kv.Value.ExePath ?? kv.Key + ".exe"}", WithRemove(mode, kv.Key)), 68);
            }

            var addPlayer = new ModernButton { Glyph = Theme.GlyphAdd, Text = "Add player", AutoSize = true };
            addPlayer.Click += (_, _) => OnAddPlayer();
            Add(Actions(addPlayer), 52);
            Add(Note("Default app…: double-click any video and it plays with HDR already on – best for RTX Video HDR. " +
                     "More: desktop shortcut, right-click entry, remove. Player settings: README."), 76);

            // ---- Web browsers ----
            Add(SectionHeader("Web browsers"), 46);
            Add(SectionDescription("YouTube HDR and browser videos – needs the browser script."), 30);
            var browsers = GroupRows(AppCategories.Browser);
            if (browsers.Count == 0) Add(Note("No browser found."), 60);
            foreach (var (process, name, path, added) in browsers)
            {
                var mode = ModeButton(process, AppCategories.Browser, ModeOf(process, AppCategories.Browser), name, removable: false);
                mode.Margin = new Padding(0);
                Add(SettingRow(name, $"{(added ? "Added by you" : "Found automatically")}  ·  {path}", added ? WithRemove(mode, process) : mode), 68);
            }
            var addBrowser = new ModernButton { Glyph = Theme.GlyphAdd, Text = "Add browser", AutoSize = true };
            addBrowser.Click += (_, _) => OnAddApp(AppCategories.Browser, "Select the web browser (.exe)");
            Add(Actions(addBrowser), 52);

            // ---- Creative apps ----
            Add(SectionHeader("Creative apps"), 46);
            Add(SectionDescription("On: SDR while the app is in front, for accurate colours. Your HDR comes back afterwards."), 30);
            var creative = GroupRows(AppCategories.Creative);
            if (creative.Count == 0) Add(Note("No photo or video editor found. Add one below."), 60);
            foreach (var (process, name, path, added) in creative)
            {
                string proc = process, display = name;
                var sw = new ToggleSwitch { Size = new Size(S(44), S(22)), BackColor = Theme.Surface, Checked = ModeOf(proc, AppCategories.Creative) == AppModes.Sdr };
                sw.CheckedChanged += (_, _) => _engine.SetAppMode(proc, sw.Checked ? AppModes.Sdr : AppModes.Off, display);
                _toolTip.SetToolTip(sw, "On: SDR while this app is in front");
                sw.Margin = new Padding(0);
                Add(SettingRow(name, $"{(added ? "Added by you" : "Found automatically")}  ·  {path}", added ? WithRemove(sw, proc) : sw), 68);
            }
            var addCreative = new ModernButton { Glyph = Theme.GlyphAdd, Text = "Add app", AutoSize = true };
            addCreative.Click += (_, _) => OnAddApp(AppCategories.Creative, "Select the app that needs SDR (.exe)");
            Add(Actions(addCreative), 52);

            // ---- Recording & streaming ----
            Add(SectionHeader("Recording & streaming"), 46);
            Add(SectionDescription("On: no HDR while it runs, so recordings never come out blown out. Your HDR comes back afterwards."), 30);
            var recording = GroupRows(AppCategories.Recording);
            if (recording.Count == 0) Add(Note("No recording app found (OBS Studio, Streamlabs). Add one below."), 60);
            foreach (var (process, name, path, added) in recording)
            {
                string proc = process, display = name;
                var sw = new ToggleSwitch { Size = new Size(S(44), S(22)), BackColor = Theme.Surface, Checked = ModeOf(proc, AppCategories.Recording) == AppModes.NoHdr };
                sw.CheckedChanged += (_, _) => _engine.SetAppMode(proc, sw.Checked ? AppModes.NoHdr : AppModes.Off, display);
                _toolTip.SetToolTip(sw, "On: no HDR while this app runs (games too)");
                sw.Margin = new Padding(0);
                Add(SettingRow(name, $"{(added ? "Added by you" : "Found automatically")}  ·  {path}", added ? WithRemove(sw, proc) : sw), 68);
            }
            var addRecording = new ModernButton { Glyph = Theme.GlyphAdd, Text = "Add app", AutoSize = true };
            addRecording.Click += (_, _) => OnAddApp(AppCategories.Recording, "Select the recording or streaming app (.exe)");
            Add(Actions(addRecording), 52);

            Add(Note("Programs that are not listed here or in Games are never touched. \"Off\" means the same."), 60);

            page.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // filler
            page.ResumeLayout(true);
            if (redrawOff)
            {
                SendMessage(page.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                page.Invalidate(true);
            }
        }

        private static (string Mode, string Short, string Long)[] ModeOptions(string category) => category switch
        {
            AppCategories.Recording => new[]
            {
                (AppModes.NoHdr, "No HDR", "No HDR – games and videos stay in SDR, your HDR comes back afterwards"),
                (AppModes.Off, "Off", "Off – running it changes nothing")
            },
            AppCategories.Browser => new[]
            {
                (AppModes.Auto, "Auto", "Auto – YouTube HDR videos, other videos as set in Settings › Videos"),
                (AppModes.HdrOnly, "HDR only", "HDR only – only real HDR videos turn HDR on"),
                (AppModes.Off, "Off", "Off – this browser never turns HDR on")
            },
            _ => new[]
            {
                (AppModes.Auto, "Auto", "Auto – HDR videos, SDR videos as set in Settings › Videos"),
                (AppModes.HdrOnly, "HDR only", "HDR only – only HDR videos turn HDR on"),
                (AppModes.Off, "Off", "Off – leave HDR alone (the player switches it itself, e.g. madVR)")
            }
        };

        /// <summary>Grey first line of a mode menu: what the choices are about.</summary>
        private static string ModeMenuTitle(string category, string name) => category switch
        {
            AppCategories.Recording => $"While {name} is running:",
            AppCategories.Browser => $"Videos in {name}:",
            _ => $"Videos in {name}:"
        };

        private static string ShortMode(string category, string mode)
        {
            foreach (var (m, shortText, _) in ModeOptions(category))
                if (m == mode) return shortText;
            return mode;
        }

        /// <summary>A button showing the program's mode ("Auto ▾"); a click opens the choices.</summary>
        private ModernButton ModeButton(string process, string category, string mode, string displayName, bool removable)
        {
            string current = mode;
            // Same width for every choice (no jumping); a preferred size, so the row's layout reserves the room for it
            string widest = ModeOptions(category).Select(o => o.Short + "  ▾").OrderByDescending(t => t.Length).First();
            var button = new ModernButton { Margin = new Padding(0, 0, 8, 0), SizingText = widest, Text = ShortMode(category, current) + "  ▾" };
            button.AutoSize = true;
            button.Size = button.GetPreferredSize(Size.Empty);
            _toolTip.SetToolTip(button, "How this program is handled – click to change");
            button.Click += (_, _) =>
            {
                var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), BackColor = Theme.Surface, ForeColor = Theme.Text, ShowImageMargin = false, ShowCheckMargin = true };
                menu.Items.Add(new ToolStripMenuItem(ModeMenuTitle(category, displayName)) { Enabled = false });
                menu.Items.Add(new ToolStripSeparator());
                foreach (var (m, _, longText) in ModeOptions(category))
                {
                    string chosen = m;
                    var item = new ToolStripMenuItem(longText) { Checked = chosen == current };
                    item.Click += (_, _) =>
                    {
                        if (chosen == current) return;
                        current = chosen;
                        _engine.SetAppMode(process, chosen, displayName);
                        if (!button.IsDisposed) button.Text = ShortMode(category, chosen) + "  ▾";
                    };
                    menu.Items.Add(item);
                }
                if (removable)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    menu.Items.Add("Remove from this list", null, (_, _) =>
                    {
                        _engine.RemoveApp(process);
                        BeginInvoke(new Action(FillAppsPage));
                    });
                }
                menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
                menu.Show(button, new Point(0, button.Height));
            };
            return button;
        }

        private void OnAddApp(string category, string title)
        {
            using var dlg = new OpenFileDialog
            {
                Title = title,
                Filter = "Programs (*.exe)|*.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            if (!_engine.AddApp(dlg.FileName, category))
            {
                MessageBox.Show(this, "This program cannot be added.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            FillAppsPage();
        }

        private const int WM_SETREDRAW = 0x000B;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private void ForgetToolTips(Control control)
        {
            _toolTip.SetToolTip(control, null);
            foreach (Control child in control.Controls) ForgetToolTips(child);
        }

        private void OnSetDefaultPlayer(PlayerInfo player)
        {
            if (!VideoPlayers.IsRegisteredAsApp(player.ExePath))
            {
                if (!VideoPlayers.RegisterAsApp(player, LauncherExe, out string error))
                {
                    MessageBox.Show(this, $"\"{VideoPlayers.AppName(player)}\" could not be registered ({error}).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _engine.Log("VIDEO", $"\"{VideoPlayers.AppName(player)}\" registered as an app for video files.");
            }

            MessageBox.Show(this,
                $"\"{VideoPlayers.AppName(player)}\" is now available as an app for videos.\n\n" +
                "Windows opens Settings › Default apps next. Click \"Set default\" at the top to use it for all video types " +
                "(or pick single file types below).\n\n" +
                "Windows does not let programs change this themselves, so this one step is up to you. " +
                "Alternative: right-click a video › Open with › Choose another app › select it › Always.",
                "Default app for videos", MessageBoxButtons.OK, MessageBoxIcon.Information);
            VideoPlayers.OpenDefaultAppsSettings(player.ExePath);
            FillAppsPage();   // shows "Open with ✓"
        }

        private void OnUnregisterPlayer(PlayerInfo player)
        {
            VideoPlayers.UnregisterApp(player.ExePath);
            _engine.Log("VIDEO", $"\"{VideoPlayers.AppName(player)}\" removed from Open with.");
            FillAppsPage();
        }

        private void ToggleContextMenu(PlayerInfo player)
        {
            if (VideoPlayers.IsInContextMenu(player.ExePath))
            {
                VideoPlayers.RemoveFromContextMenu(player.ExePath);
                _engine.Log("VIDEO", $"Right-click \"Play with HDR ({player.Name})\" removed.");
            }
            else if (VideoPlayers.AddToContextMenu(player, LauncherExe, out string error))
            {
                _engine.Log("VIDEO", $"Right-click \"Play with HDR ({player.Name})\" added for video files.");
            }
            else
            {
                MessageBox.Show(this, $"The right-click entry could not be added ({error}).", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            FillAppsPage();
        }

        private void OnAddPlayer()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select the video player (.exe)",
                Filter = "Programs (*.exe)|*.exe",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string file = dlg.FileName;
            _engine.Update(c =>
            {
                if (!c.HdrPlayerPaths.Contains(file, StringComparer.OrdinalIgnoreCase)) c.HdrPlayerPaths.Add(file);
            });
            _engine.AddApp(file, AppCategories.Player);   // also watched for videos (Auto)
            FillAppsPage();
        }

        private void RemovePlayer(PlayerInfo player)
        {
            VideoPlayers.RemoveFromContextMenu(player.ExePath);
            VideoPlayers.UnregisterApp(player.ExePath);
            _engine.Update(c => c.HdrPlayerPaths.RemoveAll(x => string.Equals(x, player.ExePath, StringComparison.OrdinalIgnoreCase)));
            _engine.RemoveApp(AppCatalog.ProcessOf(player.ExePath));   // only if it was added by hand
            FillAppsPage();
        }

        // ---- monitors ----
        private void OnMonitorToggled(ToggleSwitch changed)
        {
            if (_loadingSettings) return;

            var selected = _monitorSwitches.Where(m => m.Switch.Checked && m.Monitor.HdrSupported).Select(m => m.Monitor.DevicePath).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "At least one HDR-capable monitor has to stay selected.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                _loadingSettings = true;
                changed.Checked = true;
                _loadingSettings = false;
                return;
            }

            var connected = new HashSet<string>(_monitorSwitches.Select(m => m.Monitor.DevicePath), StringComparer.OrdinalIgnoreCase);
            _engine.Update(c =>
            {
                // keep selections of monitors that are not connected right now
                var keepDisconnected = (c.SelectedMonitors ?? new List<string>()).Where(p => !connected.Contains(p));
                c.SelectedMonitors = keepDisconnected.Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private (string Name, string? Path)? PickRunningProcess()
        {
            var rows = new List<(string Name, string Title, string? Path)>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    {
                        string? path = ProcessPaths.TryGetExePath(p.Id);   // limited access only, never reads process memory
                        rows.Add((p.ProcessName.ToLowerInvariant(), p.MainWindowTitle, path));
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            rows = rows.GroupBy(r => r.Name).Select(g => g.First()).OrderBy(r => r.Name).ToList();

            using var dlg = new Form
            {
                Text = "Add a running program", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
                Padding = new Padding(12)
            };
            dlg.SuspendLayout();
            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            foreach (var r in rows)
            {
                string t = r.Title.Length > 60 ? r.Title[..57] + "..." : r.Title;
                list.Items.Add($"{r.Name}   —   {t}");
            }
            var ok = new Button { Text = "Add", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(ok);
            list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0) dlg.DialogResult = DialogResult.OK; };
            dlg.Controls.Add(list);
            dlg.Controls.Add(bottom);
            dlg.AutoScaleDimensions = new SizeF(96F, 96F);
            dlg.AutoScaleMode = AutoScaleMode.Dpi;
            dlg.ClientSize = new Size(560, 420);
            dlg.ResumeLayout(false);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            dlg.Icon = _windowIcon;
            Theme.ApplyForm(dlg);

            if (dlg.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0) return null;
            return (rows[list.SelectedIndex].Name, rows[list.SelectedIndex].Path);
        }

        // ------------------------------------------------------------------
        // Settings page
        // ------------------------------------------------------------------
        private void LoadSettings()
        {
            _loadingSettings = true;
            try
            {
                _swAutostart.Checked = TrayAndStartupManager.IsAutostartEnabled();
                _scanInterval.Value = _engine.Read(c => c.DriveScanIntervalDays);
                UpdateScanDescription();
                _swShowOthers.Checked = _engine.Read(c => c.ShowOtherPrograms);
                _minimumHdr.Value = _engine.Read(c => c.MinimumHdrSeconds);
                _hotkeyChoice.SelectedIndex = Math.Max(0, Array.IndexOf(HotkeyModes, _engine.Read(c => HotkeySettings.Normalize(c.HotkeyModifier))));

                // Monitors: explicit selection if set, otherwise "primary only" / "all HDR monitors"
                var selection = _engine.Read(c => (c.SelectedMonitors ?? new List<string>()).ToList());
                bool onlyPrimary = _engine.Read(c => c.OnlyPrimaryMonitor);
                foreach (var (monitor, sw) in _monitorSwitches)
                {
                    sw.Checked = monitor.HdrSupported && (selection.Count > 0
                        ? selection.Contains(monitor.DevicePath, StringComparer.OrdinalIgnoreCase)
                        : !onlyPrimary || monitor.IsPrimary);
                }
                _swBrowserWindowed.Checked = _engine.Read(c => c.EnableHdrForBrowserInWindowedMode);
                _swKeepBrowserHdr.Checked = _engine.Read(c => c.KeepBrowserHdrWhenNotFocused);
                _swColorProfile.Checked = _engine.Read(c => c.ReloadColorProfileAfterSdr);
                _swRefreshApps.Checked = _engine.Read(c => c.RefreshAppsAfterSdr);
                _fallback.Value = _engine.Read(c => c.SdrFallbackDelaySeconds);
                _swKeepDesktopSdr.Checked = _engine.Read(c => c.KeepDesktopSdr);
                _swAutoUpdate.Checked = _engine.Read(c => c.CheckForUpdatesAutomatically);

                _swVideoAuto.Checked = _engine.Read(c => c.VideoHdrAutomation);
                _videoMode.SelectedIndex = Math.Max(0, Array.IndexOf(VideoModes, _engine.Read(c => VideoHdrModes.Normalize(c.VideoHdrMode))));
                _videoTrigger.SelectedIndex = Math.Max(0, Array.IndexOf(VideoTriggerModes, _engine.Read(c => VideoTriggers.Normalize(c.VideoHdrTrigger))));
                _swVideoBrowsers.Checked = _engine.Read(c => c.VideoHdrInBrowsers);
                _swVideoPlayers.Checked = _engine.Read(c => c.VideoHdrInPlayers);
                _videoRevert.Value = _engine.Read(c => c.VideoHdrRevertSeconds);
                _swVideoBattery.Checked = _engine.Read(c => c.VideoHdrDisableOnBattery);
                UpdateVideoDependents();
                _lastScriptSeen = null;
                UpdateScriptStatus();
                UpdateNeverHdrDescription();
            }
            finally { _loadingSettings = false; }
        }

        /// <summary>
        /// The video detail rows are only shown while video switching is on. With video automation for browsers on,
        /// the two older YouTube switches no longer apply – their description says why instead of just greying out.
        /// </summary>
        private void UpdateVideoDependents()
        {
            SetVideoDetailsVisible(_swVideoAuto.Checked);

            bool videoHandlesBrowsers = _swVideoAuto.Checked && _swVideoBrowsers.Checked;
            _swBrowserWindowed.Enabled = !videoHandlesBrowsers;
            _swKeepBrowserHdr.Enabled = !videoHandlesBrowsers;
            const string handled = "Set by \"HDR for videos\" below.";
            if (_browserWindowedDesc != null)
                _browserWindowedDesc.Text = videoHandlesBrowsers ? handled : "Not only when the video is full screen.";
            if (_keepBrowserDesc != null)
                _keepBrowserDesc.Text = videoHandlesBrowsers ? handled : "While the HDR video plays and you use another window.";
        }

        /// <summary>"Looks for newly installed games · last scan: 3 days ago".</summary>
        private void UpdateScanDescription()
        {
            if (_scanIntervalDesc == null) return;
            DateTime? last = _engine.Read(c => c.LastDriveScanUtc);
            string when = last == null ? "not scanned yet"
                : (DateTime.UtcNow - last.Value).TotalDays >= 2 ? $"last scan {(int)(DateTime.UtcNow - last.Value).TotalDays} days ago"
                : (DateTime.UtcNow - last.Value).TotalHours >= 20 ? "last scan yesterday"
                : "last scan today";
            _scanIntervalDesc.Text = _scanInterval.Value < 0
                ? $"Off – use \"Scan games\" in the Games tab  ·  {when}"
                : $"Searches your drives for new games  ·  {when}";
        }

        // ---- Check for updates (Settings › Updates) ----
        private bool _checkingUpdates;

        private async Task OnCheckForUpdatesAsync()
        {
            if (_checkingUpdates) return;
            _checkingUpdates = true;
            var button = _checkUpdatesButton;
            if (button != null)
            {
                button.Enabled = false;
                button.Text = "Checking…";
            }
            try
            {
                var info = await Updater.CheckAsync(System.Threading.CancellationToken.None);
                _engine.Update(c => c.LastUpdateCheckUtc = DateTime.UtcNow);
                if (IsDisposed) return;
                if (!info.IsNewer)
                {
                    _engine.Log("UPDATE", $"Version {Updater.CurrentVersionText} is the latest version.");
                    if (_updateDesc != null) _updateDesc.Text = $"Up to date ✓  (checked {DateTime.Now:HH:mm})";
                    return;
                }

                _engine.Log("UPDATE", $"Version {info.VersionText} is available.");
                if (_updateDesc != null) _updateDesc.Text = $"Version {info.VersionText} is available.";
                bool restart;
                using (var dlg = new UpdateDialog(info, _engine, _windowIcon))
                {
                    dlg.ShowDialog(this);
                    restart = dlg.RestartRequested;
                }
                // The new version has been started and waits for this one: exit after this click has been handled
                if (restart && !IsDisposed) BeginInvoke(new Action(_exitApp));
            }
            catch (Exception ex)
            {
                _engine.Log("UPDATE", $"Update check failed: {ex.Message}");
                if (!IsDisposed)
                {
                    if (_updateDesc != null) _updateDesc.Text = "Could not check for updates – see the Log tab.";
                    MessageBox.Show(this, $"Could not check for updates:\n{ex.Message}", "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                _checkingUpdates = false;
                if (button != null && !button.IsDisposed)
                {
                    button.Enabled = true;
                    button.Text = "Check for updates";
                }
            }
        }

        private void OpenInNotepad(string path)
        {
            try
            {
                if (!File.Exists(path)) { MessageBox.Show(this, "File does not exist yet:\n" + path, Text); return; }
                Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
