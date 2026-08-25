namespace Sunder.Core.TestSupport;

public static class FileSignal
{
    public static async Task WaitForAsync(
        string path,
        TimeSpan timeout,
        Func<string>? describe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath)) return;
        var parent = Path.GetDirectoryName(fullPath)
                     ?? throw new ArgumentException("The file signal path has no parent directory.", nameof(path));
        Directory.CreateDirectory(parent);

        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(parent, Path.GetFileName(fullPath))
        {
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
        };
        FileSystemEventHandler signal = (_, _) => observed.TrySetResult();
        RenamedEventHandler rename = (_, _) => observed.TrySetResult();
        watcher.Created += signal;
        watcher.Changed += signal;
        watcher.Renamed += rename;
        if (File.Exists(fullPath)) return;

        try
        {
            await observed.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(fullPath))
            {
                await Eventually.ObserveAsync(
                    _ => ValueTask.FromResult(File.Exists(fullPath)),
                    static exists => exists,
                    timeout,
                    describe: (_, error) => $"path={fullPath}; {describe?.Invoke()}; error={error?.Message}",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"Timed out waiting for file signal '{fullPath}'. {describe?.Invoke()}",
                exception);
        }
    }
}
