using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Infrastructure.Storage;

internal sealed class LocalPackageStorageContext : IPackageStorageContext
{
    internal LocalPackageStorageContext(string packageId, string packageDataRootPath)
    {
        var packageRoot = Path.Combine(
            Path.GetFullPath(packageDataRootPath),
            SanitizePathSegment(packageId)
        );

        DataRootPath = Path.Combine(packageRoot, "data");
        LogsRootPath = Path.Combine(packageRoot, "logs");
        var workspaceRootPath = Path.Combine(packageRoot, "workspace");

        Directory.CreateDirectory(DataRootPath);
        Directory.CreateDirectory(LogsRootPath);
        Directory.CreateDirectory(workspaceRootPath);

        var filesRootPath = Path.Combine(packageRoot, "files");
        Directory.CreateDirectory(filesRootPath);

        Files = new LocalPackageFileStore(filesRootPath);
        State = new JsonPackageKeyValueStore(Path.Combine(DataRootPath, "state.json"));
        LocalWorkspace = new LocalPackageWorkspaceLease(workspaceRootPath);
    }

    internal string DataRootPath { get; }

    internal string LogsRootPath { get; }

    public IPackageFileStore Files { get; }

    public IPackageKeyValueStore State { get; }

    public IPackageLocalWorkspaceLease LocalWorkspace { get; }

    private static string SanitizePathSegment(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return new string(value
            .Select(ch => invalidCharacters.Contains(ch) || ch is ':' or '/' or '\\' ? '_' : ch)
            .ToArray());
    }
}

internal sealed class PackageStateConfiguration(IPackageKeyValueStore stateStore) : IPackageConfiguration
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        => stateStore.GetValueAsync(key, cancellationToken);
}

internal sealed class LocalPackageFileStore(string rootPath) : IPackageFileStore
{
    private readonly string _rootPath = Path.GetFullPath(rootPath);

    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var path = PackageWorkspacePath.Resolve(_rootPath, relativePath);
        if (!File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        var path = PackageWorkspacePath.Resolve(_rootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, contents.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(PackageWorkspacePath.Resolve(_rootPath, relativePath));
        return Task.CompletedTask;
    }

    internal string ResolvePath(string relativePath) => PackageWorkspacePath.Resolve(_rootPath, relativePath);
}

internal sealed class LocalPackageWorkspaceLease(string rootPath) : IPackageLocalWorkspaceLease
{
    public string WorkspaceRootPath { get; } = Path.GetFullPath(rootPath);

    public string GetLocalPath(string relativePath)
        => PackageWorkspacePath.Resolve(WorkspaceRootPath, relativePath);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal static class PackageWorkspacePath
{
    internal static string Resolve(string rootPath, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Package workspace paths must be relative.", nameof(relativePath));
        }

        var segments = relativePath.Split(
            ['/', '\\'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Package workspace paths must not be empty or contain traversal segments.", nameof(relativePath));
        }

        var canonicalRoot = Path.GetFullPath(rootPath);
        var fullPath = Path.GetFullPath(Path.Combine([canonicalRoot, .. segments]));
        var rootPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Package workspace paths must remain inside the workspace.", nameof(relativePath));
        }

        return fullPath;
    }
}
