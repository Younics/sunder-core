namespace Sunder.Core.TestSupport;

public sealed class TemporaryDirectoryScope : IDisposable, IAsyncDisposable
{
    private readonly TemporaryDirectoryScopeOptions _options;
    private int _disposeStarted;

    private TemporaryDirectoryScope(string rootPath, TemporaryDirectoryScopeOptions options)
    {
        RootPath = Path.GetFullPath(rootPath);
        _options = options;
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public IReadOnlyList<string> CleanupDiagnostics => _cleanupDiagnostics;

    private readonly List<string> _cleanupDiagnostics = [];

    public static TemporaryDirectoryScope Create(
        string prefix,
        TemporaryDirectoryScopeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (prefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The temporary scope prefix must be a file-name-safe segment.", nameof(prefix));
        }

        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        return new TemporaryDirectoryScope(root, options ?? TemporaryDirectoryScopeOptions.Default);
    }

    public string GetPath(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var path = segments.Aggregate(RootPath, Path.Combine);
        var fullPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(fullPath, RootPath, comparison)
            && !fullPath.StartsWith(RootPath + Path.DirectorySeparatorChar, comparison))
        {
            throw new ArgumentException("The requested path escapes the temporary scope.", nameof(segments));
        }
        return fullPath;
    }

    public string CreateDirectory(params string[] segments)
    {
        var path = GetPath(segments);
        Directory.CreateDirectory(path);
        return path;
    }

    public string WriteAllText(string relativePath, string content)
    {
        var path = GetPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public async Task<string> WriteAllTextAsync(
        string relativePath,
        string content,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public void Dispose()
        => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;

        Exception? lastError = null;
        for (var attempt = 1; attempt <= _options.CleanupAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    await _options.DeleteDirectory(RootPath, CancellationToken.None).ConfigureAwait(false);
                }
                if (!Directory.Exists(RootPath)) return;
                lastError = new IOException("The delete operation returned while the temporary root still existed.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastError = exception;
            }

            var diagnostic = $"attempt={attempt}; error={lastError.GetType().Name}: {lastError.Message}; remaining={DescribeRemainingTree()}";
            _cleanupDiagnostics.Add(diagnostic);
            _options.ReportDiagnostic?.Invoke(diagnostic);
            await Task.Yield();
        }

        throw new TemporaryDirectoryCleanupException(
            RootPath,
            _cleanupDiagnostics.ToArray(),
            lastError ?? new IOException("Temporary directory cleanup did not remove the root."));
    }

    private string DescribeRemainingTree()
    {
        try
        {
            if (!Directory.Exists(RootPath)) return "<removed>";
            var entries = Directory.EnumerateFileSystemEntries(RootPath, "*", SearchOption.AllDirectories)
                .Take(20)
                .Select(path => Path.GetRelativePath(RootPath, path))
                .ToArray();
            return entries.Length == 0 ? "<empty-root-retained>" : string.Join(", ", entries);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"<enumeration-failed: {exception.GetType().Name}: {exception.Message}>";
        }
    }
}

public sealed record TemporaryDirectoryScopeOptions(
    int CleanupAttempts,
    Func<string, CancellationToken, ValueTask> DeleteDirectory,
    Action<string>? ReportDiagnostic = null)
{
    public static TemporaryDirectoryScopeOptions Default { get; } = new(
        CleanupAttempts: 3,
        DeleteDefault);

    private static ValueTask DeleteDefault(string path, CancellationToken _)
    {
        MakeTreeWritable(path);
        Directory.Delete(path, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static void MakeTreeWritable(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
            }
            else
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    continue;
                }
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
    }
}

public sealed class TemporaryDirectoryCleanupException(
    string rootPath,
    IReadOnlyList<string> diagnostics,
    Exception innerException)
    : IOException(
        $"Could not clean temporary directory '{rootPath}'.{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}",
        innerException)
{
    public string RootPath { get; } = rootPath;

    public IReadOnlyList<string> Diagnostics { get; } = diagnostics;
}
