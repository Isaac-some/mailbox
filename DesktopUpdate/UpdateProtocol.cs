using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace MailAssistant.Updates;

public sealed record UpdateArtifact(string Platform, string FileName, string Url, long Size, string Sha256, string RootDirectory);
public sealed record UpdateManifest(int Schema, string Version, string Tag, bool Prerelease, UpdateArtifact[] Artifacts);
public sealed record UpdateInstallPlan(string SessionDirectory, string InstallDirectory, string DataDirectory,
    string Platform, int NativeProcessId, int ServerProcessId, string CurrentVersion, string Nonce, int Port);

/// <summary>The same trust checks run in the application and in the detached installer.</summary>
public static class UpdateProtocol
{
    public const string Repository = "Isaac-some/mailbox-releases";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static UpdateManifest VerifyManifest(byte[] bytes, byte[] signature)
    {
        using var key = ECDsa.Create();
        using var stream = typeof(UpdateProtocol).Assembly.GetManifestResourceStream("UpdatePublicKey")
            ?? throw new InvalidOperationException("更新签名公钥缺失。");
        using var reader = new StreamReader(stream);
        key.ImportFromPem(reader.ReadToEnd());
        if (!key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidOperationException("更新签名校验失败，已保留当前版本。");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json)
            ?? throw new InvalidOperationException("更新信息为空。");
        ValidateManifest(manifest);
        return manifest;
    }

    public static void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.Schema != 1 || !Version.TryParse(manifest.Version, out var version) || version.Build < 0
            || !manifest.Tag.StartsWith($"v{manifest.Version}", StringComparison.Ordinal)
            || manifest.Artifacts.Length is < 1 or > 2)
            throw new InvalidOperationException("更新信息格式不受支持。");
        if (manifest.Artifacts.Select(a => a.Platform).Distinct().Count() != manifest.Artifacts.Length)
            throw new InvalidOperationException("更新平台重复。");
        foreach (var artifact in manifest.Artifacts)
        {
            var expectedRoot = artifact.Platform == "osx-arm64" ? "邮箱助手.app" : $"MailAssistant-Windows-x64-v{manifest.Version}";
            if (artifact.Platform is not ("osx-arm64" or "win-x64") || artifact.RootDirectory != expectedRoot
                || Path.GetFileName(artifact.FileName) != artifact.FileName || !artifact.FileName.EndsWith(".zip", StringComparison.Ordinal)
                || artifact.Size is < 1 or > 1_500_000_000 || artifact.Sha256.Length != 64
                || !artifact.Sha256.All(Uri.IsHexDigit))
                throw new InvalidOperationException("更新文件信息无效。");
            var expected = $"https://github.com/{Repository}/releases/download/{manifest.Tag}/{artifact.FileName}";
            if (!string.Equals(expected, artifact.Url, StringComparison.Ordinal))
                throw new InvalidOperationException("更新文件来源无效。");
        }
    }

    public static async Task VerifyArchiveAsync(string path, UpdateArtifact artifact, CancellationToken cancellationToken = default)
    {
        if (new FileInfo(path).Length != artifact.Size)
            throw new InvalidOperationException("更新文件未下载完整，请重新下载。");
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新文件校验失败，请重新下载。");
    }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var next) && Version.TryParse(current, out var installed) && next > installed;

    public static void WriteJson(string path, object value)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
