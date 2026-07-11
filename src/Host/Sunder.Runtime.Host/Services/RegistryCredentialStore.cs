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

internal sealed class RegistryCredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly JsonPackageSecretsStore _secrets;

    public RegistryCredentialStore(RuntimePackagePaths paths)
    {
        Directory.CreateDirectory(paths.RegistryCredentialRootPath);
        _secrets = new JsonPackageSecretsStore(paths.RegistryCredentialFilePath);
    }

    public async Task<RegistryCredential?> GetAsync(Uri registryOrigin, CancellationToken cancellationToken = default)
    {
        var value = await _secrets.GetSecretAsync(RegistryOrigin.Key(registryOrigin), cancellationToken);
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

    public Task SetAsync(Uri registryOrigin, RegistryCredential credential, CancellationToken cancellationToken = default)
        => _secrets.SetSecretAsync(
            RegistryOrigin.Key(registryOrigin),
            JsonSerializer.Serialize(credential, JsonOptions),
            cancellationToken);

    public Task DeleteAsync(Uri registryOrigin, CancellationToken cancellationToken = default)
        => _secrets.DeleteSecretAsync(RegistryOrigin.Key(registryOrigin), cancellationToken);
}
