namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeShutdownDeadline : IDisposable
{
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _deadline = new();
    private readonly CancellationTokenRegistration _stoppingRegistration;
    private readonly TimeSpan _timeout;
    private int _started;

    public RuntimeShutdownDeadline(CancellationToken applicationStopping, TimeSpan timeout)
    {
        _timeout = timeout;
        _stoppingRegistration = applicationStopping.Register(Start);
    }

    public CancellationToken Token => _deadline.Token;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            _deadline.CancelAfter(_timeout);
        }
    }

    public void Dispose()
    {
        _stoppingRegistration.Dispose();
        _deadline.Dispose();
    }
}
