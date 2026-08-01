using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Rpc;

/// <summary>Defines stable Sunder RPC V1 protocol values.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public static class SunderRpcProtocol
{
    /// <summary>The supported contract descriptor version.</summary>
    public const int DescriptorVersion = 1;

    /// <summary>The Draft 2020-12 metaschema URI accepted on descriptors.</summary>
    public const string JsonSchemaDialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>The maximum accepted descriptor size in bytes.</summary>
    public const int MaximumDescriptorBytes = 4 * 1024 * 1024;

    /// <summary>The discover permission action.</summary>
    public const string DiscoverAction = "discover";

    /// <summary>The unary invocation permission action.</summary>
    public const string InvokeAction = "invoke";

    /// <summary>The server-stream subscription permission action.</summary>
    public const string SubscribeAction = "subscribe";
}

/// <summary>Identifies the invocation shape of an RPC method.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum SunderRpcMethodKind
{
    /// <summary>One request produces one response.</summary>
    Unary,

    /// <summary>One request produces a bounded server event stream.</summary>
    ServerStream,
}

/// <summary>Describes one method in a schema-first RPC service.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcMethodDescriptor
{
    internal SunderRpcMethodDescriptor(
        string methodId,
        SunderRpcMethodKind kind,
        string requestSchemaReference,
        string outputSchemaReference)
    {
        MethodId = methodId;
        Kind = kind;
        RequestSchemaReference = requestSchemaReference;
        OutputSchemaReference = outputSchemaReference;
    }

    /// <summary>Gets the stable service-local method identifier.</summary>
    public string MethodId { get; }

    /// <summary>Gets the method invocation shape.</summary>
    public SunderRpcMethodKind Kind { get; }

    /// <summary>Gets the local request schema reference.</summary>
    public string RequestSchemaReference { get; }

    /// <summary>Gets the local response or event schema reference.</summary>
    public string OutputSchemaReference { get; }
}

/// <summary>Describes one service in a schema-first RPC contract.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcServiceDescriptor
{
    private readonly IReadOnlyList<SunderRpcMethodDescriptor> _methods;
    private readonly IReadOnlyDictionary<string, SunderRpcMethodDescriptor> _methodMap;

    internal SunderRpcServiceDescriptor(string serviceId, IEnumerable<SunderRpcMethodDescriptor> methods)
    {
        ServiceId = serviceId;
        var snapshot = methods.ToArray();
        _methods = Array.AsReadOnly(snapshot);
        _methodMap = new ReadOnlyDictionary<string, SunderRpcMethodDescriptor>(
            snapshot.ToDictionary(static method => method.MethodId, StringComparer.Ordinal));
    }

    /// <summary>Gets the stable contract-local service identifier.</summary>
    public string ServiceId { get; }

    /// <summary>Gets the immutable method list in descriptor order.</summary>
    public IReadOnlyList<SunderRpcMethodDescriptor> Methods => _methods;

    /// <summary>Gets a method by its case-sensitive identifier.</summary>
    public SunderRpcMethodDescriptor? FindMethod(string methodId)
        => methodId is not null && _methodMap.TryGetValue(methodId, out var method) ? method : null;
}

/// <summary>Represents a validated, immutable Sunder RPC V1 contract descriptor.</summary>
/// <remarks>
/// Descriptor numeric tokens are restricted to canonical safe integers in the range
/// -9007199254740991 through 9007199254740991. This makes canonical hashing deterministic across
/// implementations. A schema whose type is <c>number</c> may still validate fractional instance values.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcContractDescriptor
{
    private readonly IReadOnlyList<SunderRpcServiceDescriptor> _services;
    private readonly IReadOnlyDictionary<string, SunderRpcServiceDescriptor> _serviceMap;
    private readonly IReadOnlyDictionary<string, JsonElement> _definitions;
    private readonly byte[] _canonicalUtf8;

    internal SunderRpcContractDescriptor(
        string contractId,
        string version,
        IEnumerable<SunderRpcServiceDescriptor> services,
        IEnumerable<KeyValuePair<string, JsonElement>> definitions,
        byte[] canonicalUtf8,
        string sha256)
    {
        ContractId = contractId;
        Version = version;
        var serviceSnapshot = services.ToArray();
        _services = Array.AsReadOnly(serviceSnapshot);
        _serviceMap = new ReadOnlyDictionary<string, SunderRpcServiceDescriptor>(
            serviceSnapshot.ToDictionary(static service => service.ServiceId, StringComparer.Ordinal));
        _definitions = new ReadOnlyDictionary<string, JsonElement>(
            definitions.ToDictionary(static pair => pair.Key, static pair => pair.Value.Clone(), StringComparer.Ordinal));
        _canonicalUtf8 = canonicalUtf8.ToArray();
        Sha256 = sha256;
    }

    /// <summary>Gets descriptor version 1.</summary>
    public int DescriptorVersion => SunderRpcProtocol.DescriptorVersion;

    /// <summary>Gets the stable contract identifier.</summary>
    public string ContractId { get; }

    /// <summary>Gets the contract's independent strict semantic version.</summary>
    public string Version { get; }

    /// <summary>Gets the canonical descriptor SHA-256 as lowercase hexadecimal.</summary>
    public string Sha256 { get; }

    /// <summary>Gets the immutable service list in descriptor order.</summary>
    public IReadOnlyList<SunderRpcServiceDescriptor> Services => _services;

    /// <summary>Gets immutable clones of the validated schemas keyed by definition name.</summary>
    public IReadOnlyDictionary<string, JsonElement> Definitions => _definitions;

    /// <summary>Parses and validates strict UTF-8 descriptor bytes.</summary>
    /// <exception cref="SunderRpcDescriptorException">The descriptor or one of its schemas is invalid.</exception>
    public static SunderRpcContractDescriptor Parse(ReadOnlySpan<byte> descriptorUtf8)
        => SunderRpcDescriptorParser.Parse(descriptorUtf8);

    /// <summary>Returns the deterministic JCS-compatible canonical descriptor bytes.</summary>
    public byte[] GetCanonicalUtf8() => _canonicalUtf8.ToArray();

    /// <summary>Gets a service by its case-sensitive identifier.</summary>
    public SunderRpcServiceDescriptor? FindService(string serviceId)
        => serviceId is not null && _serviceMap.TryGetValue(serviceId, out var service) ? service : null;

    /// <summary>Validates a JSON value against a local definition reference.</summary>
    public bool IsValid(string schemaReference, JsonElement value, out string? error)
        => SunderRpcSchemaProfile.IsInstanceValid(this, schemaReference, value, out error);

    internal bool TryGetDefinition(string name, out JsonElement schema)
        => _definitions.TryGetValue(name, out schema);
}

/// <summary>Reports an invalid contract descriptor or unsupported schema construct.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcDescriptorException : FormatException
{
    /// <summary>Creates a descriptor validation exception.</summary>
    public SunderRpcDescriptorException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a descriptor validation exception with an inner parse failure.</summary>
    public SunderRpcDescriptorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An opaque reference to one exact provider activation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed record SunderRpcEndpointReference
{
    /// <summary>Creates an endpoint reference from a host-issued opaque value.</summary>
    public SunderRpcEndpointReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
        {
            throw new ArgumentException("An RPC endpoint reference must be non-empty and at most 256 characters.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the opaque host-issued value.</summary>
    public string Value { get; }
}

/// <summary>Identifies the lifecycle state of a provider snapshot.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum SunderRpcProviderState
{
    /// <summary>The provider is callable.</summary>
    Active,

    /// <summary>The provider activation is retired.</summary>
    Inactive,

    /// <summary>The exact provider activation faulted.</summary>
    Faulted,
}

/// <summary>Describes one host-stamped exact provider activation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed record SunderRpcProviderSnapshot(
    string PackageId,
    string PackageVersion,
    string ProviderId,
    string ContractId,
    string ContractVersion,
    string ContractSha256,
    Guid ActivationId,
    long ActivationEpoch,
    long SessionGeneration,
    SunderRpcEndpointReference Endpoint,
    long CatalogRevision,
    SunderRpcProviderState State,
    string? FaultCode = null);

/// <summary>Represents an immutable provider catalog view.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcCatalogSnapshot
{
    private readonly IReadOnlyList<SunderRpcProviderSnapshot> _providers;

    /// <summary>Creates an immutable catalog snapshot.</summary>
    public SunderRpcCatalogSnapshot(
        long revision,
        long sequence,
        IEnumerable<SunderRpcProviderSnapshot> providers,
        bool resetRequired = false)
    {
        ArgumentNullException.ThrowIfNull(providers);
        Revision = revision;
        Sequence = sequence;
        _providers = Array.AsReadOnly(providers.ToArray());
        ResetRequired = resetRequired;
    }

    /// <summary>Gets the atomic catalog revision.</summary>
    public long Revision { get; }

    /// <summary>Gets the latest replay sequence represented by the snapshot.</summary>
    public long Sequence { get; }

    /// <summary>Gets the immutable visible provider list.</summary>
    public IReadOnlyList<SunderRpcProviderSnapshot> Providers => _providers;

    /// <summary>Gets whether a watcher must replace local state with a fresh snapshot.</summary>
    public bool ResetRequired { get; }
}

/// <summary>Identifies a provider catalog transition.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum SunderRpcCatalogEventKind
{
    /// <summary>A provider identity entered the catalog.</summary>
    Added,

    /// <summary>A provider identity left the catalog.</summary>
    Removed,

    /// <summary>An exact provider activation became callable.</summary>
    Activated,

    /// <summary>An exact provider activation retired.</summary>
    Deactivated,

    /// <summary>An exact provider activation faulted.</summary>
    Faulted,

    /// <summary>Replay history was lost and the consumer must fetch a snapshot.</summary>
    ResetRequired,
}

/// <summary>Describes one ordered provider catalog transition.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed record SunderRpcCatalogEvent(
    long Revision,
    long Sequence,
    SunderRpcCatalogEventKind Kind,
    SunderRpcProviderSnapshot? Provider);

/// <summary>Classifies safe RPC call failures.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum SunderRpcErrorKind
{
    /// <summary>The provider rejected a valid request with a domain error.</summary>
    Domain,
    /// <summary>The caller lacks an effective permission.</summary>
    PermissionDenied,
    /// <summary>The requested contract, provider, service, or method was not found.</summary>
    NotFound,
    /// <summary>The endpoint identifies a retired exact activation.</summary>
    StaleEndpoint,
    /// <summary>The request or response failed schema validation.</summary>
    Validation,
    /// <summary>The inherited call deadline elapsed.</summary>
    DeadlineExceeded,
    /// <summary>The call was cancelled.</summary>
    Cancelled,
    /// <summary>A bounded concurrency, depth, event, or rate limit was reached.</summary>
    ResourceExhausted,
    /// <summary>The Runtime or activation is unavailable.</summary>
    Unavailable,
    /// <summary>The exact provider activation faulted.</summary>
    ProviderFaulted,
    /// <summary>An RPC transport or envelope invariant was violated.</summary>
    Protocol,
}

/// <summary>Contains a sanitized typed RPC failure safe to return across package boundaries.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed record SunderRpcError(SunderRpcErrorKind Kind, string Code, string Message);

/// <summary>Represents a typed RPC failure.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcException : Exception
{
    /// <summary>Creates an exception from a safe typed error.</summary>
    public SunderRpcException(SunderRpcError error)
        : base((error ?? throw new ArgumentNullException(nameof(error))).Message)
    {
        Error = error;
    }

    /// <summary>Gets the safe typed error.</summary>
    public SunderRpcError Error { get; }
}

/// <summary>Controls whether a content reference can be acquired once or repeatedly.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public enum SunderRpcContentRepeatability
{
    /// <summary>The content may be acquired once.</summary>
    SingleUse,
    /// <summary>The content may be acquired up to its host-owned use limit.</summary>
    Repeatable,
}

/// <summary>Describes host-mediated content without exposing a stream or filesystem path.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed record SunderRpcContentReference(
    string Id,
    long Length,
    string Sha256,
    string MediaType,
    string FileName,
    DateTimeOffset ExpiresAtUtc,
    SunderRpcContentRepeatability Repeatability);

/// <summary>Controls registration of bounded Host-mediated RPC content.</summary>
/// <param name="MediaType">The content media type.</param>
/// <param name="FileName">A display-only file name without path semantics.</param>
/// <param name="Length">The expected content length, or <see langword="null"/> when unknown.</param>
/// <param name="ExpiresAtUtc">An optional expiry no later than the invocation deadline and Host policy limit.</param>
/// <param name="Repeatability">Whether the content can be acquired once or repeatedly.</param>
/// <param name="MaximumUses">The bounded acquisition count.</param>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed record SunderRpcContentRegistrationOptions(
    string MediaType,
    string FileName,
    long? Length = null,
    DateTimeOffset? ExpiresAtUtc = null,
    SunderRpcContentRepeatability Repeatability = SunderRpcContentRepeatability.SingleUse,
    int MaximumUses = 1);

/// <summary>Registers and acquires package-scoped content references during an exact RPC invocation.</summary>
/// <remarks>
/// The Host binds every operation to the provider activation, caller audience, Runtime generation,
/// expiry, hash, and use count stamped by each invocation context. Returned streams are local
/// conveniences and never cross the RPC boundary.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public interface ISunderRpcContentClient
{
    /// <summary>Copies a bounded readable stream into Host-mediated content for the invocation caller.</summary>
    ValueTask<SunderRpcContentReference> RegisterAsync(
        SunderRpcInvocationContext context,
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Copies a bounded file into Host-mediated content for the invocation caller.</summary>
    ValueTask<SunderRpcContentReference> RegisterFileAsync(
        SunderRpcInvocationContext context,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Acquires validated content published by the invocation caller for this provider activation.</summary>
    ValueTask<Stream> OpenReadAsync(
        SunderRpcInvocationContext context,
        SunderRpcContentReference reference,
        CancellationToken cancellationToken = default);
}

/// <summary>Contains host-stamped information for one provider invocation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcInvocationContext
{
    /// <summary>Creates a host-stamped invocation context for a broker adapter.</summary>
    public SunderRpcInvocationContext(
        string callerPackageId,
        string callerPackageVersion,
        SunderRpcProviderSnapshot provider,
        DateTimeOffset deadlineUtc,
        int callDepth,
        CancellationToken cancellationToken)
    {
        CallerPackageId = callerPackageId;
        CallerPackageVersion = callerPackageVersion;
        Provider = provider;
        DeadlineUtc = deadlineUtc;
        CallDepth = callDepth;
        CancellationToken = cancellationToken;
    }

    /// <summary>Gets the host-stamped caller package identifier.</summary>
    public string CallerPackageId { get; }

    /// <summary>Gets the host-stamped caller package version.</summary>
    public string CallerPackageVersion { get; }

    /// <summary>Gets the exact callee provider snapshot.</summary>
    public SunderRpcProviderSnapshot Provider { get; }

    /// <summary>Gets the effective inherited UTC deadline.</summary>
    public DateTimeOffset DeadlineUtc { get; }

    /// <summary>Gets the one-based nested call depth.</summary>
    public int CallDepth { get; }

    /// <summary>Gets cancellation linked to request, deadline, activations, session, and Host shutdown.</summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>Supplies an optional earlier deadline for an RPC call.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed record SunderRpcCallOptions(DateTimeOffset? DeadlineUtc = null);

/// <summary>Handles validated JSON RPC requests for one declared provider.</summary>
/// <remarks>Handlers are activation-scoped and may be called concurrently. They must observe cancellation.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public interface ISunderRpcServiceHandler
{
    /// <summary>Handles one unary request and returns one non-disposed JSON response.</summary>
    ValueTask<JsonElement> InvokeUnaryAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default);

    /// <summary>Handles one server-stream request and returns non-disposed JSON events.</summary>
    IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default);
}

/// <summary>Discovers and invokes providers visible to the current host-stamped package activation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public interface ISunderRpcClient
{
    /// <summary>Gets a visible exact provider snapshot by opaque endpoint reference.</summary>
    ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default);

    /// <summary>Discovers visible providers satisfying one locally bundled contract import.</summary>
    ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(
        string contractId,
        CancellationToken cancellationToken = default);

    /// <summary>Watches visible catalog changes after a known revision and sequence.</summary>
    IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(
        long afterRevision,
        long afterSequence,
        CancellationToken cancellationToken = default);

    /// <summary>Invokes one unary method on an exact provider activation.</summary>
    ValueTask<JsonElement> InvokeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Subscribes to one server-stream method on an exact provider activation.</summary>
    IAsyncEnumerable<JsonElement> SubscribeAsync(
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        JsonElement request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides generated-binding-friendly typed JSON adapters.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public static class SunderRpcClientExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a typed request and deserializes a typed unary response.</summary>
    public static async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        this ISunderRpcClient client,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        TRequest request,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        var requestElement = JsonSerializer.SerializeToElement(request, JsonOptions);
        var response = await client.InvokeAsync(
            endpoint,
            serviceId,
            methodId,
            requestElement,
            options,
            cancellationToken).ConfigureAwait(false);
        return response.Deserialize<TResponse>(JsonOptions)
               ?? throw new SunderRpcException(new SunderRpcError(
                   SunderRpcErrorKind.Protocol,
                   "rpc.binding.null-response",
                   "The RPC response could not be deserialized as the generated response type."));
    }

    /// <summary>Serializes a typed request and deserializes typed server-stream events.</summary>
    public static async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        this ISunderRpcClient client,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        TRequest request,
        SunderRpcCallOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TRequest : notnull
        where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        var requestElement = JsonSerializer.SerializeToElement(request, JsonOptions);
        await foreach (var item in client.SubscribeAsync(
                           endpoint,
                           serviceId,
                           methodId,
                           requestElement,
                           options,
                           cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return item.Deserialize<TEvent>(JsonOptions)
                         ?? throw new SunderRpcException(new SunderRpcError(
                             SunderRpcErrorKind.Protocol,
                             "rpc.binding.null-event",
                             "An RPC event could not be deserialized as the generated event type."));
        }
    }
}
