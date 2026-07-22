using Sunder.Runtime.Client;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Storage;

namespace Sunder.App.Services;

internal sealed class AppRuntimePackageStorageContext(
    string packageId,
    RuntimePackageDataClient client,
    IPackageRoleLocalWorkspace roleLocalWorkspace,
    AppPackageGenerationPublication publication) : IPackageStorageContext
{
    public IPackageFileStore Files { get; } = new AppRuntimePackageFileStore(packageId, client, publication);

    public IPackageKeyValueStore State { get; } = new AppRuntimePackageStateStore(packageId, client, publication);

    public IPackageRoleLocalWorkspace RoleLocalWorkspace { get; } = roleLocalWorkspace;
}

internal sealed class AppRuntimePackageStateStore(
    string packageId,
    RuntimePackageDataClient client,
    AppPackageGenerationPublication publication) : IPackageKeyValueStore
{
    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.Key(key, nameof(key));
        var value = (await client.GetStateAsync(packageId, key, cancellationToken).ConfigureAwait(false))?.Value;
        AppPackageStorageGuards.ReturnedValue(value);
        return value;
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.Key(key, nameof(key));
        AppPackageStorageGuards.Value(value, nameof(value));
        publication.RequirePublished("package state mutation");
        return client.SetStateAsync(packageId, key, value, cancellationToken);
    }

    public async Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.Key(key, nameof(key));
        var result = await client.GetStateAsync(packageId, key, cancellationToken).ConfigureAwait(false);
        AppPackageStorageGuards.ReturnedValue(result?.Value);
        return result?.Found == true;
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.Key(key, nameof(key));
        publication.RequirePublished("package state mutation");
        return client.DeleteStateAsync(packageId, key, cancellationToken);
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
        => ListKeysCoreAsync(prefix, cancellationToken);

    private async Task<IReadOnlyList<string>> ListKeysCoreAsync(
        string? prefix,
        CancellationToken cancellationToken)
    {
        AppPackageStorageGuards.KeyPrefix(prefix, nameof(prefix));
        var keys = await client.ListStateKeysAsync(packageId, prefix, cancellationToken).ConfigureAwait(false);
        if (keys.Any(static key => !PackageStorageValidation.IsValidKey(key)))
        {
            throw new InvalidDataException("The Runtime returned an invalid package storage key.");
        }

        return keys;
    }
}

internal sealed class AppRuntimePackageSettings(
    string packageId,
    RuntimePackageDataClient client,
    AppPackageGenerationPublication publication) : IPackageSettings
{
    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await GetSettingAsync(key, cancellationToken).ConfigureAwait(false);
        return result?.EffectiveValue;
    }

    public async Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await GetSettingAsync(key, cancellationToken).ConfigureAwait(false);
        return result?.StoredValue;
    }

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.Key(key, nameof(key));
        AppPackageStorageGuards.Value(value, nameof(value));
        publication.RequirePublished("package settings mutation");
        return client.SetSettingAsync(packageId, key, value, cancellationToken);
    }

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.Key(key, nameof(key));
        publication.RequirePublished("package settings mutation");
        return client.DeleteSettingAsync(packageId, key, cancellationToken);
    }

    private async Task<Sunder.Runtime.Contracts.PackageSettingValueResponse?> GetSettingAsync(
        string key,
        CancellationToken cancellationToken)
    {
        AppPackageStorageGuards.Key(key, nameof(key));
        var result = await client.GetSettingAsync(packageId, key, cancellationToken).ConfigureAwait(false);
        AppPackageStorageGuards.ReturnedValue(result?.StoredValue);
        AppPackageStorageGuards.ReturnedValue(result?.EffectiveValue);
        return result;
    }
}

internal sealed class AppRuntimePackageSecrets(
    string packageId,
    RuntimePackageDataClient client,
    AppPackageGenerationPublication publication) : IPackageSecrets
{
    public async Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.Key(key, nameof(key));
        var value = (await client.GetSecretAsync(packageId, key, cancellationToken).ConfigureAwait(false))?.Value;
        AppPackageStorageGuards.ReturnedValue(value);
        return value;
    }

    public Task SetSecretAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.Key(key, nameof(key));
        AppPackageStorageGuards.Value(value, nameof(value));
        publication.RequirePublished("package secret mutation");
        return client.SetSecretAsync(packageId, key, value, cancellationToken);
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.Key(key, nameof(key));
        publication.RequirePublished("package secret mutation");
        return client.DeleteSecretAsync(packageId, key, cancellationToken);
    }
}

internal sealed class AppRuntimePackageFileStore(
    string packageId,
    RuntimePackageDataClient client,
    AppPackageGenerationPublication publication) : IPackageFileStore
{
    public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.RelativePath(relativePath, nameof(relativePath));
        var contents = await client.ReadFileAsync(packageId, relativePath, cancellationToken).ConfigureAwait(false);
        if (contents is not null && !PackageStorageValidation.IsValidFileLength(contents.LongLength))
        {
            throw new InvalidDataException(
                $"The Runtime returned a package file larger than {PackageStorageValidation.MaximumFileBytes} bytes.");
        }

        return contents;
    }

    public Task WriteAsync(
        string relativePath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.RelativePath(relativePath, nameof(relativePath));
        AppPackageStorageGuards.FileLength(contents.Length, nameof(contents));
        publication.RequirePublished("package file mutation");
        return client.WriteFileAsync(packageId, relativePath, contents, cancellationToken);
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        AppPackageStorageGuards.RelativePath(relativePath, nameof(relativePath));
        publication.RequirePublished("package file mutation");
        return client.DeleteFileAsync(packageId, relativePath, cancellationToken);
    }
}

internal static class AppPackageStorageGuards
{
    internal static void Key(string? key, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        if (!PackageStorageValidation.IsValidKey(key))
        {
            throw new ArgumentException(
                $"Package storage keys must be portable ASCII tokens of at most {PackageStorageValidation.MaximumKeyLength} characters.",
                parameterName);
        }
    }

    internal static void KeyPrefix(string? prefix, string parameterName)
    {
        if (prefix is not null && prefix.Length != 0 && !PackageStorageValidation.IsValidKey(prefix))
        {
            throw new ArgumentException("Package storage key prefixes must be empty or valid portable ASCII key prefixes.", parameterName);
        }
    }

    internal static void Value(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!PackageStorageValidation.IsValidValue(value))
        {
            throw new ArgumentException(
                $"Package storage values cannot exceed {PackageStorageValidation.MaximumValueUtf8Bytes} UTF-8 bytes.",
                parameterName);
        }
    }

    internal static void ReturnedValue(string? value)
    {
        if (value is not null && !PackageStorageValidation.IsValidValue(value))
        {
            throw new InvalidDataException("The Runtime returned a package storage value outside the SDK contract.");
        }
    }

    internal static void RelativePath(string? relativePath, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(relativePath, parameterName);
        if (!PackageStorageValidation.IsValidRelativePath(relativePath))
        {
            throw new ArgumentException(
                "Package file paths must be relative, portable, non-empty, free of traversal, and within the declared length limits.",
                parameterName);
        }
    }

    internal static void FileLength(long byteCount, string parameterName)
    {
        if (!PackageStorageValidation.IsValidFileLength(byteCount))
        {
            throw new ArgumentException(
                $"Package files cannot exceed {PackageStorageValidation.MaximumFileBytes} bytes.",
                parameterName);
        }
    }
}

internal sealed class AppPackageRoleLocalWorkspace(string packageId) : IPackageRoleLocalWorkspace
{
    public string WorkspaceRootPath { get; } = CreateWorkspaceRoot(packageId);

    public string GetLocalPath(string relativePath)
    {
        AppPackageStorageGuards.RelativePath(relativePath, nameof(relativePath));
        var segments = relativePath.Split('/');

        var path = Path.GetFullPath(Path.Combine([WorkspaceRootPath, .. segments]));
        var rootPrefix = WorkspaceRootPath.EndsWith(Path.DirectorySeparatorChar)
            ? WorkspaceRootPath
            : WorkspaceRootPath + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(rootPrefix, pathComparison))
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
