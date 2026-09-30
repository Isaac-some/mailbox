using System.Text;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services;

public interface IUpstreamMailboxSyncCursorStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);
    Task WriteAsync(string cursor, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Stores only the upstream serverTimestamp cursor; credentials never enter this file.</summary>
public sealed class UpstreamMailboxSyncCursorStore : IUpstreamMailboxSyncCursorStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpstreamMailboxSyncCursorStore(IHostEnvironment environment, IOptions<MailArchiver.Models.UpstreamMailboxSyncOptions> options)
    {
        var configured = options.Value.CursorFilePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _path = configured.Trim();
            return;
        }

        var dataDirectory = Environment.GetEnvironmentVariable("MAIL_ASSISTANT_DATA_DIRECTORY");
        var root = string.IsNullOrWhiteSpace(dataDirectory) ? environment.ContentRootPath : dataDirectory;
        _path = Path.Combine(root, "upstream-mailbox-sync.cursor");
    }

    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path))
                return null;
            var value = await File.ReadAllTextAsync(_path, cancellationToken);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(string cursor, CancellationToken cancellationToken = default)
    {
        if (!TryValidateCursor(cursor))
            throw new ArgumentException("上游 serverTimestamp 不是有效的 Unix 时间戳。", nameof(cursor));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var tempPath = _path + ".tmp";
            await File.WriteAllTextAsync(tempPath, cursor.Trim() + Environment.NewLine, Encoding.UTF8, cancellationToken);
            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryValidateCursor(string value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;
        if (long.TryParse(trimmed, out var timestamp))
            return timestamp > 0;

        // Accept the ISO value written by releases before serverTimestamp was
        // added. It will be sent back as-is and naturally expires after the
        // server rejects it, while new successful syncs migrate to numeric form.
        return DateTimeOffset.TryParse(trimmed, out _);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        finally
        {
            _gate.Release();
        }
    }
}
