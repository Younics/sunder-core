namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeFileLock : IDisposable
{
    private const int RetryDelayMilliseconds = 25;
    private FileStream? _stream;

    private RuntimeFileLock(FileStream stream)
    {
        _stream = stream;
    }

    internal FileStream Stream => _stream ?? throw new ObjectDisposedException(nameof(RuntimeFileLock));

    public static RuntimeFileLock? TryAcquireExclusive(string path)
        => TryAcquire(path, FileShare.None);

    public static async ValueTask<RuntimeFileLock> AcquireExclusiveAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = TryAcquireExclusive(path);
            if (acquired is not null)
            {
                return acquired;
            }
            await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    public static RuntimeFileLock AcquireExclusive(string path)
    {
        while (true)
        {
            var acquired = TryAcquireExclusive(path);
            if (acquired is not null)
            {
                return acquired;
            }
            Thread.Sleep(RetryDelayMilliseconds);
        }
    }

    public static async ValueTask<RuntimeFileLock> AcquireSharedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = TryAcquire(path, FileShare.ReadWrite);
            if (acquired is not null)
            {
                return acquired;
            }
            await Task.Delay(RetryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
        => Interlocked.Exchange(ref _stream, null)?.Dispose();

    private static RuntimeFileLock? TryAcquire(string path, FileShare share)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        PackageStorePathGuard.EnsureNotReparsePoint(fullPath);
        try
        {
            var stream = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                share,
                bufferSize: 1,
                FileOptions.None);
            try
            {
                PackageStorePathGuard.EnsureNotReparsePoint(fullPath);
                VerifyLockEnforced(fullPath);
                return new RuntimeFileLock(stream);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (IOException exception) when (IsContention(exception))
        {
            return null;
        }
    }

    private static bool IsContention(IOException exception)
        => (exception.HResult & 0xffff) is 11 or 13 or 32 or 33 or 35;

    private static void VerifyLockEnforced(string path)
    {
        try
        {
            using var unexpected = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception) when (IsContention(exception))
        {
            return;
        }
        throw new PlatformNotSupportedException(
            $"The filesystem containing Runtime lock '{path}' does not enforce cross-process file sharing locks.");
    }
}
