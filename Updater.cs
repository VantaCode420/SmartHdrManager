using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SmartHdrManager
{
    // =========================================================================
    // CHECK FOR UPDATES (2.6)
    // Only when you click "Check for updates" (or turn on the weekly check, off by default). Contacts api.github.com
    // for the latest release and github.com for the download – nothing is sent except what every browser sends.
    // The download is only installed if its SHA256 matches the SHA256SUMS file of the same release.
    // =========================================================================

    /// <summary>The latest release on GitHub.</summary>
    public sealed class UpdateInfo
    {
        public Version Version { get; init; } = new(0, 0, 0);
        public string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
        public string Notes { get; init; } = string.Empty;
        public string PageUrl { get; init; } = Updater.ReleasesPage;
        public string? ZipUrl { get; init; }                         // the package that fits this copy (x64 / ARM64, standalone / small)
        public string ZipName { get; init; } = string.Empty;
        public long ZipSize { get; init; }
        public List<string> SumsUrls { get; init; } = new();         // SHA256SUMS.txt (and SHA256SUMS-x64.txt / -arm64.txt)
        public bool IsNewer => Version > Updater.CurrentVersion;
    }

    public static class Updater
    {
        public const string ReleasesPage = "https://github.com/VantaCode420/SmartHdrManager/releases";
        private const string LatestApi = "https://api.github.com/repos/VantaCode420/SmartHdrManager/releases/latest";
        private const string ExeName = "SmartHdrManager.exe";

        public static Version CurrentVersion { get; } = ThreeParts(typeof(Updater).Assembly.GetName().Version);
        public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

        /// <summary>
        /// Standalone build (runs without .NET installed) or the small one: written into the exe by the build
        /// (csproj "BuildFlavor"). Unknown: standalone – that package runs on every PC.
        /// </summary>
        public static bool IsStandalone { get; } = ReadStandalone();

        private static Version ThreeParts(Version? v) => v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        private static bool ReadStandalone()
        {
            try
            {
                foreach (var a in typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
                    if (a.Key == "BuildFlavor") return !string.Equals(a.Value, "small", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return true;
        }

        private static string Rid => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

        /// <summary>"x64, standalone" – which package this copy is (Settings › Updates, and the one an update downloads).</summary>
        public static string BuildDescription => $"{Rid.Substring(4)}, {(IsStandalone ? "standalone" : "small")}";

        private static HttpClient CreateClient(TimeSpan timeout)
        {
            var http = new HttpClient { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"SmartHdrManager/{CurrentVersionText}");   // required by the GitHub API
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        /// <summary>Reads the latest release from GitHub. Throws with a readable message when that fails.</summary>
        public static async Task<UpdateInfo> CheckAsync(CancellationToken ct)
        {
            using var http = CreateClient(TimeSpan.FromSeconds(20));
            using var response = await http.GetAsync(LatestApi, ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status == 404) throw new InvalidOperationException("No release found on GitHub.");
            if (status == 403 || status == 429) throw new InvalidOperationException("GitHub refused the request for now (too many requests). Try again later.");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub answered with error {status}.");
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json);
        }

        private static UpdateInfo Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = Text(root, "tag_name");
            Version version = ParseVersion(tag) ?? throw new InvalidOperationException($"The latest release has an unknown version ('{tag}').");

            string page = Text(root, "html_url");
            if (!IsGitHubUrl(page)) page = ReleasesPage;

            string rid = Rid;
            string wanted = IsStandalone ? $"-{rid}-standalone.zip" : $"-{rid}.zip";
            string? zipUrl = null;
            string zipName = string.Empty;
            long zipSize = 0;
            string? sumsAll = null, sumsArch = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = Text(asset, "name");
                    string url = Text(asset, "browser_download_url");
                    if (name.Length == 0 || !IsGitHubUrl(url) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;

                    if (name.StartsWith("SmartHdrManager-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = url;
                        zipName = name;
                        zipSize = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out long bytes) ? bytes : 0;
                    }
                    else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        sumsAll = url;
                    }
                    else if (name.Equals($"SHA256SUMS-{rid.Substring(4)}.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        sumsArch = url;
                    }
                }
            }

            var sums = new List<string>();
            if (sumsAll != null) sums.Add(sumsAll);
            if (sumsArch != null) sums.Add(sumsArch);

            string notes = Text(root, "body").Replace("\r\n", "\n").Replace("\n", Environment.NewLine).Trim();
            if (notes.Length > 6000) notes = notes.Substring(0, 6000) + Environment.NewLine + "…";

            return new UpdateInfo
            {
                Version = version, Notes = notes, PageUrl = page,
                ZipUrl = zipUrl, ZipName = zipName, ZipSize = zipSize, SumsUrls = sums
            };
        }

        private static string Text(JsonElement e, string property) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? string.Empty
                : string.Empty;

        /// <summary>"v2.6.1" / "2.6.1" / "v2.6.1-beta" -> 2.6.1</summary>
        private static Version? ParseVersion(string tag)
        {
            string t = tag.Trim().TrimStart('v', 'V');
            int dash = t.IndexOfAny(new[] { '-', '+', ' ' });
            if (dash >= 0) t = t.Substring(0, dash);
            return Version.TryParse(t, out var v) ? ThreeParts(v) : null;
        }

        private static bool IsGitHubUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);

        public static void OpenReleasePage(string? url = null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url != null && IsGitHubUrl(url) ? url : ReleasesPage) { UseShellExecute = true })?.Dispose();
            }
            catch { }
        }

        private static string UpdateFolder => Path.Combine(Path.GetTempPath(), "SmartHdrManager-update");

        /// <summary>
        /// Downloads the package, checks it against SHA256SUMS (mismatch or no checksum = nothing is installed) and
        /// unpacks it. Returns the folder with the new SmartHdrManager.exe.
        /// </summary>
        public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<string> progress, CancellationToken ct)
        {
            if (info.ZipUrl == null)
                throw new InvalidOperationException($"The release has no package for this copy ({Rid}, {(IsStandalone ? "standalone" : "small")}).");
            if (info.SumsUrls.Count == 0)
                throw new InvalidOperationException("The release has no SHA256SUMS file, so the download cannot be checked. It is not installed automatically.");

            string dir = UpdateFolder;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);

            using var http = CreateClient(TimeSpan.FromMinutes(15));

            // 1. The expected checksum (small text file of the same release)
            progress.Report("Reading the checksums …");
            string? expected = null;
            foreach (string url in info.SumsUrls)
            {
                string sums = await http.GetStringAsync(url, ct).ConfigureAwait(false);
                expected = FindHash(sums, info.ZipName);
                if (expected != null) break;
            }
            if (expected == null)
                throw new InvalidOperationException($"SHA256SUMS does not list {info.ZipName}, so the download cannot be checked. It is not installed automatically.");

            // 2. The package
            string zip = Path.Combine(dir, info.ZipName);
            using (var response = await http.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The download failed (error {(int)response.StatusCode}).");
                long total = response.Content.Headers.ContentLength ?? info.ZipSize;
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(zip);
                var buffer = new byte[81920];
                long done = 0, reported = -1;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    long step = total > 0 ? done * 100 / total : done / 1_000_000;
                    if (step != reported)
                    {
                        reported = step;
                        progress.Report(total > 0 ? $"Downloading … {step} %" : $"Downloading … {step} MB");
                    }
                }
            }

            // 3. Check it
            progress.Report("Checking the download (SHA256) …");
            string actual;
            await using (var file = File.OpenRead(zip))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The download does not match the SHA256 checksum of the release. Nothing was installed – " +
                                                    "please download the update from the release page.");

            // 4. Unpack
            progress.Report("Unpacking …");
            string files = Path.Combine(dir, "files");
            ZipFile.ExtractToDirectory(zip, files);
            string exe = Path.Combine(files, ExeName);
            if (!File.Exists(exe))
                exe = Directory.GetFiles(files, ExeName, SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new InvalidOperationException($"{ExeName} is missing in the package.");
            return Path.GetDirectoryName(exe)!;
        }

        /// <summary>"&lt;64 hex&gt;  name" lines of SHA256SUMS.txt: the hash for <paramref name="fileName"/>, or null.</summary>
        private static string? FindHash(string sums, string fileName)
        {
            foreach (string raw in sums.Split('\n'))
            {
                string line = raw.Trim();
                int space = line.IndexOf(' ');
                if (space != 64) continue;
                string hash = line.Substring(0, 64);
                string name = line.Substring(space).Trim().TrimStart('*');
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase) && hash.All(Uri.IsHexDigit)) return hash;
            }
            return null;
        }

        /// <summary>
        /// Puts the new version in place of the running one: a running exe cannot be overwritten, but it can be renamed –
        /// SmartHdrManager.exe becomes SmartHdrManager.exe.old (deleted by the new version), the new exe and the files
        /// next to it are copied in. Settings are in %AppData% (or config.json next to the exe in portable mode) and are
        /// not touched. Returns null when done, otherwise the reason why not (then nothing was changed).
        /// </summary>
        public static string? Install(string sourceDir, out string exePath)
        {
            exePath = Environment.ProcessPath ?? string.Empty;
            if (exePath.Length == 0 || !File.Exists(exePath)) return "The location of this app is unknown.";
            string dir = Path.GetDirectoryName(exePath)!;
            if (!CanWrite(dir))
                return $"The app's folder is not writable ({dir}) – for example in Program Files. Please download the update from the release page.";

            string old = exePath + ".old";
            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(exePath, old);
            }
            catch (Exception ex)
            {
                return $"The app could not be replaced ({ex.Message}).";
            }

            try
            {
                File.Copy(Path.Combine(sourceDir, ExeName), exePath, overwrite: false);
            }
            catch (Exception ex)
            {
                try { File.Move(old, exePath, overwrite: true); } catch { }   // put the old version back
                return $"The new version could not be copied ({ex.Message}). Nothing was changed.";
            }

            foreach (string name in new[] { "README.md", "CHANGELOG.md", "LICENSE.txt", "SmartHdrManager_YouTube.user.js" })
            {
                string source = Path.Combine(sourceDir, name);
                if (!File.Exists(source)) continue;
                try { File.Copy(source, Path.Combine(dir, name), overwrite: true); } catch { }
            }
            return null;
        }

        private static bool CanWrite(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, $"SmartHdrManager.update-check-{Environment.ProcessId}.tmp");
                using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The installer's AppId (installer\SmartHdrManager.iss): its entry in Settings › Apps › Installed apps.</summary>
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{2D815E3F-DDE7-4630-9749-D3885F39D122}_is1";

        /// <summary>
        /// Installed with the setup (per user): keeps the version shown in Settings › Apps › Installed apps in step after
        /// "Check for updates" (or files replaced by hand) put a newer exe into the install folder. Does nothing for
        /// unzipped copies, and for an install for all users (that entry is not writable without administrator rights).
        /// </summary>
        public static void SyncInstalledVersion()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
                if (key?.GetValue("InstallLocation") is not string location) return;
                if (!string.Equals(location.TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                if (key.GetValue("DisplayVersion") as string != CurrentVersionText) key.SetValue("DisplayVersion", CurrentVersionText);
            }
            catch { }
        }

        /// <summary>After an update: deletes SmartHdrManager.exe.old and the download (in the background, with retries).</summary>
        public static void CleanUp()
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            string old = exe + ".old";
            string temp = UpdateFolder;
            if (!File.Exists(old) && !Directory.Exists(temp)) return;
            Task.Run(async () =>
            {
                for (int i = 0; i < 10; i++)   // the old version may still be closing
                {
                    try
                    {
                        if (File.Exists(old)) File.Delete(old);
                        if (Directory.Exists(temp)) Directory.Delete(temp, true);
                        return;
                    }
                    catch { }
                    await Task.Delay(2000).ConfigureAwait(false);
                }
            });
        }
    }

    /// <summary>"Version x is available" – release notes, Update now / Release page / Later.</summary>
    internal sealed class UpdateDialog : Form
    {
        private readonly UpdateInfo _info;
        private readonly HdrEngine _engine;
        private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, Font = Theme.Small, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
        private readonly ModernButton _update = new() { Text = "Update now", Primary = true, AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        private readonly ModernButton _page = new() { Text = "Release page", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        private readonly ModernButton _later = new() { Text = "Later", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        private CancellationTokenSource? _cts;
        private bool _busy;

        /// <summary>The new version is installed and started: the caller exits this one (after the dialog has closed).</summary>
        public bool RestartRequested { get; private set; }

        public UpdateDialog(UpdateInfo info, HdrEngine engine, Icon? icon)
        {
            _info = info;
            _engine = engine;

            Text = "Update available";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = true;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            Padding = new Padding(16);
            if (icon != null) Icon = icon;
            HandleCreated += (_, _) => Theme.StyleWindow(Handle);

            SuspendLayout();
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(0) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

            table.Controls.Add(new Label
            {
                Text = $"Smart HDR Manager {info.VersionText} is available", Font = Theme.Section, ForeColor = Theme.Text,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false
            }, 0, 0);
            table.Controls.Add(new Label
            {
                Text = $"You have {Updater.CurrentVersionText}. Your settings and games are kept.", Font = Theme.Small, ForeColor = Theme.Muted,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false
            }, 0, 1);

            var notesCard = new RoundedPanel { Radius = 10, BackColor = Theme.Surface, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 6), Padding = new Padding(12, 10, 4, 10) };
            var notes = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true,
                BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.Small,
                Text = info.Notes.Length > 0 ? info.Notes : "No release notes."
            };
            Theme.UseDarkScrollbars(notes);
            notesCard.Controls.Add(notes);
            table.Controls.Add(notesCard, 0, 2);

            _status.Text = info.ZipUrl != null
                ? $"Download: {info.ZipName}{(info.ZipSize > 0 ? $" ({Math.Max(1, info.ZipSize / 1_000_000)} MB)" : "")} · checked with SHA256 before it is installed"
                : "There is no package for this copy in the release – use the release page.";
            table.Controls.Add(_status, 0, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
            _update.Enabled = info.ZipUrl != null;
            buttons.Controls.Add(_update);
            buttons.Controls.Add(_page);
            buttons.Controls.Add(_later);
            table.Controls.Add(buttons, 0, 4);

            Controls.Add(table);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(600, 480);
            ResumeLayout(false);

            _update.Click += async (_, _) => await UpdateNowAsync();
            _page.Click += (_, _) => Updater.OpenReleasePage(_info.PageUrl);
            _later.Click += (_, _) =>
            {
                if (_busy) _cts?.Cancel();   // "Cancel" while downloading: nothing has been changed yet
                else Close();
            };
            FormClosing += (_, e) =>
            {
                if (_busy) e.Cancel = true;   // never leave a half-finished update behind
            };
        }

        private async Task UpdateNowAsync()
        {
            if (_busy) return;
            _busy = true;
            _update.Enabled = _page.Enabled = false;
            _later.Text = "Cancel";
            _cts = new CancellationTokenSource();
            var progress = new Progress<string>(text => { if (!IsDisposed) _status.Text = text; });
            try
            {
                _engine.Log("UPDATE", $"Downloading {_info.ZipName} ({_info.VersionText}) …");
                string folder = await Updater.DownloadAsync(_info, progress, _cts.Token);
                _engine.Log("UPDATE", "Download checked (SHA256 matches the release) – installing.");
                _later.Enabled = false;   // from here on: no cancelling halfway

                string? problem = Updater.Install(folder, out string exe);
                if (problem != null)
                {
                    _engine.Log("UPDATE", $"Not installed: {problem}");
                    _busy = false;
                    _later.Text = "Later";
                    MessageBox.Show(this, problem, "Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _page.Enabled = _later.Enabled = true;
                    return;
                }

                _engine.Log("UPDATE", $"Installed {_info.VersionText} – restarting.");
                _status.Text = "Restarting …";
                try
                {
                    Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty })?.Dispose();
                }
                catch (Exception ex)
                {
                    // This copy keeps running (from the renamed .old file); the new version starts next time
                    _engine.Log("UPDATE", $"The new version could not be started ({ex.Message}).");
                    _busy = false;
                    MessageBox.Show(this, $"The update is installed, but the new version could not be started ({ex.Message}). " +
                                          "It starts the next time you open Smart HDR Manager (or exit it in the tray and start it again).",
                        "Update", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    Close();
                    return;
                }
                _busy = false;
                RestartRequested = true;   // the new version waits until this one has closed
                Close();
            }
            catch (Exception ex)
            {
                bool cancelled = _cts?.IsCancellationRequested == true;
                string message = cancelled ? "Cancelled." : ex is OperationCanceledException ? "The download timed out." : ex.Message;
                _engine.Log("UPDATE", $"Update not installed: {message}");
                _busy = false;
                if (!IsDisposed)
                {
                    _later.Text = "Later";
                    _update.Enabled = _page.Enabled = _later.Enabled = true;
                    _status.Text = cancelled ? "Cancelled – nothing was changed." : "Update failed – nothing was changed.";
                    if (!cancelled)
                        MessageBox.Show(this, $"{message}\n\nNothing was changed. You can download the update from the release page.", "Update",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }
        }
    }
}
