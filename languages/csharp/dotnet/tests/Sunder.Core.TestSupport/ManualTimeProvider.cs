namespace Sunder.Core.TestSupport;

public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow;
    private long _timestamp;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _utcNow = start ?? DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        _timestamp = _utcNow.UtcTicks;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _utcNow;
    }

    public override long GetTimestamp()
    {
        lock (_gate) return _timestamp;
    }

    public int PendingTimerCount
    {
        get
        {
            lock (_gate) return _timers.Count(timer => !timer.IsDisposed && timer.IsScheduled);
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ValidateTimeout(dueTime, nameof(dueTime));
        ValidateTimeout(period, nameof(period));
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.ChangeUnderLock(dueTime, period);
        }
        return timer;
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        => Task.Delay(delay, this, cancellationToken);

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        DateTimeOffset target;
        lock (_gate) target = _utcNow + duration;

        var callbacks = 0;
        while (true)
        {
            ManualTimer? timer;
            lock (_gate)
            {
                timer = _timers
                    .Where(candidate => !candidate.IsDisposed
                                        && candidate.IsScheduled
                                        && candidate.DueAt <= target)
                    .OrderBy(candidate => candidate.DueAt)
                    .ThenBy(candidate => candidate.Sequence)
                    .FirstOrDefault();
                if (timer is null)
                {
                    MoveClockUnderLock(target);
                    return;
                }

                MoveClockUnderLock(timer.DueAt);
                timer.ScheduleNextUnderLock();
            }

            timer.Invoke();
            callbacks++;
            if (callbacks > 100_000)
            {
                throw new InvalidOperationException("Manual time advance exceeded the timer callback safety bound.");
            }
        }
    }

    private void MoveClockUnderLock(DateTimeOffset value)
    {
        var delta = value - _utcNow;
        _utcNow = value;
        _timestamp = checked(_timestamp + delta.Ticks);
    }

    private static void ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value < Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private static long _nextSequence;
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            Sequence = Interlocked.Increment(ref _nextSequence);
        }

        public long Sequence { get; }

        public DateTimeOffset DueAt { get; private set; }

        public bool IsScheduled { get; private set; }

        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ValidateTimeout(dueTime, nameof(dueTime));
            ValidateTimeout(period, nameof(period));
            lock (_owner._gate)
            {
                if (IsDisposed) return false;
                ChangeUnderLock(dueTime, period);
                return true;
            }
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                IsDisposed = true;
                IsScheduled = false;
                _owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void ChangeUnderLock(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            IsScheduled = dueTime != Timeout.InfiniteTimeSpan;
            DueAt = IsScheduled ? _owner._utcNow + dueTime : DateTimeOffset.MaxValue;
        }

        public void ScheduleNextUnderLock()
        {
            if (_period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero)
            {
                IsScheduled = false;
                return;
            }
            DueAt += _period;
        }

        public void Invoke() => _callback(_state);
    }
}
