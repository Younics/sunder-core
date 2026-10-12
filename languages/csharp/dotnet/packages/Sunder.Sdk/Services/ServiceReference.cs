using System.Text.Json.Serialization;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Rpc;

namespace Sunder.Sdk.Services;

/// <summary>A typed locator for an already registered, exact service-provider activation.</summary>
/// <remarks>
/// Obtain a reference from <see cref="ServiceProvider{TService}.Reference"/>. A received reference binds
/// to the receiver's generated client and dependency-injection lifetime. It does not carry permission,
/// export a local object, or follow replacement activations.
/// </remarks>
[JsonConverter(typeof(SunderRpcJsonConverterFactory))]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class ServiceReference<TService> where TService : class
{
    private SunderRpcServiceBinding? _binding;
    private readonly TService _service;

    internal ServiceReference(SunderRpcProviderSnapshot snapshot, TService service, SunderRpcServiceBinding? binding = null)
    {
        SunderRpcServiceLocator.Validate(snapshot);
        if (snapshot.State != SunderRpcProviderState.Active)
            throw SunderRpcServiceLocator.Stale();
        Snapshot = snapshot;
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _binding = binding;
    }

    internal SunderRpcProviderSnapshot Snapshot { get; }
    internal SunderRpcServiceBinding? Binding => Volatile.Read(ref _binding);
    internal SunderRpcServiceBinding Attach(SunderRpcServiceBinding binding)
        => Interlocked.CompareExchange(ref _binding, binding, null) ?? binding;

    internal static SunderRpcException Unbound() => new(new(SunderRpcErrorKind.Unavailable,
        "rpc.binding.lifetime-required", "The service reference requires its receiver's generated binding lifetime."));

    /// <summary>Gets the exact-bound service owned by this receiver's service lifetime.</summary>
    [JsonIgnore]
    public TService Service { get { Binding?.ThrowIfRetired(); return _service; } }

    /// <summary>Gets cancellation for permanent retirement of this exact reference.</summary>
    /// <remarks>The receiver's shared catalog observer supplies this signal; recovery never revives an issued reference.</remarks>
    [JsonIgnore]
    public CancellationToken RetirementToken
    {
        get { var binding = Binding; binding?.Owner.Start(throwIfStopped: false); return binding?.Token ?? new CancellationToken(canceled: true); }
    }

    /// <summary>Validates all exact identity fields against the receiver's current visible implementation.</summary>
    public ValueTask EnsureCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Binding?.EnsureCurrentAsync(cancellationToken) ?? ValueTask.FromException(Unbound());
    }

    /// <summary>Reports a semantic invariant violation only for this exact, current implementation.</summary>
    /// <remarks>Permission, transport and structured RPC failures retain their original classifications and dispatch metadata.</remarks>
    public ValueTask<bool> TryReportInvariantViolationAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        cancellationToken.ThrowIfCancellationRequested();
        return Binding?.TryReportAsync(exception, cancellationToken) ?? ValueTask.FromException<bool>(Unbound());
    }
}
