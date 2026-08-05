using System.Collections.ObjectModel;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Worker;

/// <summary>Configures one standalone Sunder worker activation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderWorkerOptions
{
    private readonly IReadOnlyList<SunderWorkerProviderRegistration> _providers;

    /// <summary>Creates worker options for the providers declared by the package manifest.</summary>
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

    /// <summary>Gets the immutable provider registrations advertised during the worker handshake.</summary>
    public IReadOnlyList<SunderWorkerProviderRegistration> Providers => _providers;

    /// <summary>Gets or initializes the callback run after the worker acknowledges activation.</summary>
    public Func<ISunderRpcClient, CancellationToken, ValueTask>? OnActivated { get; init; }

    /// <summary>Gets or initializes the callback awaited before the worker acknowledges shutdown.</summary>
    public Func<SunderWorkerShutdownContext, CancellationToken, ValueTask>? OnShutdown { get; init; }
}
