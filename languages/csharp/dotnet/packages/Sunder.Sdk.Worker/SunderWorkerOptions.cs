using System.Collections.ObjectModel;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Worker;

/// <summary>Configures one native <c>sunder.worker.v1</c> activation.</summary>
[SunderSdkCapability(SunderWorkerCapabilities.ProtocolV1)]
public sealed class SunderWorkerOptions
{
    private readonly IReadOnlyList<SunderWorkerProviderRegistration> _providers;
    private IReadOnlyList<IPackageCallbackHandler> _callbackHandlers = [];
    private IReadOnlyList<PackageStorageKeyMigration> _stateMigrations = [];
    private IReadOnlyList<PackageStorageKeyMigration> _secretMigrations = [];

    /// <summary>Creates V1 worker options for the RPC providers declared by the package manifest.</summary>
    public SunderWorkerOptions(IEnumerable<SunderWorkerProviderRegistration> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var snapshot = providers.ToArray();
        if (snapshot.Any(static provider => provider is null))
        {
            throw new ArgumentException("Worker provider registrations cannot contain null.", nameof(providers));
        }

        var duplicate = snapshot
            .GroupBy(static provider => provider.ProviderId, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Worker provider '{duplicate.Key}' is registered more than once.",
                nameof(providers));
        }

        _providers = new ReadOnlyCollection<SunderWorkerProviderRegistration>(snapshot);
    }

    /// <summary>Gets the immutable RPC provider registrations advertised during composition.</summary>
    public IReadOnlyList<SunderWorkerProviderRegistration> Providers => _providers;

    /// <summary>Gets or initializes the canonical settings schema advertised during composition.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.SettingsSchemaV1)]
    public PackageSettingsSchema? SettingsSchema { get; init; }

    /// <summary>Gets or initializes package-owned DI and staged Worker lifecycle composition for this activation.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.BackgroundServicesV1)]
    [SunderSdkCapability(SunderSdkCapabilities.RuntimeGenerationsV1)]
    public SunderWorkerComposition? Composition { get; init; }

    /// <summary>Gets or initializes immutable state-key migrations completed before activation.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
    [SunderSdkCapability(SunderSdkCapabilities.StorageKeyMigrationV1)]
    public IReadOnlyList<PackageStorageKeyMigration> StateMigrations
    {
        get => _stateMigrations;
        init => _stateMigrations = SnapshotMigrations(value, nameof(StateMigrations));
    }

    /// <summary>Gets or initializes immutable secret-key migrations completed before activation.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
    [SunderSdkCapability(SunderSdkCapabilities.StorageKeyMigrationV1)]
    public IReadOnlyList<PackageStorageKeyMigration> SecretMigrations
    {
        get => _secretMigrations;
        init => _secretMigrations = SnapshotMigrations(value, nameof(SecretMigrations));
    }

    /// <summary>Gets or initializes the immutable package-local callback handler catalog.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
    public IReadOnlyList<IPackageCallbackHandler> CallbackHandlers
    {
        get => _callbackHandlers;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            var snapshot = value.ToArray();
            if (snapshot.Any(static handler => handler is null))
            {
                throw new ArgumentException("Worker callback handlers cannot contain null.", nameof(value));
            }
            foreach (var handler in snapshot)
            {
                ValidateCallbackHandlerId(handler.CallbackHandlerId);
                if (string.Equals(
                        handler.CallbackHandlerId,
                        PackageCallbackHandlerIds.Authentication,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "The reserved authentication callback id must be registered through AuthHandler.",
                        nameof(value));
                }
            }
            var duplicate = snapshot
                .GroupBy(static handler => handler.CallbackHandlerId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(static group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new ArgumentException(
                    $"Worker callback handler '{duplicate.Key}' is registered more than once.",
                    nameof(value));
            }
            _callbackHandlers = new ReadOnlyCollection<IPackageCallbackHandler>(snapshot);
        }
    }

    /// <summary>Gets or initializes the optional activation-scoped authorization handler.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.AuthV1)]
    [SunderSdkCapability(SunderSdkCapabilities.CallbacksV1)]
    public IPackageAuthHandler? AuthHandler { get; init; }

    /// <summary>Gets or initializes additional non-destructive work awaited after composition candidate preparation.</summary>
    public Func<CancellationToken, ValueTask>? OnCandidateStarted { get; init; }

    /// <summary>Gets or initializes additional idempotent work awaited after composition commits the exact Runtime generation.</summary>
    public Func<SunderWorkerGenerationContext, CancellationToken, ValueTask>? OnGenerationCommitted { get; init; }

    /// <summary>Gets or initializes read-only state preparation awaited before declared storage migrations run.</summary>
    [SunderSdkCapability(SunderSdkCapabilities.StorageV1)]
    [SunderSdkCapability(SunderSdkCapabilities.StorageKeyMigrationV1)]
    public Func<SunderWorkerMigrationPreparationContext, CancellationToken, ValueTask>?
        OnBeforeStorageMigrations { get; init; }

    /// <summary>Gets or initializes additional work awaited after composition activation and before publication readiness is acknowledged.</summary>
    public Func<SunderWorkerContext, CancellationToken, ValueTask>? OnActivated { get; init; }

    /// <summary>Gets or initializes work awaited before reverse-order composition shutdown and shutdown acknowledgement.</summary>
    public Func<SunderWorkerShutdownContext, CancellationToken, ValueTask>? OnShutdown { get; init; }

    private static void ValidateCallbackHandlerId(string handlerId)
    {
        if (string.IsNullOrWhiteSpace(handlerId)
            || handlerId.Length > 128
            || handlerId.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new ArgumentException(
                "Worker callback handler ids must be ASCII tokens of at most 128 characters.",
                nameof(handlerId));
        }
    }

    private static IReadOnlyList<PackageStorageKeyMigration> SnapshotMigrations(
        IReadOnlyList<PackageStorageKeyMigration> value,
        string propertyName)
    {
        ArgumentNullException.ThrowIfNull(value);
        var snapshot = value.ToArray();
        if (snapshot.Length > 32)
        {
            throw new ArgumentException(
                "A worker storage migration plan cannot contain more than 32 rules.",
                propertyName);
        }
        if (snapshot.Any(static migration => migration is null))
        {
            throw new ArgumentException(
                "Worker storage migration plans cannot contain null.",
                propertyName);
        }
        return new ReadOnlyCollection<PackageStorageKeyMigration>(snapshot);
    }
}
