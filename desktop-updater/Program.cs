using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using MailAssistant.Updates;

if (args.Length != 1) return 2;
UpdateInstallPlan? plan = null;
string? stage = null;
string? backup = null;
var swapped = false;
try
{
    plan = JsonSerializer.Deserialize<UpdateInstallPlan>(File.ReadAllBytes(args[0]), UpdateProtocol.Json)
        ?? throw new InvalidOperationException("更新安装计划无效。");
    ValidatePlan(plan);
    var manifest = UpdateProtocol.VerifyManifest(
        File.ReadAllBytes(Path.Combine(plan.SessionDirectory, "desktop-update.json")),
        File.ReadAllBytes(Path.Combine(plan.SessionDirectory, "desktop-update.sig")));
    if (!UpdateProtocol.IsNewer(manifest.Version, plan.CurrentVersion)) throw new InvalidOperationException("禁止安装旧版本。");
    var artifact = manifest.Artifacts.Single(a => a.Platform == plan.Platform);
    var archive = Path.Combine(plan.SessionDirectory, "archive.zip");
    await UpdateProtocol.VerifyArchiveAsync(archive, artifact);
    var parent = Directory.GetParent(plan.InstallDirectory)!.FullName;
    stage = Path.Combine(parent, ".mail-assistant-update-" + plan.Nonce);
    backup = Path.Combine(parent, ".mail-assistant-previous-" + plan.Nonce);
    Directory.CreateDirectory(stage);
    ValidateZip(archive, artifact.RootDirectory);
    if (OperatingSystem.IsMacOS())
        await RunAsync("/usr/bin/ditto", ["-x", "-k", archive, stage]);
    else
        ZipFile.ExtractToDirectory(archive, stage);
    var extracted = Path.Combine(stage, artifact.RootDirectory);
    await ValidatePackageAsync(extracted, manifest.Version, plan.Platform);
    // Same-volume staging and a rename test fail BEFORE the running application exits.
    var probe = stage + ".probe";
    Directory.Move(stage, probe);
    Directory.Move(probe, stage);
    await File.WriteAllTextAsync(Path.Combine(plan.SessionDirectory, "installer-ready"), plan.Nonce);
    var approved = false;
    for (var attempt = 0; attempt < 150; attempt++)
    {
        if (File.Exists(Path.Combine(plan.SessionDirectory, "install-aborted")))
            throw new InvalidOperationException("更新已取消。");
        var approval = Path.Combine(plan.SessionDirectory, "install-approved");
        if (File.Exists(approval) && File.ReadAllText(approval) == plan.Nonce) { approved = true; break; }
        await Task.Delay(200);
    }
    if (!approved) throw new InvalidOperationException("更新安装未确认，已取消。");
    if (!await WaitForExitAsync(plan.NativeProcessId, 150) || !await WaitForExitAsync(plan.ServerProcessId, 30))
        throw new InvalidOperationException("应用未能退出，已取消安装。");
    Directory.Move(plan.InstallDirectory, backup);
    try { Directory.Move(extracted, plan.InstallDirectory); swapped = true; }
    catch { Directory.Move(backup, plan.InstallDirectory); throw; }
    var launched = Launch(plan, plan.Nonce);
    var health = await WaitForHealthAsync(plan, manifest.Version, launched);
    if (!health)
    {
        await StopLaunchedAsync(plan, launched);
        throw new InvalidOperationException("新版未能正常启动，已恢复原版本。");
    }
    WriteResult(plan, true, manifest.Version, $"已成功更新至 v{manifest.Version}。", false);
    DeleteRequest(plan);
    // Retain the previous program until startup and readiness have actually succeeded.
    TryDelete(backup);
    TryDelete(stage);
    return 0;
}
catch (Exception exception)
{
    if (plan is not null)
    {
        var rolledBack = false;
        try
        {
            if (swapped && backup is not null && Directory.Exists(backup))
            {
                var failed = stage + ".failed";
                Directory.Move(plan.InstallDirectory, failed);
                Directory.Move(backup, plan.InstallDirectory);
                rolledBack = true;
                TryDelete(failed);
            }
            DeleteRequest(plan);
            WriteResult(plan, false, plan.CurrentVersion,
                rolledBack ? "更新未完成，已恢复原版本，请重试。" : "更新未完成，当前版本已保留，请重试。", rolledBack);
            if (rolledBack || !IsAlive(plan.NativeProcessId)) Launch(plan, null);
            TryDelete(stage);
        }
        catch (Exception recoveryException)
        {
            File.WriteAllText(Path.Combine(plan.SessionDirectory, "recovery-error.txt"), recoveryException.GetType().Name);
        }
        File.WriteAllText(Path.Combine(plan.SessionDirectory, "installer-error.txt"),
            exception is InvalidOperationException ? exception.Message : exception.GetType().Name);
    }
    return 1;
}

static void ValidatePlan(UpdateInstallPlan plan)
{
    var install = Path.GetFullPath(plan.InstallDirectory).TrimEnd(Path.DirectorySeparatorChar);
    var data = Path.GetFullPath(plan.DataDirectory).TrimEnd(Path.DirectorySeparatorChar);
    var session = Path.GetFullPath(plan.SessionDirectory);
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    if (!session.StartsWith(data + Path.DirectorySeparatorChar + "updates" + Path.DirectorySeparatorChar, comparison)
        || data.StartsWith(install + Path.DirectorySeparatorChar, comparison) || data.Equals(install, comparison)
        || install.StartsWith(data + Path.DirectorySeparatorChar, comparison)
        || plan.Nonce.Length != 48 || !plan.Nonce.All(Uri.IsHexDigit)
        || plan.NativeProcessId <= 0 || plan.ServerProcessId <= 0 || plan.Port is < 1024 or > 65535)
        throw new InvalidOperationException("更新目录或进程信息无效。");
    if (!Directory.Exists(install) || new DirectoryInfo(install).LinkTarget is not null)
        throw new InvalidOperationException("请将应用安装到可写的普通目录后再更新。");
    if (OperatingSystem.IsMacOS() && (install.StartsWith("/Volumes/", StringComparison.Ordinal)
        || install.Contains("/AppTranslocation/", StringComparison.Ordinal)))
        throw new InvalidOperationException("请先将邮箱助手拖到“应用程序”后再更新。");
    if (plan.Platform != (OperatingSystem.IsMacOS() ? "osx-arm64" : "win-x64"))
        throw new InvalidOperationException("更新平台与本机不一致。");
}

static void ValidateZip(string path, string root)
{
    using var zip = ZipFile.OpenRead(path);
    if (zip.Entries.Count > 30_000) throw new InvalidOperationException("更新文件数量异常。");
    long expanded = 0;
    foreach (var entry in zip.Entries)
    {
        var name = entry.FullName;
        // ditto's metadata is allowed; all actual files must live below the signed root directory.
        if (name.StartsWith("__MACOSX/", StringComparison.Ordinal)) continue;
        if (name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(p => p is ".." or "." || p.Contains(':'))
            || !(name == root + "/" || name.StartsWith(root + "/", StringComparison.Ordinal)))
            throw new InvalidOperationException("更新包包含无效路径。");
        expanded += entry.Length;
        if (expanded > 4_000_000_000) throw new InvalidOperationException("更新包解压大小异常。");
    }
}

static async Task ValidatePackageAsync(string extracted, string version, string platform)
{
    var server = platform == "osx-arm64" ? Path.Combine(extracted, "Contents", "Resources", "server") : Path.Combine(extracted, "server");
    if (AssemblyName.GetAssemblyName(Path.Combine(server, "MailArchiver.dll")).Version?.ToString(3) != version)
        throw new InvalidOperationException("更新包服务版本不匹配。");
    var launcher = platform == "osx-arm64" ? Path.Combine(extracted, "Contents", "MacOS", "邮箱助手") : Path.Combine(extracted, "邮箱助手.exe");
    if (!File.Exists(launcher) || !Directory.Exists(Path.Combine(server, "wwwroot"))
        || !File.Exists(Path.Combine(server, "updater", platform == "osx-arm64" ? "MailAssistant.Updater" : "MailAssistant.Updater.exe")))
        throw new InvalidOperationException("更新包缺少必要文件。");
    if (platform == "osx-arm64")
    {
        await RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", extracted]);
        var plistVersion = await RunAsync("/usr/libexec/PlistBuddy", ["-c", "Print :CFBundleShortVersionString", Path.Combine(extracted, "Contents", "Info.plist")]);
        if (plistVersion.Trim() != version) throw new InvalidOperationException("更新包应用版本不匹配。");
    }
    else if (FileVersionInfo.GetVersionInfo(launcher).FileVersion != version + ".0")
        throw new InvalidOperationException("更新包应用版本不匹配。");
}

static Process Launch(UpdateInstallPlan plan, string? nonce)
{
    var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
    info.Environment["MAIL_ASSISTANT_UPDATE_NONCE"] = nonce ?? "";
    info.Environment["MAIL_ASSISTANT_UPDATE_PID_FILE"] = Path.Combine(plan.SessionDirectory, "new-native.pid");
    if (Environment.GetEnvironmentVariable("MAIL_ASSISTANT_ISOLATED_PROFILE") == "1")
        info.Environment["MAIL_ASSISTANT_DATA_DIRECTORY"] = plan.DataDirectory;
    else info.Environment.Remove("MAIL_ASSISTANT_DATA_DIRECTORY");
    info.Environment["MAIL_ASSISTANT_PORT"] = plan.Port.ToString();
    if (OperatingSystem.IsMacOS())
    {
        info.FileName = "/usr/bin/open";
        info.ArgumentList.Add("-n");
        // LaunchServices does not inherit the caller's environment; pass relevant app configuration explicitly.
        foreach (var entry in info.Environment.Where(e => e.Key.StartsWith("MAIL_ASSISTANT_")
            || e.Key.Contains("__", StringComparison.Ordinal)))
        {
            info.ArgumentList.Add("--env"); info.ArgumentList.Add(entry.Key + "=" + entry.Value);
        }
        info.ArgumentList.Add(plan.InstallDirectory);
    }
    else info.FileName = Path.Combine(plan.InstallDirectory, "邮箱助手.exe");
    return Process.Start(info) ?? throw new InvalidOperationException("无法重新打开应用。");
}

static async Task<bool> WaitForHealthAsync(UpdateInstallPlan plan, string version, Process launched)
{
    using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
    for (var i = 0; i < 75; i++)
    {
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{plan.Port}/DesktopUpdates/Health");
            using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            var root = json.RootElement;
            if (response.IsSuccessStatusCode && root.GetProperty("nonce").GetString() == plan.Nonce
                && root.GetProperty("version").GetString() == version) return true;
        }
        catch { }
        if (!OperatingSystem.IsMacOS() && launched.HasExited) return false;
        await Task.Delay(1000);
    }
    return false;
}

static async Task StopLaunchedAsync(UpdateInstallPlan plan, Process launched)
{
    if (File.Exists(Path.Combine(plan.SessionDirectory, "new-native.pid"))
        && int.TryParse(File.ReadAllText(Path.Combine(plan.SessionDirectory, "new-native.pid")), out var pid))
    {
        try { using var native = Process.GetProcessById(pid); native.Kill(entireProcessTree: true); await native.WaitForExitAsync(); }
        catch (ArgumentException) { }
    }
    else if (!OperatingSystem.IsMacOS() && !launched.HasExited)
    { launched.Kill(entireProcessTree: true); await launched.WaitForExitAsync(); }
}

static async Task<bool> WaitForExitAsync(int pid, int seconds)
{
    try
    {
        using var process = Process.GetProcessById(pid);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        await process.WaitForExitAsync(timeout.Token);
        return true;
    }
    catch (ArgumentException) { return true; }
    catch (OperationCanceledException) { return false; }
}

static bool IsAlive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

static async Task<string> RunAsync(string file, string[] arguments)
{
    var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    await process.WaitForExitAsync(timeout.Token);
    await error;
    if (process.ExitCode != 0) throw new InvalidOperationException("更新包解压或签名校验失败，请确认安装目录可写后重试。");
    return await output;
}

static void WriteResult(UpdateInstallPlan plan, bool succeeded, string version, string message, bool rolledBack) =>
    UpdateProtocol.WriteJson(Path.Combine(plan.DataDirectory, "update-result.json"),
        new { succeeded, version, message, rolledBack, completedAt = DateTimeOffset.UtcNow });
static void DeleteRequest(UpdateInstallPlan plan)
{
    var path = Path.Combine(plan.DataDirectory, "update-request.json");
    if (File.Exists(path)) File.Delete(path);
}
static void TryDelete(string? path)
{
    try { if (path is not null && Directory.Exists(path)) Directory.Delete(path, true); }
    catch { /* A verified running installation must not be rolled back because cleanup was denied. */ }
}
