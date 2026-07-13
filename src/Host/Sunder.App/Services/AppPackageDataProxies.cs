using Sunder.Runtime.Client;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class AppRuntimePackageStorageContext(
    string packageId,
    RuntimePackageDataClient client,
    IPackageRoleLocalWorkspace roleLocalWorkspace) : IPackageStorageContext
{
    public IPackageFileStore Files { get; } = new AppRuntimePackageFileStore(packageId, client);

    public IPackageKeyValueStore State { get; } = new AppRuntimePackageStateStore(packageId, client);

    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = roleLocalWorkspace;
}

internal sealed class AppRuntimePackageStateStore(
    string packageId,
    RuntimePackageDataClient client) : IPackageKeyValueStore
{
    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        => (await client.GetStateAsync(packageId, key, cancellationToken).ConfigureAwait(false))?.Value;

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        => client.SetStateAsync(packageId, key, value, cancellationToken);

    public async Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
        => (await client.GetStateAsync(packageId, key, cancellationToken).ConfigureAwait(false))?.Found == true;

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        => client.DeleteStateAsync(packageId, key, cancellationToken);

    public Task<IReadOnlyList<string>> ListKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
        => client.ListStateKeysAsync(packageId, prefix, cancellationToken);
}

internal sealed class AppRuntimePackageSettings(
    string packageId,
    RuntimePackageDataClient client) : IPackageSettings
{
    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        => (await client.GetSettingAsync(packageId, key, cancellationToken).ConfigureAwait(false))?.EffectiveValue;

    public async Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
        => (await client.GetSettingAsync(packageId, key, cancellationToken).ConfigureAwait(false))?.StoredValue;

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        => client.SetSettingAsync(packageId, key, value, cancellationToken);

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        => client.DeleteSettingAsync(packageId, key, cancellationToken);
}

internal sealed class AppRuntimePackageSecrets(
    string packageId,
    RuntimePackageDataClient client) : IPackageSecrets
{
    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        => (await client.GetSecretAsync(packageId, key, cancellationToken).ConfigureAwait(false))?.Value;

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        => client.SetSecretAsync(packageId, key, value, cancellationToken);

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        => client.DeleteSecretAsync(packageId, key, cancellationToken);
}

internal sealed class AppRuntimePackageFileStore(
    string packageId,
    RuntimePackageDataClient client) : IPackageFileStore
{
    public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
        => client.ReadFileAsync(packageId, relativePath, cancellationToken);

    public Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
        => client.WriteFileAsync(packageId, relativePath, contents, cancellationToken);

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        => client.DeleteFileAsync(packageId, relativePath, cancellationToken);
}

internal sealed class AppPackageRoleLocalWorkspace(string packageId) : IPackageRoleLocalWorkspace
{
    public string WorkspaceRootPath { get; } = CreateWorkspaceRoot(packageId);

    public string GetLocalPath(string relativePath)
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

        var path = Path.GetFullPath(Path.Combine([WorkspaceRootPath, .. segments]));
        var rootPrefix = WorkspaceRootPath.EndsWith(Path.DirectorySeparatorChar)
            ? WorkspaceRootPath
            : WorkspaceRootPath + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Package workspace paths must remain inside the workspace.", nameof(relativePath));
        }

        return path;
    }

    private static string CreateWorkspaceRoot(string packageId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safePackageId = new string(packageId.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        var root = AppLocalState.GetPath("package-workspaces", safePackageId);
        Directory.CreateDirectory(root);
        return Path.GetFullPath(root);
    }
}

internal sealed class AppUnavailablePackageRoleLocalWorkspace : IPackageRoleLocalWorkspace
{
    internal static AppUnavailablePackageRoleLocalWorkspace Instance { get; } = new();

    public string WorkspaceRootPath => throw Unavailable();

    public string GetLocalPath(string relativePath) => throw Unavailable();

    private static InvalidOperationException Unavailable()
        => new("The package local workspace is unavailable because the local Runtime is not connected.");
}

internal sealed class AppPreflightPackageStorageContext : IPackageStorageContext
{
    internal static AppPreflightPackageStorageContext Instance { get; } = new();

    public IPackageFileStore Files { get; } = new AppPreflightPackageFileStore();
    public IPackageKeyValueStore State { get; } = new AppPreflightPackageStateStore();
    public IPackageRoleLocalWorkspace RoleLocalWorkspace => throw PersistenceUnavailable();

    internal static InvalidOperationException PersistenceUnavailable()
        => new("Package persistence and local workspaces are unavailable during App package preflight.");
}

internal sealed class AppPreflightPackageStateStore : IPackageKeyValueStore
{
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) => throw Reject();
    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) => throw Reject();
    public Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default) => throw Reject();
    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default) => throw Reject();
    public Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default)
        => throw Reject();

    private static InvalidOperationException Reject() => AppPreflightPackageStorageContext.PersistenceUnavailable();
}

internal sealed class AppPreflightPackageSettings : IPackageSettings
{
    internal static AppPreflightPackageSettings Instance { get; } = new();
    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        => throw AppPreflightPackageStorageContext.PersistenceUnavailable();
    public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
        => throw AppPreflightPackageStorageContext.PersistenceUnavailable();
    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        => throw AppPreflightPackageStorageContext.PersistenceUnavailable();
    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        => throw AppPreflightPackageStorageContext.PersistenceUnavailable();
}

internal sealed class AppPreflightPackageSecrets : IPackageSecrets
{
    internal static AppPreflightPackageSecrets Instance { get; } = new();
    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
        => throw AppPreflightPackageStorageContext.PersistenceUnavailable();
    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
        => throw AppPreflightPackageStorageContext.PersistenceUnavailable();
    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
        => throw AppPreflightPackageStorageContext.PersistenceUnavailable();
}

internal sealed class AppPreflightPackageFileStore : IPackageFileStore
{
    public Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
        => throw AppPreflightPackageStorageContext.PersistenceUnavailable();
    public Task WriteAsync(string relativePath, ReadOnlyMemory<byte> contents, CancellationToken cancellationToken = default)
        => throw AppPreflightPackageStorageContext.PersistenceUnavailable();
    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        => throw AppPreflightPackageStorageContext.PersistenceUnavailable();
}
