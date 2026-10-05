using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Worker;

/// <summary>Provides generation-fenced read-only state access before Worker V1 storage migrations run.</summary>
[SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
[SunderSdkCapability(SunderWorkerCapabilities.ProtocolV1)]
public sealed class SunderWorkerMigrationPreparationContext
{
    private readonly Internal.WorkerRpcClient _client;

    internal SunderWorkerMigrationPreparationContext(
        Internal.WorkerRpcClient client,
        SunderWorkerGenerationContext generation)
    {
        _client = client;
        Generation = generation;
    }

    /// <summary>Gets the exact Host-committed generation authorized for this preparation phase.</summary>
    public SunderWorkerGenerationContext Generation { get; }

    /// <summary>Reads one package-state value without granting mutation or provider access.</summary>
    public Task<string?> GetStateValueAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        Internal.WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        return _client.GetStateForMigrationPreparationAsync(key, cancellationToken);
    }

    /// <summary>Checks whether one package-state key exists.</summary>
    public Task<bool> ContainsStateKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        Internal.WorkerPackageDataValidation.ValidateKey(key, nameof(key));
        return _client.ContainsStateKeyForMigrationPreparationAsync(key, cancellationToken);
    }

    /// <summary>Lists package-state keys matching an optional portable prefix.</summary>
    public Task<IReadOnlyList<string>> ListStateKeysAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        Internal.WorkerPackageDataValidation.ValidatePrefix(prefix, nameof(prefix));
        return _client.ListStateKeysForMigrationPreparationAsync(prefix, cancellationToken);
    }
}
