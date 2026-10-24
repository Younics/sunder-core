using System.ComponentModel;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Services;

/// <summary>Discovers authorized implementations of a generated service interface.</summary>
/// <remarks>
/// Sunder supplies this catalog through package composition. Each entry exposes an exact-bound
/// service; selecting an entry is application policy. Resolving the catalog or accessing a service
/// does not dispatch a call. A retired entry never silently changes to another activation.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public interface IServiceProviders<TService> where TService : class
{
    /// <summary>Gets an immutable view of the providers visible to this package role.</summary>
    ValueTask<ServiceProviderCatalog<TService>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Watches changes after the revision and sequence returned by a catalog snapshot.</summary>
    /// <remarks>A reset event requires a fresh snapshot. Both cursor values belong to the same observation.</remarks>
    IAsyncEnumerable<ServiceProviderCatalogEvent<TService>> WatchAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken cancellationToken = default);
}

/// <summary>The availability of one exact service implementation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum ServiceProviderState
{
    /// <summary>The implementation is callable.</summary>
    Active,
    /// <summary>The implementation's activation has retired.</summary>
    Inactive,
    /// <summary>The implementation's activation has faulted.</summary>
    Faulted,
}

/// <summary>An ordered change to a service provider catalog.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum ServiceProviderChangeKind
{
    /// <summary>An implementation entered the catalog.</summary>
    Added,
    /// <summary>An implementation left the catalog.</summary>
    Removed,
    /// <summary>An implementation became callable.</summary>
    Activated,
    /// <summary>An implementation's activation retired.</summary>
    Deactivated,
    /// <summary>An implementation's activation faulted.</summary>
    Faulted,
    /// <summary>Replay history was lost; obtain a fresh snapshot.</summary>
    ResetRequired,
}

/// <summary>A discovered implementation and its automatically managed, exact-bound service.</summary>
/// <remarks>The catalog owner manages transport lifetime; consumers do not dispose individual entries.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class ServiceProvider<TService> where TService : class
{
    private readonly SunderRpcProviderSnapshot _snapshot;
    private readonly SunderRpcServiceBinding? _binding;
    private readonly TService _service;

    /// <summary>Creates an entry for generated bindings from validated provider metadata.</summary>
    /// <remarks>Metadata-only entries have no observing lifetime. Generated ready entries use the lifetime overload.</remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ServiceProvider(SunderRpcProviderSnapshot snapshot, TService service)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(service);
        if (!Enum.IsDefined(snapshot.State))
            throw new ArgumentOutOfRangeException(nameof(snapshot), "The provider state is invalid.");
        _snapshot = snapshot;
        _service = service;
    }

    /// <summary>Creates an exact entry attached to the receiver's shared generated binding lifetime.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ServiceProvider(SunderRpcProviderSnapshot snapshot, TService service, SunderRpcServiceBindingLifetime lifetime)
        : this(snapshot, service, (lifetime ?? throw new ArgumentNullException(nameof(lifetime))).Track(snapshot)) { }

    internal ServiceProvider(SunderRpcProviderSnapshot snapshot, TService service, SunderRpcServiceBinding binding)
        : this(snapshot, service) => _binding = binding;

    /// <summary>Gets the ready-to-call generated service for this exact implementation.</summary>
    public TService Service { get { _binding?.ThrowIfRetired(); return _service; } }
    /// <summary>Gets a serializable typed locator for this already bound, exact implementation.</summary>
    public ServiceReference<TService> Reference
    {
        get { _binding?.Owner.Start(throwIfStopped: false); return new(_snapshot, _service, _binding); }
    }
    /// <summary>Gets cancellation for permanent retirement of this exact entry, including catalog disconnect or DI disposal.</summary>
    /// <remarks>A metadata-only entry has an already-cancelled token. Link this token for a selected service's business lifetime.</remarks>
    public CancellationToken RetirementToken
    {
        get { _binding?.Owner.Start(throwIfStopped: false); return _binding?.Token ?? new CancellationToken(canceled: true); }
    }
    /// <summary>Validates this exact implementation against the receiver's current visible provider identity.</summary>
    public ValueTask EnsureCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _binding?.EnsureCurrentAsync(cancellationToken) ?? ValueTask.FromException(ServiceReference<TService>.Unbound());
    }
    /// <summary>Reports a semantic invariant violation only for this exact, current implementation.</summary>
    /// <remarks>Permission, transport and structured RPC failures propagate without conversion to an unavailable result.</remarks>
    public ValueTask<bool> TryReportInvariantViolationAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        cancellationToken.ThrowIfCancellationRequested();
        return _binding?.TryReportAsync(exception, cancellationToken) ?? ValueTask.FromException<bool>(ServiceReference<TService>.Unbound());
    }
    /// <summary>Gets the implementing package identity.</summary>
    public string PackageId => _snapshot.PackageId;
    /// <summary>Gets the implementing package version.</summary>
    public string PackageVersion => _snapshot.PackageVersion;
    /// <summary>Gets the implementation's stable provider identity.</summary>
    public string ProviderId => _snapshot.ProviderId;
    /// <summary>Gets the service contract identity.</summary>
    public string ContractId => _snapshot.ContractId;
    /// <summary>Gets the exact service contract version.</summary>
    public string ContractVersion => _snapshot.ContractVersion;
    /// <summary>Gets the canonical descriptor digest.</summary>
    public string DescriptorSha256 => _snapshot.DescriptorSha256;
    /// <summary>Gets the canonical ABI digest.</summary>
    public string AbiSha256 => _snapshot.AbiSha256;
    /// <summary>Gets the exact activation identity.</summary>
    public Guid ActivationId => _snapshot.ActivationId;
    /// <summary>Gets the exact activation epoch.</summary>
    public long ActivationEpoch => _snapshot.ActivationEpoch;
    /// <summary>Gets the generation that established this exact RPC activation; unrelated catalog publications do not change it.</summary>
    public long SessionGeneration => _snapshot.SessionGeneration;
    /// <summary>Gets the catalog revision that supplied this entry.</summary>
    public long CatalogRevision => _snapshot.CatalogRevision;
    /// <summary>Gets the observed implementation state.</summary>
    public ServiceProviderState State => (ServiceProviderState)_snapshot.State;
    /// <summary>Gets the safe fault code, when supplied for a faulted implementation.</summary>
    public string? FaultCode => _snapshot.FaultCode;
}

/// <summary>An immutable, cursor-bearing view of visible service implementations.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class ServiceProviderCatalog<TService> where TService : class
{
    /// <summary>Creates an immutable catalog view for generated bindings.</summary>
    public ServiceProviderCatalog(long revision, long sequence,
        IEnumerable<ServiceProvider<TService>> providers, bool resetRequired = false)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        var entries = providers.ToArray();
        if (entries.Any(entry => entry is null))
            throw new ArgumentException("A catalog cannot contain a null provider.", nameof(providers));
        Revision = revision;
        Sequence = sequence;
        Providers = Array.AsReadOnly(entries);
        ResetRequired = resetRequired;
    }

    /// <summary>Gets the atomic catalog revision.</summary>
    public long Revision { get; }
    /// <summary>Gets the latest replay sequence represented by the snapshot.</summary>
    public long Sequence { get; }
    /// <summary>Gets the immutable visible provider entries.</summary>
    public IReadOnlyList<ServiceProvider<TService>> Providers { get; }
    /// <summary>Gets whether a watcher must replace its view with a fresh snapshot.</summary>
    public bool ResetRequired { get; }
}

/// <summary>Describes an ordered provider change or a catalog reset.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed record ServiceProviderCatalogEvent<TService>(
    long Revision,
    long Sequence,
    ServiceProviderChangeKind Kind,
    ServiceProvider<TService>? Provider) where TService : class;
