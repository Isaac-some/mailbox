using System.Security.Cryptography;
using System.Text;
using MailAssistant.Updates;
using MailArchiver.Services;

namespace MailArchiver.Tests.LocalApp;

public sealed class DesktopUpdateTests
{
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
