using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Tests;

internal sealed class EmptyTestPackageSettings : IPackageSettings
{
    internal static EmptyTestPackageSettings Instance { get; } = new();

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task<string?> GetStoredValueAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task DeleteValueAsync(string key, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
