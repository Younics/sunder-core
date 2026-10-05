using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeRootLease : IDisposable
{
    private readonly RuntimeFileLock _lock;
    private bool _disposed;

    private RuntimeRootLease(RuntimeFileLock fileLock)
    {
        _lock = fileLock;
    }

    public static RuntimeRootLease Acquire(RuntimePackagePaths paths)
    {
        RuntimeOwnedFileSystem.ValidateNoReparsePointAncestors(paths.RootPath);
        DurableJsonDocument.CreateDirectory(paths.RootPath);
        RuntimeOwnedFileSystem.ValidateMutableDirectory(paths.RootPath, paths.RootPath);
        RuntimeFileLock? fileLock = null;
        try
        {
            fileLock = RuntimeFileLock.TryAcquireExclusive(paths.LeaseFilePath)
                       ?? throw new IOException("The Runtime root lease is already held.");
            RuntimeOwnedFileSystem.ValidateMutableRegularFile(paths.RootPath, paths.LeaseFilePath);
            var stream = fileLock.Stream;
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write($"pid={Environment.ProcessId}{Environment.NewLine}started={DateTimeOffset.UtcNow:O}{Environment.NewLine}");
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return new RuntimeRootLease(fileLock);
        }
        catch (Exception ex)
        {
            fileLock?.Dispose();
            if (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Another Sunder Runtime instance owns package root '{paths.RootPath}'. Stop that Runtime or select a different profile/package root.",
                    ex);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock.Dispose();
    }
}
