namespace MailArchiver.Services;

public interface IMailSyncTrigger
{
    void RequestSync();
    Task WaitForNextPollAsync(TimeSpan pollInterval, CancellationToken cancellationToken);
}

public sealed class MailSyncTrigger : IMailSyncTrigger, IDisposable
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _signalLock = new();

    public void RequestSync()
    {
        lock (_signalLock)
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
    }

    public async Task WaitForNextPollAsync(
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        await _signal.WaitAsync(pollInterval, cancellationToken);
    }

    public void Dispose()
    {
        _signal.Dispose();
    }
}
