namespace Sunder.Core.TestSupport;

public sealed class OutputLockContenderBarrier<T> : IDisposable
{
    private readonly TaskCompletionSource _attempting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string> _describe;
    private readonly FileSystemWatcher _watcher;
    private int _watcherStopped;

    private OutputLockContenderBarrier(string outputPath, Func<T> acquire, Func<string> describe)
    {
        _describe = describe;
        var fullOutputPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullOutputPath)
                     ?? throw new ArgumentException("The output path must have a parent directory.", nameof(outputPath));
        Directory.CreateDirectory(parent);
        var transitionPrefix = Path.GetFileName(fullOutputPath) + ".sunder-output.lock.transition";
        _watcher = new FileSystemWatcher(parent)
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = false,
        };
        _watcher.Created += ObserveAttempt;
        _watcher.Changed += ObserveAttempt;
        _watcher.Renamed += ObserveAttempt;
        Completion = Task.Run(acquire);

        void ObserveAttempt(object? _, FileSystemEventArgs eventArgs)
        {
            if (eventArgs.Name?.StartsWith(transitionPrefix, PathComparison) != true) return;
            _attempting.TrySetResult();
            StopWatcher();
        }
    }

    public Task<T> Completion { get; }

    public static OutputLockContenderBarrier<T> Start(
        string outputPath,
        Func<T> acquire,
        Func<string> describe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(describe);
        return new OutputLockContenderBarrier<T>(outputPath, acquire, describe);
    }

    public async Task WaitForAttemptAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            await _attempting.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            StopWatcher();
            throw new TimeoutException($"Output-lock contender did not create an acquisition transition. {_describe()}", exception);
        }
    }

    public Task AssertContendedAsync()
        => Eventually.AssertPendingAsync(Completion, _describe);

    public void Dispose() => StopWatcher();

    private void StopWatcher()
    {
        if (Interlocked.Exchange(ref _watcherStopped, 1) != 0) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
