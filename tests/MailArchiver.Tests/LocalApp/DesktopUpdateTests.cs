using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;
using MailAssistant.Updates;
using MailArchiver.Services;

namespace MailArchiver.Tests.LocalApp;

public sealed class DesktopUpdateTests
{
    [Fact]
    public void Current_version_comes_from_the_running_backend_not_a_display_configuration()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ReleaseNotes:AppVersion"] = "9.9.9" }).Build();
        var service = new DesktopUpdateService(null!, configuration, null!, new DesktopUpdateActivity(), null!);
        Assert.Equal(typeof(DesktopUpdateService).Assembly.GetName().Version!.ToString(3), service.CurrentVersion);
        Assert.NotEqual("9.9.9", service.CurrentVersion);
    }

    [Theory]
    [InlineData("2.4.3", "2.4.1", "ahead")]
    [InlineData("2.4.1", "2.4.1", "current")]
    [InlineData("2.4.0", "2.4.1", "available")]
    public void Update_state_distinguishes_a_local_build_ahead_of_the_published_release(string local, string published, string expected)
    {
        var artifact = new UpdateArtifact("osx-arm64", "update.zip", "", 20, new string('a', 64), "邮箱助手.app");
        var manifest = new UpdateManifest(1, published, "v" + published, false, [artifact], "Old release notes");
        var state = DesktopUpdateService.BuildVersionState(local, manifest, artifact);
        Assert.Equal(expected, state.Phase);
        Assert.Equal(local, state.CurrentVersion);
        Assert.Equal(published, state.LatestVersion);
        if (expected == "ahead") {
            Assert.Null(state.Notes);
            Assert.Contains("本机版本高于 GitHub", state.Message);
            Assert.DoesNotContain("已是最新", state.Message);
        }
    }

    [Fact]
    public void GitHub_api_fallback_constructs_a_fixed_repository_asset_url()
    {
        var metadata = Encoding.UTF8.GetBytes("""{"assets":[{"name":"desktop-update.json","id":42,"url":"https://untrusted.invalid/file"}]}""");
        Assert.Equal("https://api.github.com/repos/Isaac-some/mailbox-releases/releases/assets/42",
            DesktopUpdateService.ResolveGitHubApiAsset(metadata, "desktop-update.json").AbsoluteUri);
        Assert.Throws<InvalidOperationException>(() => DesktopUpdateService.ResolveGitHubApiAsset(metadata, "missing.zip"));
    }

    [Fact]
    public void In_app_update_preserves_installer_uninstall_support_and_prefers_new_package_support()
    {
        var root = Path.Combine(Path.GetTempPath(), "updater-installer-" + Guid.NewGuid().ToString("N"));
        var installed = Path.Combine(root, "installed"); var extracted = Path.Combine(root, "extracted");
        Directory.CreateDirectory(installed); Directory.CreateDirectory(extracted);
        try {
            File.WriteAllText(Path.Combine(installed, "卸载邮箱助手.exe"), "existing-uninstaller");
            UpdateProtocol.PreserveWindowsInstallerSupport(installed, extracted);
            Assert.Equal("existing-uninstaller", File.ReadAllText(Path.Combine(extracted, "卸载邮箱助手.exe")));
            File.WriteAllText(Path.Combine(extracted, "卸载邮箱助手.exe"), "new-uninstaller");
            UpdateProtocol.PreserveWindowsInstallerSupport(installed, extracted);
            Assert.Equal("new-uninstaller", File.ReadAllText(Path.Combine(extracted, "卸载邮箱助手.exe")));
        } finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("2.4.0", "2.3.9", true)]
    [InlineData("2.3.10", "2.3.9", true)]
    [InlineData("2.4.0", "2.4.0", false)]
    [InlineData("2.3.9", "2.4.0", false)]
    [InlineData("garbage", "2.4.0", false)]
    public void Version_comparison_prevents_downgrades(string next, string current, bool expected) =>
        Assert.Equal(expected, UpdateProtocol.IsNewer(next, current));

    [Fact]
    public void Unknown_signer_and_tampered_manifests_are_rejected()
    {
        var payload = Encoding.UTF8.GetBytes("{\"schema\":1,\"version\":\"2.4.0\"}");
        using var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = impostor.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.Throws<InvalidOperationException>(() => UpdateProtocol.VerifyManifest(payload, signature));
    }

    [Theory]
    [InlineData("https://attacker.test/update.zip", "MailAssistant-Windows-x64-v2.4.0", "win-x64")]
    [InlineData("https://github.com/Isaac-some/mailbox-releases/releases/download/v2.4.0/update.zip", "../escape", "win-x64")]
    [InlineData("https://github.com/Isaac-some/mailbox-releases/releases/download/v2.4.0/update.zip", "邮箱助手.app", "linux-x64")]
    public void Invalid_sources_and_roots_are_rejected(string url, string root, string platform)
    {
        var artifact = new UpdateArtifact(platform, "update.zip", url, 20, new string('a', 64), root);
        Assert.Throws<InvalidOperationException>(() => UpdateProtocol.ValidateManifest(new(1, "2.4.0", "v2.4.0", false, [artifact])));
    }

    [Fact]
    public async Task Partial_and_corrupted_downloads_are_rejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var artifact = new UpdateArtifact("win-x64", "update.zip", "", 3,
                Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })), "");
            await UpdateProtocol.VerifyArchiveAsync(path, artifact);
            await File.WriteAllBytesAsync(path, [1, 2]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateProtocol.VerifyArchiveAsync(path, artifact));
            await File.WriteAllBytesAsync(path, [1, 2, 4]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateProtocol.VerifyArchiveAsync(path, artifact));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Active_send_blocks_installation_and_installation_blocks_new_sends()
    {
        var activity = new DesktopUpdateActivity();
        var first = activity.EnterSend();
        var second = activity.EnterSend();
        Assert.False(activity.TryBeginInstall());
        first.Dispose();
        first.Dispose();
        Assert.False(activity.TryBeginInstall());
        second.Dispose();
        Assert.True(activity.TryBeginInstall());
        Assert.Throws<InvalidOperationException>(() => activity.EnterSend());
        Assert.False(activity.TryBeginInstall());
        activity.CancelInstall();
        using var resumed = activity.EnterSend();
    }

    [Fact]
    public async Task Concurrent_send_and_install_cannot_both_acquire()
    {
        for (var i = 0; i < 100; i++)
        {
            var activity = new DesktopUpdateActivity();
            using var start = new ManualResetEventSlim();
            IDisposable? send = null;
            var sending = Task.Run(() => { start.Wait(); try { send = activity.EnterSend(); } catch (InvalidOperationException) { } });
            var installing = Task.Run(() => { start.Wait(); return activity.TryBeginInstall(); });
            start.Set();
            await Task.WhenAll(sending, installing);
            Assert.NotEqual(send is not null, await installing);
            send?.Dispose();
        }
    }
}
