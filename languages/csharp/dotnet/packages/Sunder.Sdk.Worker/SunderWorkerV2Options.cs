using System.Collections.ObjectModel;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Settings;

namespace Sunder.Sdk.Worker;

/// <summary>Configures one native <c>sunder.worker.v2</c> activation.</summary>
[SunderSdkCapability(SunderWorkerCapabilities.ProtocolV2)]
public sealed class SunderWorkerV2Options
{
    private readonly IReadOnlyList<SunderWorkerProviderRegistration> _providers;

    /// <summary>Creates V2 worker options for the RPC providers declared by the package manifest.</summary>
    public SunderWorkerV2Options(IEnumerable<SunderWorkerProviderRegistration> providers)
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

    /// <summary>Gets or initializes non-destructive work awaited before candidate startup is acknowledged.</summary>
    public Func<CancellationToken, ValueTask>? OnCandidateStarted { get; init; }

    /// <summary>Gets or initializes idempotent work awaited when the Host commits the exact Runtime generation.</summary>
    public Func<SunderWorkerGenerationContext, CancellationToken, ValueTask>? OnGenerationCommitted { get; init; }

    /// <summary>Gets or initializes work awaited before the worker acknowledges activation.</summary>
    public Func<SunderWorkerContext, CancellationToken, ValueTask>? OnActivated { get; init; }

    /// <summary>Gets or initializes work awaited before the worker acknowledges shutdown.</summary>
    public Func<SunderWorkerShutdownContext, CancellationToken, ValueTask>? OnShutdown { get; init; }
}
