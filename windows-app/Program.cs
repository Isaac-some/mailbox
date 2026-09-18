using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MailAssistant.Windows;

internal static class Program
{
    private const string ResetArgument = "--complete-reset";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == ResetArgument && int.TryParse(args[1], out var parentProcessId))
        {
            CompleteReset(parentProcessId, args[2]);
            return;
        }

        using var instanceLock = new Mutex(true, "Local\\MailAssistant", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("邮箱助手已经在运行。", "邮箱助手", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void CompleteReset(int parentProcessId, string dataDirectory)
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            parent.WaitForExit(30_000);
        }
        catch (ArgumentException)
        {
            // The parent has already exited.
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(dataDirectory))
                {
                    Directory.Delete(dataDirectory, recursive: true);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    UseShellExecute = true
                });
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                Thread.Sleep(250);
            }
        }

        MessageBox.Show("无法清空本机数据。请关闭邮箱助手后重试。", "邮箱助手", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

internal sealed class MainForm : Form
{
    private const int LocalPort = 5180;
    private const string AppName = "邮箱助手";

    private enum WindowMode
    {
        Desktop,
        Compact
    }

    private sealed class WindowPreferences
    {
        public string Mode { get; set; } = WindowMode.Desktop.ToString();
        public bool AlwaysOnTop { get; set; }
        public int DesktopWidth { get; set; } = 1280;
        public int DesktopHeight { get; set; } = 820;
        public int CompactWidth { get; set; } = 420;
        public int CompactHeight { get; set; } = 860;
    }

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label _loadingLabel = new()
    {
        Dock = DockStyle.Fill,
        Text = "正在打开邮箱助手...",
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.FromArgb(102, 112, 133),
        Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12)
    };
    private readonly MenuStrip _menuStrip = new() { Dock = DockStyle.Top };
    private readonly ToolStripMenuItem _desktopWindowMenuItem = new("桌面窗口");
    private readonly ToolStripMenuItem _compactWindowMenuItem = new("手机窄窗");
    private readonly ToolStripMenuItem _alwaysOnTopMenuItem = new("始终置顶");

    private readonly string _dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MailAssistant");
    private readonly string _serverDirectory = Path.Combine(AppContext.BaseDirectory, "server");
    private readonly WindowPreferences _windowPreferences;
    private Process? _server;
    private bool _isClosing;
    private bool _resetInProgress;
    private bool _isApplyingWindowBounds;
    private bool _hasAppliedWindowMode;
    private bool _canSaveWindowPreferences;
    private WindowMode _windowMode;

    private string ResetMarkerPath => Path.Combine(_dataDirectory, "factory-reset.request");
    private string WindowPreferencesPath => Path.Combine(_dataDirectory, "window-preferences.json");
    private string LoginUrl => $"http://127.0.0.1:{LocalPort}/Auth/Login";

    public MainForm()
    {
        _windowPreferences = LoadWindowPreferences();
        _windowMode = Enum.TryParse<WindowMode>(_windowPreferences.Mode, ignoreCase: true, out var savedMode)
            ? savedMode
            : WindowMode.Desktop;

        Text = AppName;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.Manual;
        ConfigureWindowMenu();
        Controls.Add(_webView);
        Controls.Add(_loadingLabel);
        Controls.Add(_menuStrip);
        _menuStrip.BringToFront();

        ApplyWindowMode(_windowMode, centered: true);
        ApplyAlwaysOnTop(_windowPreferences.AlwaysOnTop);

        FormClosing += OnFormClosing;
        ResizeEnd += (_, _) => PersistCurrentWindowSize();
        Shown += async (_, _) => await StartAsync();
    }

    private void ConfigureWindowMenu()
    {
        var windowMenu = new ToolStripMenuItem("窗口");

        _desktopWindowMenuItem.Click += (_, _) => ApplyWindowMode(WindowMode.Desktop, centered: false);
        _compactWindowMenuItem.Click += (_, _) => ApplyWindowMode(WindowMode.Compact, centered: false);
        _compactWindowMenuItem.ShortcutKeys = Keys.Control | Keys.Alt | Keys.M;
        _alwaysOnTopMenuItem.Click += (_, _) => ApplyAlwaysOnTop(!TopMost);
        _alwaysOnTopMenuItem.ShortcutKeys = Keys.Control | Keys.Alt | Keys.P;

        windowMenu.DropDownItems.Add(_desktopWindowMenuItem);
        windowMenu.DropDownItems.Add(_compactWindowMenuItem);
        windowMenu.DropDownItems.Add(new ToolStripSeparator());
        windowMenu.DropDownItems.Add(_alwaysOnTopMenuItem);
        _menuStrip.Items.Add(windowMenu);
        MainMenuStrip = _menuStrip;
    }

    private void ApplyWindowMode(WindowMode mode, bool centered)
    {
        if (_hasAppliedWindowMode)
        {
            PersistCurrentWindowSize();
        }

        _windowMode = mode;
        _windowPreferences.Mode = mode.ToString();
        var requestedMinimumSize = mode == WindowMode.Compact
            ? new Size(360, 560)
            : new Size(960, 650);
        var workingArea = _hasAppliedWindowMode
            ? Screen.FromRectangle(Bounds).WorkingArea
            : Screen.FromPoint(Cursor.Position).WorkingArea;
        MinimumSize = new Size(
            Math.Min(requestedMinimumSize.Width, workingArea.Width),
            Math.Min(requestedMinimumSize.Height, workingArea.Height));

        var requestedSize = mode == WindowMode.Compact
            ? new Size(_windowPreferences.CompactWidth, _windowPreferences.CompactHeight)
            : new Size(_windowPreferences.DesktopWidth, _windowPreferences.DesktopHeight);
        var fittedSize = new Size(
            Math.Min(Math.Max(requestedSize.Width, MinimumSize.Width), workingArea.Width),
            Math.Min(Math.Max(requestedSize.Height, MinimumSize.Height), workingArea.Height));
        var currentCenter = centered || !_hasAppliedWindowMode
            ? new Point(workingArea.Left + workingArea.Width / 2, workingArea.Top + workingArea.Height / 2)
            : new Point(Bounds.Left + Bounds.Width / 2, Bounds.Top + Bounds.Height / 2);
        var targetLocation = new Point(
            Math.Clamp(currentCenter.X - fittedSize.Width / 2, workingArea.Left, workingArea.Right - fittedSize.Width),
            Math.Clamp(currentCenter.Y - fittedSize.Height / 2, workingArea.Top, workingArea.Bottom - fittedSize.Height));

        _isApplyingWindowBounds = true;
        WindowState = FormWindowState.Normal;
        Bounds = new Rectangle(targetLocation, fittedSize);
        _isApplyingWindowBounds = false;
        _hasAppliedWindowMode = true;

        UpdateWindowMenu();
        SaveWindowPreferences();
    }

    private void ApplyAlwaysOnTop(bool alwaysOnTop)
    {
        TopMost = alwaysOnTop;
        _windowPreferences.AlwaysOnTop = alwaysOnTop;
        UpdateWindowMenu();
        SaveWindowPreferences();
    }

    private void PersistCurrentWindowSize()
    {
        if (!_hasAppliedWindowMode || _isApplyingWindowBounds || WindowState != FormWindowState.Normal)
        {
            return;
        }

        if (_windowMode == WindowMode.Compact)
        {
            _windowPreferences.CompactWidth = Width;
            _windowPreferences.CompactHeight = Height;
        }
        else
        {
            _windowPreferences.DesktopWidth = Width;
            _windowPreferences.DesktopHeight = Height;
        }

        SaveWindowPreferences();
    }

    private void UpdateWindowMenu()
    {
        _desktopWindowMenuItem.Checked = _windowMode == WindowMode.Desktop;
        _compactWindowMenuItem.Checked = _windowMode == WindowMode.Compact;
        _alwaysOnTopMenuItem.Checked = TopMost;
    }

    private WindowPreferences LoadWindowPreferences()
    {
        try
        {
            if (File.Exists(WindowPreferencesPath))
            {
                return JsonSerializer.Deserialize<WindowPreferences>(File.ReadAllText(WindowPreferencesPath))
                    ?? new WindowPreferences();
            }
        }
        catch (JsonException)
        {
            // Ignore a damaged preference file and restore usable defaults.
        }
        catch (IOException)
        {
            // Preferences must never prevent the mailbox from opening.
        }

        return new WindowPreferences();
    }

    private void SaveWindowPreferences()
    {
        if (!_canSaveWindowPreferences)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var temporaryPath = $"{WindowPreferencesPath}.tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_windowPreferences));
            File.Move(temporaryPath, WindowPreferencesPath, overwrite: true);
        }
        catch (IOException)
        {
            // Window preferences are optional and must not interrupt mail work.
        }
        catch (UnauthorizedAccessException)
        {
            // Window preferences are optional and must not interrupt mail work.
        }
    }

    private async Task StartAsync()
    {
        try
        {
            MigrateExistingDataIfNeeded();
            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(Path.Combine(_dataDirectory, "keys"));
            _canSaveWindowPreferences = true;
            SaveWindowPreferences();

            await InitializeWebViewAsync();
            StartServer();
            await WaitForServerAsync();
        }
        catch (Exception exception)
        {
            ShowFatal($"无法启动本机服务：{exception.Message}");
        }
    }

    private void MigrateExistingDataIfNeeded()
    {
        if (Directory.Exists(_dataDirectory))
        {
            return;
        }

        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = Directory.EnumerateDirectories(baseDirectory)
            .Where(candidate =>
                !string.Equals(candidate, _dataDirectory, StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.Combine(candidate, "mail-archive.sqlite"))
                && File.Exists(Path.Combine(candidate, "credential-encryption.key")))
            .ToArray();

        if (candidates.Length == 1)
        {
            Directory.Move(candidates[0], _dataDirectory);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        var webViewDirectory = Path.Combine(_dataDirectory, "webview");
        var environment = await CoreWebView2Environment.CreateAsync(null, webViewDirectory);
        await _webView.EnsureCoreWebView2Async(environment);
        _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
        _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
    }

    private void StartServer()
    {
        var serverExecutable = Path.Combine(_serverDirectory, "MailArchiver.exe");
        if (!File.Exists(serverExecutable))
        {
            throw new FileNotFoundException("应用服务文件缺失，无法启动。", serverExecutable);
        }

        var credentialKeyPath = Path.Combine(_dataDirectory, "credential-encryption.key");
        if (!File.Exists(credentialKeyPath))
        {
            File.WriteAllText(credentialKeyPath, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = serverExecutable,
            WorkingDirectory = _serverDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Local";
        startInfo.Environment["ASPNETCORE_CONTENTROOT"] = _serverDirectory;
        startInfo.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{LocalPort}";
        startInfo.Environment["MAIL_ASSISTANT_LOCAL_APP"] = "1";
        startInfo.Environment["MAIL_ASSISTANT_DATA_DIRECTORY"] = _dataDirectory;
        startInfo.Environment["MAIL_ASSISTANT_FACTORY_RESET_MARKER"] = ResetMarkerPath;
        startInfo.Environment["ReleaseNotes__AppVersion"] =
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "2.3.3";
        startInfo.Environment["ConnectionStrings__DefaultConnection"] = $"Data Source={Path.Combine(_dataDirectory, "mail-archive.sqlite")}";
        startInfo.Environment["DataProtection__KeyPath"] = Path.Combine(_dataDirectory, "keys");
        startInfo.Environment["CredentialEncryption__KeyFilePath"] = credentialKeyPath;

        _server = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _server.Exited += OnServerExited;
        _server.Start();
    }

    private async Task WaitForServerAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(LoginUrl);
                if (response.IsSuccessStatusCode)
                {
                    _loadingLabel.Visible = false;
                    _webView.Visible = true;
                    _webView.CoreWebView2.Navigate(LoginUrl);
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The server is still starting.
            }
            catch (TaskCanceledException)
            {
                // The server is still starting.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("本机服务启动超时。");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var target) &&
            (target.Host == "127.0.0.1" || target.Host == "localhost" || target.Scheme == "about"))
        {
            return;
        }

        eventArgs.Cancel = true;
        OpenExternalUrl(eventArgs.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        OpenExternalUrl(eventArgs.Uri);
    }

    private static void OpenExternalUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    private void OnServerExited(object? sender, EventArgs eventArgs)
    {
        if (_isClosing || IsDisposed)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (File.Exists(ResetMarkerPath))
            {
                BeginFactoryReset();
                return;
            }

            ShowFatal("本机服务已停止。请重新打开应用。");
        });
    }

    private void BeginFactoryReset()
    {
        if (_resetInProgress)
        {
            return;
        }

        _resetInProgress = true;
        Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = $"--complete-reset {Environment.ProcessId} {QuoteArgument(_dataDirectory)}",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        PersistCurrentWindowSize();
        _isClosing = true;
        if (_server is { HasExited: false })
        {
            _server.Kill(entireProcessTree: true);
        }
    }

    private void ShowFatal(string message)
    {
        if (IsDisposed || _isClosing)
        {
            return;
        }

        _isClosing = true;
        MessageBox.Show(message, $"{AppName}无法继续", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Close();
    }

    private static string QuoteArgument(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
