namespace Sunder.App.Services;

internal static class DevPackageWatchSupport
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(750);
    public static readonly TimeSpan StabilityProbeDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxStabilityWait = TimeSpan.FromSeconds(5);

    public static bool ShouldIgnorePath(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName)
               || string.Equals(fileName, ".DS_Store", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".swp", StringComparison.OrdinalIgnoreCase)
               || fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLoadableDevPackageFolder(string folder, bool requireLibraryFolder = false)
        => Directory.Exists(folder)
           && File.Exists(Path.Combine(folder, "sunder-package.json"))
           && (!requireLibraryFolder || Directory.Exists(Path.Combine(folder, "lib")));

    public static async Task<bool> WaitForStableFoldersAsync(
        IReadOnlyList<string> folders,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken,
        bool requireLibraryFolder = false,
        Action? onLoadabilityRetry = null)
    {
        var deadline = DateTimeOffset.UtcNow + MaxStabilityWait;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!folders.All(folder => IsLoadableDevPackageFolder(folder, requireLibraryFolder)))
            {
                await delayAsync(StabilityProbeDelay, cancellationToken).ConfigureAwait(false);
                onLoadabilityRetry?.Invoke();
                continue;
            }

            if (!TrySnapshotFiles(folders, out var before))
            {
                await delayAsync(StabilityProbeDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await delayAsync(StabilityProbeDelay, cancellationToken).ConfigureAwait(false);
            if (TrySnapshotFiles(folders, out var after) && AreSnapshotsEqual(before, after))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySnapshotFiles(
        IReadOnlyList<string> folders,
        out Dictionary<string, FileSnapshot> snapshot)
    {
        snapshot = new Dictionary<string, FileSnapshot>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var folder in folders)
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (ShouldIgnorePath(file))
                    {
                        continue;
                    }

                    var info = new FileInfo(file);
                    snapshot[file] = new FileSnapshot(info.Length, info.LastWriteTimeUtc);
                }
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool AreSnapshotsEqual(
        IReadOnlyDictionary<string, FileSnapshot> left,
        IReadOnlyDictionary<string, FileSnapshot> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var entry in left)
        {
            if (!right.TryGetValue(entry.Key, out var rightSnapshot) || !entry.Value.Equals(rightSnapshot))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct FileSnapshot(long Length, DateTime LastWriteTimeUtc);
}
