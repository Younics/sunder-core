using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Host.Infrastructure.Storage;

namespace Sunder.Runtime.Host.Services;

internal sealed record RegistryCredential(
    string AccessToken,
    string? UserId,
    DateTimeOffset? ExpiresAtUtc,
    string? Username,
    string? DisplayName,
    string? Email,
    string? AvatarUrl,
    bool RequiresUsername);

internal sealed record RegistryCredentialSnapshot(
    RegistryCredential Credential,
    long Revision,
    string TokenId);

internal sealed class RegistryCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _originStatesLock = new();
    private readonly Dictionary<string, OriginState> _originStates = new(StringComparer.Ordinal);
    private readonly JsonPackageSecretsStore _secrets;

    public RegistryCredentialStore(RuntimePackagePaths paths)
    {
        Directory.CreateDirectory(paths.RegistryCredentialRootPath);
        _secrets = new JsonPackageSecretsStore(
            paths.RegistryCredentialFilePath,
            enforcePackageKeyValidation: false);
    }

    public async Task<RegistryCredential?> GetAsync(Uri registryOrigin, CancellationToken cancellationToken = default)
        => (await GetSnapshotAsync(registryOrigin, cancellationToken))?.Credential;

    public async Task<RegistryCredentialSnapshot?> GetSnapshotAsync(
        Uri registryOrigin,
        CancellationToken cancellationToken = default)
    {
        var key = RegistryOrigin.Key(registryOrigin);
        var state = GetOriginState(key);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var credential = await LoadAsync(key, cancellationToken);
            return credential is null
                ? null
                : new RegistryCredentialSnapshot(credential, state.Revision, TokenId(credential));
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task SetAsync(
        Uri registryOrigin,
        RegistryCredential credential,
        CancellationToken cancellationToken = default)
    {
        var key = RegistryOrigin.Key(registryOrigin);
        var state = GetOriginState(key);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            await SetAsync(key, credential, cancellationToken);
            state.Revision = checked(state.Revision + 1);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<bool> TryUpdateAsync(
        Uri registryOrigin,
        RegistryCredentialSnapshot expected,
        RegistryCredential updated,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(TokenId(updated), expected.TokenId, StringComparison.Ordinal))
        {
            throw new ArgumentException("A conditional Registry credential update cannot replace the access token.", nameof(updated));
        }

        var key = RegistryOrigin.Key(registryOrigin);
        var state = GetOriginState(key);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Revision != expected.Revision
                || !await HasExpectedTokenAsync(key, expected.TokenId, cancellationToken))
            {
                return false;
            }

            await SetAsync(key, updated, cancellationToken);
            state.Revision = checked(state.Revision + 1);
            return true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<bool> TryDeleteAsync(
        Uri registryOrigin,
        RegistryCredentialSnapshot expected,
        CancellationToken cancellationToken = default)
    {
        var key = RegistryOrigin.Key(registryOrigin);
        var state = GetOriginState(key);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (state.Revision != expected.Revision
                || !await HasExpectedTokenAsync(key, expected.TokenId, cancellationToken))
            {
                return false;
            }

            await _secrets.DeleteSecretAsync(key, cancellationToken);
            state.Revision = checked(state.Revision + 1);
            return true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task DeleteAsync(Uri registryOrigin, CancellationToken cancellationToken = default)
    {
        var key = RegistryOrigin.Key(registryOrigin);
        var state = GetOriginState(key);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            await _secrets.DeleteSecretAsync(key, cancellationToken);
            state.Revision = checked(state.Revision + 1);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private OriginState GetOriginState(string key)
    {
        lock (_originStatesLock)
        {
            if (!_originStates.TryGetValue(key, out var state))
            {
                state = new OriginState();
                _originStates.Add(key, state);
            }

            return state;
        }
    }

    private async Task<bool> HasExpectedTokenAsync(
        string key,
        string expectedTokenId,
        CancellationToken cancellationToken)
    {
        var current = await LoadAsync(key, cancellationToken);
        return current is not null
               && string.Equals(TokenId(current), expectedTokenId, StringComparison.Ordinal);
    }

    private async Task<RegistryCredential?> LoadAsync(string key, CancellationToken cancellationToken)
    {
        var value = await _secrets.GetSecretAsync(key, cancellationToken);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var credential = JsonSerializer.Deserialize<RegistryCredential>(value, JsonOptions);
        if (credential is null || string.IsNullOrWhiteSpace(credential.AccessToken))
        {
            throw new InvalidDataException("The encrypted Registry credential is invalid.");
        }

        return credential;
    }

    private Task SetAsync(string key, RegistryCredential credential, CancellationToken cancellationToken)
        => _secrets.SetSecretAsync(key, JsonSerializer.Serialize(credential, JsonOptions), cancellationToken);

    private static string TokenId(RegistryCredential credential)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential.AccessToken)));

    private sealed class OriginState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long Revision { get; set; }
    }
}
