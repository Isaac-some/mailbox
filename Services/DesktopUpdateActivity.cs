namespace MailArchiver.Services;

/// <summary>Closes the race between checking active sends and starting an installation.</summary>
public sealed class DesktopUpdateActivity
{
    private readonly object _sync = new();
    private int _sends;
    private bool _installing;
    public bool Installing { get { lock (_sync) return _installing; } }

    public IDisposable EnterSend()
    {
        lock (_sync)
        {
            if (_installing) throw new InvalidOperationException("应用即将更新，请重启完成后再发送。");
            _sends++;
            return new Lease(() => { lock (_sync) _sends--; });
        }
    }

    public bool TryBeginInstall()
    {
        lock (_sync)
        {
            if (_installing || _sends > 0) return false;
            _installing = true;
            return true;
        }
    }

    public void CancelInstall() { lock (_sync) _installing = false; }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
