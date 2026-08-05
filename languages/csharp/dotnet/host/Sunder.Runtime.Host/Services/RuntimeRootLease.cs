namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeRootLease : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    private RuntimeRootLease(FileStream stream)
    {
        _stream = stream;
    }

    public static RuntimeRootLease Acquire(RuntimePackagePaths paths)
    {
        Directory.CreateDirectory(paths.RootPath);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                paths.LeaseFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write($"pid={Environment.ProcessId}{Environment.NewLine}started={DateTimeOffset.UtcNow:O}{Environment.NewLine}");
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return new RuntimeRootLease(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            throw new InvalidOperationException(
                $"Another Sunder Runtime instance owns package root '{paths.RootPath}'. Stop that Runtime or select a different profile/package root.",
                ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
