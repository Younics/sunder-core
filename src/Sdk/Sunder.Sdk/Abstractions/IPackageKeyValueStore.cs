namespace Sunder.Sdk.Abstractions;

public interface IPackageKeyValueStore
{
    string? GetValue(string key);

    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);

    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);

    Task<bool> ContainsKeyAsync(string key, CancellationToken cancellationToken = default);

    Task DeleteValueAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListKeysAsync(string? prefix = null, CancellationToken cancellationToken = default);
}
