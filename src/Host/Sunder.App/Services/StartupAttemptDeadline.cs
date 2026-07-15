namespace Sunder.App.Services;

internal sealed class StartupAttemptDeadline : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _deadlineCancellation;
    private readonly CancellationTokenSource _linkedCancellation;

    public StartupAttemptDeadline(CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _deadlineCancellation = new CancellationTokenSource(_timeout);
        _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _deadlineCancellation.Token);
    }

    public CancellationToken Token => _linkedCancellation.Token;

    public bool HasExpired => _deadlineCancellation.IsCancellationRequested;

    public TimeoutException CreateTimeoutException(OperationCanceledException exception)
    {
        var timeoutDescription = _timeout.TotalSeconds >= 1
            ? $"{_timeout.TotalSeconds:0} seconds"
            : $"{_timeout.TotalMilliseconds:0} milliseconds";
        return new TimeoutException(
            $"Sunder startup did not complete within {timeoutDescription}. Retry, open the Core Shell, or quit.",
            exception);
    }

    public void Dispose()
    {
        _linkedCancellation.Dispose();
        _deadlineCancellation.Dispose();
    }
}
