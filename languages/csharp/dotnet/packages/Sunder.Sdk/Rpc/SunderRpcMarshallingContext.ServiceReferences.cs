using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Services;

namespace Sunder.Sdk.Rpc;

public sealed partial class SunderRpcMarshallingContext
{
    private readonly Dictionary<Type, IServiceReferenceBinding> _serviceBindings = [];
    private readonly HashSet<SunderRpcProviderSnapshot> _strictReferenceWrites = [];
    private readonly HashSet<SunderRpcProviderSnapshot> _strictReferenceReads = [];
    private readonly HashSet<SunderRpcServiceBinding> _requiredReferenceParticipants = [];
    private readonly ISunderRpcClient? _receiver;

    /// <summary>Installs a generated typed factory for a validated projected service target before JSON options are used.</summary>
    /// <remarks>
    /// The factory receives the receiver's original client, never the sender's service or the temporary call scope.
    /// It must capture the receiver's DI-owned generated service lifetime and create an exact-bound proxy.
    /// </remarks>
    public void AddServiceReference<TService>(SunderRpcContractDescriptor contract, string serviceId,
        Func<ISunderRpcClient, SunderRpcProviderSnapshot, TService> createService) where TService : class
    {
        ArgumentNullException.ThrowIfNull(createService);
        AddServiceReference(contract, serviceId, _serviceBindingLifetime
            ?? throw ServiceReference<TService>.Unbound(), (client, provider, _) => createService(client, provider));
    }

    /// <summary>Installs a generated reference factory borrowing the receiver's shared binding observer.</summary>
    /// <remarks>The generated proxy must observe the supplied permanent retirement token on every operation.</remarks>
    public void AddServiceReference<TService>(SunderRpcContractDescriptor contract, string serviceId,
        SunderRpcServiceBindingLifetime serviceBindings,
        Func<ISunderRpcClient, SunderRpcProviderSnapshot, CancellationToken, TService> createService) where TService : class
    {
        Check();
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(serviceBindings);
        ArgumentNullException.ThrowIfNull(createService);
        if (!ReferenceEquals(serviceBindings.Client, Receiver))
            throw new ArgumentException("Service references must use the receiver's own binding lifetime.", nameof(serviceBindings));
        if (contract.FindService(serviceId) is null)
            throw new ArgumentException("The projected service target is absent from the selected descriptor.", nameof(serviceId));
        if (JsonOptions.IsReadOnly) throw new InvalidOperationException("Install service-reference bindings before serialization.");
        if (_serviceBindings.ContainsKey(typeof(TService)))
            throw new InvalidOperationException("A generated service type must have exactly one projected service target.");
        var binding = new ServiceReferenceBinding<TService>(this, contract, serviceBindings, createService);
        _serviceBindings.Add(typeof(TService), binding);
        JsonOptions.Converters.Insert(0, binding);
    }

    /// <summary>Validates a visited bound reference against the sender's visible exact snapshot before serialization.</summary>
    public async ValueTask RegisterServiceReferenceAsync<TService>(ServiceReference<TService>? reference,
        CancellationToken cancellationToken = default) where TService : class
    {
        Check();
        if (reference is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_owner.Token, cancellationToken);
        await Binding<TService>().RegisterAsync(reference, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Prepares an explicitly optional nullable reference occurrence; safe exact absence writes null without coupling retirement to this operation.</summary>
    public async ValueTask RegisterOptionalServiceReferenceAsync<TService>(ServiceReference<TService>? reference,
        CancellationToken cancellationToken = default) where TService : class
    {
        Check();
        if (reference is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_owner.Token, cancellationToken);
        await Binding<TService>().RegisterAsync(reference, linked.Token, optional: true).ConfigureAwait(false);
    }

    /// <summary>Resolves one generated JSON locator path against the receiver's visibility before synchronous decoding.</summary>
    /// <remarks>Null is a no-op for nullable/container projections. Both increased and decreased generations are rejected.</remarks>
    public async ValueTask PrepareServiceReferenceAsync<TService>(JsonElement locator,
        CancellationToken cancellationToken = default) where TService : class
    {
        Check();
        if (locator.ValueKind == JsonValueKind.Null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_owner.Token, cancellationToken);
        await Binding<TService>().PrepareAsync(locator, linked.Token).ConfigureAwait(false);
    }

    /// <summary>Prepares an explicitly optional nullable locator occurrence; safe exact absence decodes to null, never a replacement or stale callable reference.</summary>
    public async ValueTask PrepareOptionalServiceReferenceAsync<TService>(JsonElement locator,
        CancellationToken cancellationToken = default) where TService : class
    {
        Check();
        if (locator.ValueKind == JsonValueKind.Null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_owner.Token, cancellationToken);
        await Binding<TService>().PrepareAsync(locator, linked.Token, optional: true).ConfigureAwait(false);
    }

    private ServiceReferenceBinding<TService> Binding<TService>() where TService : class
        => _serviceBindings.TryGetValue(typeof(TService), out var binding)
            ? (ServiceReferenceBinding<TService>)binding
            : throw new InvalidOperationException("The generated service-reference target has not been installed.");

    private ISunderRpcClient Receiver => _receiver
        ?? throw new InvalidOperationException("Decoding service references requires the receiver's own RPC client.");

    private void RetainRequiredReference(SunderRpcServiceBinding binding)
    {
        _owner.RetainParticipant(binding);
        _requiredReferenceParticipants.Add(binding);
    }

    partial void CheckRequiredReferences()
    {
        // CancelAsync marks the binding before its callbacks run. A fast response
        // must not escape during propagation to the operation cancellation token.
        foreach (var binding in _requiredReferenceParticipants)
        {
            try { binding.ThrowIfRetired(); }
            catch (SunderRpcException error)
            {
                _ = _owner.DisposeAsync();
                // At this operation boundary effects may already have occurred.
                // Keep the error classification without manufacturing safe retry.
                throw error.WithDispatchMetadata(SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe);
            }
        }
    }

    partial void ClearOutgoingReferences()
    {
        foreach (var binding in _serviceBindings.Values) binding.ClearOutgoing();
        _strictReferenceWrites.Clear();
    }

    partial void ClearIncomingReferences()
    {
        foreach (var binding in _serviceBindings.Values) binding.ClearIncoming();
        _strictReferenceReads.Clear();
    }

    private interface IServiceReferenceBinding
    {
        void ClearOutgoing();
        void ClearIncoming();
    }

    private sealed class ServiceReferenceBinding<TService>(SunderRpcMarshallingContext context,
        SunderRpcContractDescriptor contract, SunderRpcServiceBindingLifetime serviceBindings,
        Func<ISunderRpcClient, SunderRpcProviderSnapshot, CancellationToken, TService> createService)
        : JsonConverter<ServiceReference<TService>>, IServiceReferenceBinding where TService : class
    {
        private readonly Dictionary<ServiceReference<TService>, bool> _outgoing = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<SunderRpcProviderSnapshot, IncomingReference> _incoming = [];
        private sealed record IncomingReference(ServiceReference<TService>? Reference, bool Optional);

        private static SunderRpcProviderSnapshot Identity(SunderRpcProviderSnapshot snapshot)
            => snapshot with { CatalogRevision = 0, State = SunderRpcProviderState.Active, FaultCode = null };

        internal async ValueTask RegisterAsync(ServiceReference<TService> reference, CancellationToken cancellationToken, bool optional = false)
        {
            MatchContract(reference.Snapshot);
            var identity = Identity(reference.Snapshot);
            if (!optional) context._strictReferenceWrites.Add(identity);
            optional &= !context._strictReferenceWrites.Contains(identity);
            if (optional)
            {
                var existing = reference.Binding;
                if (existing is null)
                {
                    existing = await serviceBindings.ResolveOptionalBindingAsync(reference.Snapshot, cancellationToken).ConfigureAwait(false);
                    if (existing is not null) existing = reference.Attach(existing);
                }
                var available = existing is not null
                    && await existing.Owner.EnsureOptionalCurrentAsync(existing, cancellationToken).ConfigureAwait(false);
                if (available && !ReferenceEquals(existing!.Owner.Client, context.Receiver))
                    available = await serviceBindings.ResolveOptionalBindingAsync(reference.Snapshot, cancellationToken).ConfigureAwait(false) is not null;
                cancellationToken.ThrowIfCancellationRequested();
                _outgoing[reference] = available;
                return;
            }
            var binding = reference.Binding ?? reference.Attach(
                await serviceBindings.ResolveBindingAsync(reference.Snapshot, cancellationToken).ConfigureAwait(false));
            binding.ThrowIfRetired();
            if (context._scope is not null) context.RetainRequiredReference(binding);
            await reference.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(binding.Owner.Client, context.Receiver))
                await SunderRpcServiceLocator.ResolveAsync(context.Receiver, reference.Snapshot, cancellationToken).ConfigureAwait(false);
            _outgoing[reference] = true;
            // A distinct wrapper with the same locator cannot use an earlier
            // optional absence to downgrade this strict occurrence.
            foreach (var (other, available) in _outgoing.Where(pair => Identity(pair.Key.Snapshot) == identity))
            {
                if (!available) throw SunderRpcServiceLocator.Stale();
                other.Binding?.ThrowIfRetired();
            }
        }

        internal async ValueTask PrepareAsync(JsonElement locator, CancellationToken cancellationToken, bool optional = false)
        {
            var snapshot = SunderRpcServiceLocator.Read(locator, context.JsonOptions);
            MatchContract(snapshot);
            if (!optional) context._strictReferenceReads.Add(snapshot);
            optional &= !context._strictReferenceReads.Contains(snapshot);
            if (_incoming.TryGetValue(snapshot, out var existing))
            {
                optional &= existing.Optional;
                if (optional) return;
                // Strict occurrence always validates/enrolls, including after an
                // optional-first cache hit or absent resolution.
            }
            ServiceReference<TService>? reference;
            if (optional)
            {
                var binding = await serviceBindings.ResolveOptionalBindingAsync(snapshot, cancellationToken).ConfigureAwait(false);
                reference = binding is null ? null : serviceBindings.CreateProvider(binding.Snapshot, createService).Reference;
            }
            else reference = await serviceBindings.ResolveAsync(snapshot, createService, cancellationToken).ConfigureAwait(false);
            if (!optional && context._invocation is not null) context.RetainRequiredReference(reference!.Binding!);
            cancellationToken.ThrowIfCancellationRequested();
            _incoming[snapshot] = new(reference, optional);
        }

        public override ServiceReference<TService>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            context.Check();
            using var value = JsonDocument.ParseValue(ref reader);
            var locator = SunderRpcServiceLocator.Read(value.RootElement, options);
            if (!_incoming.TryGetValue(locator, out var entry))
                throw new JsonException("The generated receive visitor must resolve service references before decoding.");
            var reference = entry.Reference;
            if (entry.Optional && !context._strictReferenceReads.Contains(locator)
                && (reference is null || !SunderRpcOptionalReferenceAvailability.IsAvailable(reference.Binding!, context.CancellationToken))) return null;
            if (reference is null) throw SunderRpcServiceLocator.Stale();
            reference.Binding?.ThrowIfRetired();
            return reference;
        }

        public override void Write(Utf8JsonWriter writer, ServiceReference<TService> value, JsonSerializerOptions options)
        {
            context.Check();
            if (!_outgoing.TryGetValue(value, out var available))
                throw new JsonException("The generated send visitor must validate service references before serialization.");
            if (!context._strictReferenceWrites.Contains(Identity(value.Snapshot)) && (!available
                || value.Binding is { } binding && !SunderRpcOptionalReferenceAvailability.IsAvailable(binding, context.CancellationToken)))
            { writer.WriteNullValue(); return; }
            if (!available) throw SunderRpcServiceLocator.Stale();
            value.Binding?.ThrowIfRetired();
            SunderRpcServiceLocator.Write(writer, value.Snapshot);
        }

        private void MatchContract(SunderRpcProviderSnapshot snapshot)
        {
            SunderRpcServiceLocator.Validate(snapshot);
            if (snapshot.ContractId != contract.ContractId || snapshot.ContractVersion != contract.Version
                || snapshot.DescriptorSha256 != contract.Sha256 || snapshot.AbiSha256 != contract.AbiSha256)
                throw new JsonException("The service reference does not match its selected projected contract.");
        }

        public void ClearOutgoing() => _outgoing.Clear();
        public void ClearIncoming() => _incoming.Clear();
    }
}
