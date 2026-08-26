using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KouziMailAssistant.Windows;

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

        using var instanceLock = new Mutex(true, "Local\\KouziMailAssistant", out var isFirstInstance);
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

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label _loadingLabel = new()
    {
        Dock = DockStyle.Fill,
        Text = "正在打开邮箱助手...",
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.FromArgb(102, 112, 133),
        Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12)
    };

    private readonly string _dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KouziMailAssistant");
    private readonly string _serverDirectory = Path.Combine(AppContext.BaseDirectory, "server");
    private Process? _server;
    private string _serverPassword = string.Empty;
    private bool _isClosing;
    private bool _resetInProgress;

    private string ResetMarkerPath => Path.Combine(_dataDirectory, "factory-reset.request");
    private string LoginUrl => $"http://127.0.0.1:{LocalPort}/Auth/Login";

    public MainForm()
    {
        Text = AppName;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 820);
        MinimumSize = new Size(960, 650);
        Controls.Add(_webView);
        Controls.Add(_loadingLabel);

        FormClosing += OnFormClosing;
        Shown += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(Path.Combine(_dataDirectory, "keys"));

            await InitializeWebViewAsync();
            StartServer();
            await WaitForServerAsync();
        }
        catch (Exception exception)
        {
            ShowFatal($"无法启动本机服务：{exception.Message}");
        }
    }

    private async Task InitializeWebViewAsync()
    {
        var webViewDirectory = Path.Combine(_dataDirectory, "webview");
        var environment = await CoreWebView2Environment.CreateAsync(null, webViewDirectory);
        await _webView.EnsureCoreWebView2Async(environment);
        _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
        _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        _webView.CoreWebView2.NavigationCompleted += async (_, _) => await CompleteAutomaticLoginAsync();
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

        _serverPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
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
        startInfo.Environment["KOUZI_LOCAL_APP"] = "1";
        startInfo.Environment["KOUZI_DATA_DIRECTORY"] = _dataDirectory;
        startInfo.Environment["KOUZI_FACTORY_RESET_MARKER"] = ResetMarkerPath;
        startInfo.Environment["ConnectionStrings__DefaultConnection"] = $"Data Source={Path.Combine(_dataDirectory, "mail-archive.sqlite")}";
        startInfo.Environment["DataProtection__KeyPath"] = Path.Combine(_dataDirectory, "keys");
        startInfo.Environment["CredentialEncryption__KeyFilePath"] = credentialKeyPath;
        startInfo.Environment["Authentication__Username"] = "local";
        startInfo.Environment["Authentication__Password"] = _serverPassword;

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

    private async Task CompleteAutomaticLoginAsync()
    {
        var source = _webView.Source;
        if (source is null ||
            !source.AbsolutePath.StartsWith("/Auth/Login", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var username = JsonSerializer.Serialize("local");
        var password = JsonSerializer.Serialize(_serverPassword);
        var script = $$"""
            (() => {
              const form = document.querySelector('form[action="/Auth/Login"], form');
              const username = document.querySelector('input[name="Username"]');
              const password = document.querySelector('input[name="Password"]');
              const remember = document.querySelector('input[name="RememberMe"]');
              if (!form || !username || !password) return;
              username.value = {{username}};
              password.value = {{password}};
              if (remember) remember.checked = true;
              form.submit();
            })();
            """;
        await _webView.CoreWebView2.ExecuteScriptAsync(script);
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
