using System.Diagnostics;
using Sunder.Runtime.Client;

namespace Sunder.App.Services;

internal enum StartupPhase
{
    Theme,
    Cli,
    RuntimeHost,
    RuntimePackages,
    AppPackageActivation,
    ShellComposition,
    MainWindowCreation,
    RuntimeSubscription,
    InitialViewActivation,
    Reveal,
}

internal sealed class StartupAttemptDeadline : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan DefaultRuntimePackagesPhaseBudget = TimeSpan.FromSeconds(45);

    private readonly object _syncRoot = new();
    private readonly TimeSpan _timeout;
    private readonly TimeSpan? _runtimePackagesPhaseBudget;
    private readonly CancellationTokenSource _deadlineCancellation;
    private readonly CancellationTokenSource _linkedCancellation;
    private readonly long _startedAt;
    private Timer? _timer;
    private long _timerGeneration;
    private long _timerStartedAt;
    private TimeSpan _armedTimeout;
    private StartupPhase? _currentPhase;
    private StartupPhase? _expiredPhase;
    private TimeSpan? _currentPhaseBudget;
    private TimeSpan? _expiredPhaseBudget;
    private bool _phaseDeadlineIsEffective;
    private bool _expiredByPhase;
    private bool _expired;
    private bool _completed;
    private bool _disposed;

    public StartupAttemptDeadline(CancellationToken cancellationToken, TimeSpan? timeout = null)
        : this(cancellationToken, timeout ?? DefaultTimeout, runtimePackagesPhaseBudget: null)
    {
    }

    private StartupAttemptDeadline(
        CancellationToken cancellationToken,
        TimeSpan timeout,
        TimeSpan? runtimePackagesPhaseBudget)
    {
        _timeout = timeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Startup timeout must be positive.");
        }
        if (runtimePackagesPhaseBudget is { } phaseBudget && phaseBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runtimePackagesPhaseBudget),
                "Runtime packages startup phase budget must be positive."
            );
        }
        _runtimePackagesPhaseBudget = runtimePackagesPhaseBudget;

        _startedAt = Stopwatch.GetTimestamp();
        _deadlineCancellation = new CancellationTokenSource();
        _linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _deadlineCancellation.Token);
        lock (_syncRoot)
        {
            ArmTimer(_timeout);
        }
    }

    internal static StartupAttemptDeadline CreateForStartup(
        CancellationToken cancellationToken,
        bool openCoreShell,
        int devPackageCount,
        RuntimeClientTransport runtimeTransport)
    {
        ArgumentNullException.ThrowIfNull(runtimeTransport);
        if (openCoreShell || devPackageCount <= 0)
        {
            return new StartupAttemptDeadline(cancellationToken);
        }

        var requestTimeout = runtimeTransport.Policy.RequestTimeout;
        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(runtimeTransport),
                "The Runtime request timeout must be positive for dev package startup."
            );
        }

        var totalBudget = requestTimeout * 1.5;
        var runtimePackagesPhaseBudget = requestTimeout * 1.2;
        return new StartupAttemptDeadline(
            cancellationToken,
            totalBudget > DefaultTimeout ? totalBudget : DefaultTimeout,
            runtimePackagesPhaseBudget > DefaultRuntimePackagesPhaseBudget
                ? runtimePackagesPhaseBudget
                : DefaultRuntimePackagesPhaseBudget
        );
    }

    public CancellationToken Token => _linkedCancellation.Token;

    internal TimeSpan TotalBudget => _timeout;

    public bool HasExpired
    {
        get
        {
            lock (_syncRoot)
            {
                return _expired;
            }
        }
    }

    internal StartupPhase? CurrentPhase
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentPhase;
            }
        }
    }

    internal StartupPhase? ExpiredPhase
    {
        get
        {
            lock (_syncRoot)
            {
                return _expiredPhase;
            }
        }
    }

    internal void EnterPhase(StartupPhase phase, TimeSpan? budget = null)
    {
        Token.ThrowIfCancellationRequested();
        var phaseBudget = budget ?? GetPhaseBudget(phase);
        if (phaseBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "Startup phase budget must be positive.");
        }

        var remaining = _timeout - Stopwatch.GetElapsedTime(_startedAt);
        var cancelImmediately = false;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                throw new InvalidOperationException("Sunder startup was already committed.");
            }
            if (_expired)
            {
                cancelImmediately = true;
            }
            else if (Stopwatch.GetElapsedTime(_timerStartedAt) >= _armedTimeout)
            {
                CaptureExpiration();
                cancelImmediately = true;
            }
            else if (!cancelImmediately)
            {
                _currentPhase = phase;
                _currentPhaseBudget = phaseBudget;
                _phaseDeadlineIsEffective = phaseBudget < remaining;
                if (remaining <= TimeSpan.Zero)
                {
                    CaptureExpiration();
                    cancelImmediately = true;
                }
                else
                {
                    ArmTimer(phaseBudget < remaining ? phaseBudget : remaining);
                }
            }
        }

        if (cancelImmediately)
        {
            _deadlineCancellation.Cancel();
            Token.ThrowIfCancellationRequested();
        }
    }

    internal bool TryCommit()
    {
        var expire = false;
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                return true;
            }
            if (_expired || _linkedCancellation.IsCancellationRequested)
            {
                return false;
            }
            if (Stopwatch.GetElapsedTime(_timerStartedAt) >= _armedTimeout)
            {
                CaptureExpiration();
                expire = true;
            }
            else
            {
                _completed = true;
                _timerGeneration++;
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return true;
            }
        }
        if (expire)
        {
            try
            {
                _deadlineCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        return false;
    }

    public TimeoutException CreateTimeoutException(OperationCanceledException exception)
    {
        StartupPhase? expiredPhase;
        TimeSpan? expiredPhaseBudget;
        bool expiredByPhase;
        lock (_syncRoot)
        {
            expiredPhase = _expiredPhase;
            expiredPhaseBudget = _expiredPhaseBudget;
            expiredByPhase = _expiredByPhase;
        }

        var message = expiredByPhase && expiredPhase is { } phase && expiredPhaseBudget is { } phaseBudget
            ? $"Sunder startup phase '{DescribePhase(phase)}' did not complete within {DescribeTimeout(phaseBudget)}."
            : expiredPhase is { } activePhase
                ? $"Sunder startup did not complete within {DescribeTimeout(_timeout)} while {DescribePhase(activePhase)}."
                : $"Sunder startup did not complete within {DescribeTimeout(_timeout)}.";
        return new TimeoutException(message, exception);
    }

    internal static string DescribePhase(StartupPhase phase) => phase switch
    {
        StartupPhase.Theme => "loading the theme",
        StartupPhase.Cli => "checking the CLI",
        StartupPhase.RuntimeHost => "starting the Runtime Host",
        StartupPhase.RuntimePackages => "loading Runtime packages",
        StartupPhase.AppPackageActivation => "activating App packages",
        StartupPhase.ShellComposition => "composing the shell",
        StartupPhase.MainWindowCreation => "creating the main window",
        StartupPhase.RuntimeSubscription => "subscribing to Runtime events",
        StartupPhase.InitialViewActivation => "preparing package views",
        StartupPhase.Reveal => "revealing the main window",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };

    internal TimeSpan GetPhaseBudget(StartupPhase phase) =>
        phase == StartupPhase.RuntimePackages && _runtimePackagesPhaseBudget is { } budget
            ? budget
            : GetDefaultPhaseBudget(phase);

    private static TimeSpan GetDefaultPhaseBudget(StartupPhase phase) => phase switch
    {
        StartupPhase.Theme => TimeSpan.FromSeconds(5),
        StartupPhase.Cli => TimeSpan.FromSeconds(10),
        StartupPhase.RuntimeHost => TimeSpan.FromSeconds(45),
        StartupPhase.RuntimePackages => DefaultRuntimePackagesPhaseBudget,
        StartupPhase.AppPackageActivation => TimeSpan.FromSeconds(30),
        StartupPhase.ShellComposition => TimeSpan.FromSeconds(10),
        StartupPhase.MainWindowCreation => TimeSpan.FromSeconds(10),
        StartupPhase.RuntimeSubscription => TimeSpan.FromSeconds(10),
        StartupPhase.InitialViewActivation => TimeSpan.FromSeconds(15),
        StartupPhase.Reveal => TimeSpan.FromSeconds(5),
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };

    private static string DescribeTimeout(TimeSpan timeout) => timeout.TotalSeconds >= 1
        ? $"{timeout.TotalSeconds:0} seconds"
        : $"{timeout.TotalMilliseconds:0} milliseconds";

    private void ArmTimer(TimeSpan timeout)
    {
        var previous = _timer;
        var generation = ++_timerGeneration;
        _timerStartedAt = Stopwatch.GetTimestamp();
        _armedTimeout = timeout;
        _timer = new Timer(Expire, generation, timeout, Timeout.InfiniteTimeSpan);
        previous?.Dispose();
    }

    private void Expire(object? state)
    {
        if (state is not long generation)
        {
            return;
        }
        var cancel = false;
        lock (_syncRoot)
        {
            if (_disposed || _completed || _expired || generation != _timerGeneration)
            {
                return;
            }
            var elapsed = Stopwatch.GetElapsedTime(_timerStartedAt);
            if (elapsed < _armedTimeout)
            {
                _timer?.Change(_armedTimeout - elapsed, Timeout.InfiniteTimeSpan);
            }
            else
            {
                CaptureExpiration();
                cancel = true;
            }
        }
        if (cancel)
        {
            try
            {
                _deadlineCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void CaptureExpiration()
    {
        _expired = true;
        _expiredPhase = _currentPhase;
        _expiredPhaseBudget = _currentPhaseBudget;
        _expiredByPhase = _phaseDeadlineIsEffective;
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _completed = true;
            _timerGeneration++;
            timer = _timer;
            _timer = null;
            timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        timer?.Dispose();
        _linkedCancellation.Dispose();
        _deadlineCancellation.Dispose();
    }
}
