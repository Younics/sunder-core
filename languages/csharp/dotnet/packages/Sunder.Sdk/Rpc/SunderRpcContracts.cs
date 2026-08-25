using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Rpc;

/// <summary>Defines stable Sunder RPC protocol values.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public static class SunderRpcProtocol
{
    /// <summary>The standalone V1 contract descriptor version retained for compatibility utilities.</summary>
    public const int DescriptorVersionV1 = 1;

    /// <summary>The Sunder RPC V2 contract descriptor version.</summary>
    public const int DescriptorVersionV2 = 2;

    /// <summary>The current deployable contract descriptor version.</summary>
    public const int DescriptorVersion = DescriptorVersionV2;

    /// <summary>The Draft 2020-12 metaschema URI accepted on descriptors.</summary>
    public const string JsonSchemaDialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>The normative value-schema profile required by Sunder RPC V2 descriptors.</summary>
    public const string ValueSchemaProfile = "sunder.rpc.values.v1";

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
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public enum SunderRpcMethodKind
{
    /// <summary>One request produces one response.</summary>
    Unary,

    /// <summary>One request produces a bounded server event stream.</summary>
    ServerStream,
}

/// <summary>Identifies whether repeating a completed RPC method has the same externally visible effect.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public enum SunderRpcMethodIdempotency
{
    /// <summary>Repeating a dispatched call may repeat effects.</summary>
    None,

    /// <summary>Repeating a completed call has the same externally visible effect as one invocation.</summary>
    Idempotent,
}

/// <summary>Describes one method in a schema-first RPC service.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed class SunderRpcMethodDescriptor
{
    private readonly IReadOnlyList<string> _errors;

    internal SunderRpcMethodDescriptor(
        string methodId,
        SunderRpcMethodKind kind,
        string requestSchemaReference,
        string outputSchemaReference,
        SunderRpcMethodIdempotency idempotency,
        IEnumerable<string> errors)
    {
        MethodId = methodId;
        Kind = kind;
        RequestSchemaReference = requestSchemaReference;
        OutputSchemaReference = outputSchemaReference;
        Idempotency = idempotency;
        _errors = Array.AsReadOnly(errors.ToArray());
    }

    /// <summary>Gets the stable service-local method identifier.</summary>
    public string MethodId { get; }

    /// <summary>Gets the method invocation shape.</summary>
    public SunderRpcMethodKind Kind { get; }

    /// <summary>Gets the local request schema reference.</summary>
    public string RequestSchemaReference { get; }

    /// <summary>Gets the local response or event schema reference.</summary>
    public string OutputSchemaReference { get; }

    /// <summary>Gets the method's declared idempotency semantics.</summary>
    public SunderRpcMethodIdempotency Idempotency { get; }

    /// <summary>Gets the immutable list of domain error codes declared by the method.</summary>
    public IReadOnlyList<string> Errors => _errors;
}

/// <summary>Describes one contract-wide domain error declaration.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed class SunderRpcErrorDeclaration
{
    internal SunderRpcErrorDeclaration(
        string code,
        string? description,
        string? detailsSchema)
    {
        Code = code;
        Description = description;
        DetailsSchema = detailsSchema;
    }

    /// <summary>Gets the stable domain error code.</summary>
    public string Code { get; }

    /// <summary>Gets the optional human-readable declaration annotation.</summary>
    public string? Description { get; }

    /// <summary>Gets the optional local schema reference for machine-readable error details.</summary>
    public string? DetailsSchema { get; }
}

/// <summary>Describes one service in a schema-first RPC contract.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
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

/// <summary>Represents a validated, immutable Sunder RPC contract descriptor.</summary>
/// <remarks>
/// V1 descriptor numeric tokens are restricted to canonical safe integers. V2 accepts finite binary64
/// descriptor numbers and canonicalizes them using the RFC 8785 number representation.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed class SunderRpcContractDescriptor
{
    private readonly IReadOnlyList<SunderRpcServiceDescriptor> _services;
    private readonly IReadOnlyDictionary<string, SunderRpcServiceDescriptor> _serviceMap;
    private readonly IReadOnlyDictionary<string, SunderRpcErrorDeclaration> _errors;
    private readonly IReadOnlyDictionary<string, JsonElement> _definitions;
    private readonly byte[] _canonicalUtf8;
    private readonly byte[]? _canonicalAbiProjectionUtf8;

    internal SunderRpcContractDescriptor(
        int descriptorVersion,
        string? schemaProfile,
        string contractId,
        string version,
        IEnumerable<SunderRpcErrorDeclaration> errors,
        IEnumerable<SunderRpcServiceDescriptor> services,
        IEnumerable<KeyValuePair<string, JsonElement>> definitions,
        byte[] canonicalUtf8,
        string sha256,
        byte[]? canonicalAbiProjectionUtf8,
        string? abiSha256)
    {
        DescriptorVersion = descriptorVersion;
        SchemaProfile = schemaProfile;
        ContractId = contractId;
        Version = version;
        var serviceSnapshot = services.ToArray();
        _services = Array.AsReadOnly(serviceSnapshot);
        _serviceMap = new ReadOnlyDictionary<string, SunderRpcServiceDescriptor>(
            serviceSnapshot.ToDictionary(static service => service.ServiceId, StringComparer.Ordinal));
        _errors = new ReadOnlyDictionary<string, SunderRpcErrorDeclaration>(
            errors.ToDictionary(static declaration => declaration.Code, StringComparer.Ordinal));
        _definitions = new ReadOnlyDictionary<string, JsonElement>(
            definitions.ToDictionary(static pair => pair.Key, static pair => pair.Value.Clone(), StringComparer.Ordinal));
        _canonicalUtf8 = canonicalUtf8.ToArray();
        _canonicalAbiProjectionUtf8 = canonicalAbiProjectionUtf8?.ToArray();
        Sha256 = sha256;
        AbiSha256 = abiSha256;
    }

    /// <summary>Gets the parsed descriptor version.</summary>
    public int DescriptorVersion { get; }

    /// <summary>Gets the selected V2 value-schema profile, or <see langword="null"/> for V1.</summary>
    public string? SchemaProfile { get; }

    /// <summary>Gets the stable contract identifier.</summary>
    public string ContractId { get; }

    /// <summary>Gets the contract's independent strict semantic version.</summary>
    public string Version { get; }

    /// <summary>Gets the canonical descriptor SHA-256 as lowercase hexadecimal.</summary>
    public string Sha256 { get; }

    /// <summary>Gets the domain-separated V2 ABI SHA-256, or <see langword="null"/> for V1.</summary>
    public string? AbiSha256 { get; }

    /// <summary>Gets the immutable service list in descriptor order.</summary>
    public IReadOnlyList<SunderRpcServiceDescriptor> Services => _services;

    /// <summary>Gets immutable V2 domain error declarations keyed by code; V1 descriptors return an empty map.</summary>
    public IReadOnlyDictionary<string, SunderRpcErrorDeclaration> Errors => _errors;

    /// <summary>Gets immutable clones of the validated schemas keyed by definition name.</summary>
    public IReadOnlyDictionary<string, JsonElement> Definitions => _definitions;

    /// <summary>Parses and validates strict UTF-8 descriptor bytes.</summary>
    /// <exception cref="SunderRpcDescriptorException">The descriptor or one of its schemas is invalid.</exception>
    public static SunderRpcContractDescriptor Parse(ReadOnlySpan<byte> descriptorUtf8)
        => SunderRpcDescriptorParser.Parse(descriptorUtf8);

    /// <summary>Returns the deterministic JCS-compatible canonical descriptor bytes.</summary>
    public byte[] GetCanonicalUtf8() => _canonicalUtf8.ToArray();

    /// <summary>Returns the normalized canonical V2 ABI projection bytes, or <see langword="null"/> for V1.</summary>
    public byte[]? GetCanonicalAbiProjectionUtf8() => _canonicalAbiProjectionUtf8?.ToArray();

    /// <summary>Gets a service by its case-sensitive identifier.</summary>
    public SunderRpcServiceDescriptor? FindService(string serviceId)
        => serviceId is not null && _serviceMap.TryGetValue(serviceId, out var service) ? service : null;

    /// <summary>Gets a domain error declaration by its case-sensitive code.</summary>
    public SunderRpcErrorDeclaration? FindError(string code)
        => code is not null && _errors.TryGetValue(code, out var declaration) ? declaration : null;

    /// <summary>Validates a JSON value against a local definition reference.</summary>
    public bool IsValid(string schemaReference, JsonElement value, out string? error)
        => SunderRpcSchemaProfile.IsInstanceValid(this, schemaReference, value, out error);

    internal bool IsValidDetailed(
        string schemaReference,
        JsonElement value,
        out string? error,
        out SunderRpcValidationFailure? failure)
        => SunderRpcSchemaProfile.IsInstanceValid(this, schemaReference, value, out error, out failure);

    internal bool TryGetDefinition(string name, out JsonElement schema)
        => _definitions.TryGetValue(name, out schema);
}

internal sealed record SunderRpcValidationFailure(string Code, string Path);

/// <summary>Reports an invalid contract descriptor or unsupported schema construct.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed class SunderRpcDescriptorException : FormatException
{
    /// <summary>Creates a descriptor validation exception.</summary>
    public SunderRpcDescriptorException(string message)
        : base(message)
    {
        ErrorCode = "descriptor-invalid";
    }

    /// <summary>Creates a descriptor validation exception with an inner parse failure.</summary>
    public SunderRpcDescriptorException(string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = "descriptor-invalid";
    }

    internal SunderRpcDescriptorException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    internal SunderRpcDescriptorException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Gets the stable broad conformance rejection category.</summary>
    public string ErrorCode { get; }
}

/// <summary>An opaque reference to one exact provider activation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
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
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
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
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed record SunderRpcProviderSnapshot(
    string PackageId,
    string PackageVersion,
    string ProviderId,
    string ContractId,
    string ContractVersion,
    string DescriptorSha256,
    string? AbiSha256,
    Guid ActivationId,
    long ActivationEpoch,
    long SessionGeneration,
    SunderRpcEndpointReference Endpoint,
    long CatalogRevision,
    SunderRpcProviderState State,
    string? FaultCode = null);

/// <summary>Represents an immutable provider catalog view.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
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
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
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
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed record SunderRpcCatalogEvent(
    long Revision,
    long Sequence,
    SunderRpcCatalogEventKind Kind,
    SunderRpcProviderSnapshot? Provider);

/// <summary>Classifies safe RPC call failures.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
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

/// <summary>Describes whether provider handler entry may have occurred before an RPC failure.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public enum SunderRpcDispatchState
{
    /// <summary>The host rejected the call before entering the provider handler.</summary>
    NotDispatched,
    /// <summary>The provider handler may have observed the request.</summary>
    PossiblyDispatched,
}

/// <summary>Classifies whether retrying a failed call can duplicate externally visible effects.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public enum SunderRpcRetrySafety
{
    /// <summary>Retrying cannot duplicate externally visible effects under the V2 rules.</summary>
    Safe,
    /// <summary>Retrying may duplicate effects or replay stream events.</summary>
    Unsafe,
}

/// <summary>Contains a sanitized typed RPC failure safe to return across package boundaries.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed record SunderRpcError
{
    /// <summary>Creates a sanitized typed RPC failure.</summary>
    public SunderRpcError(
        SunderRpcErrorKind kind,
        string code,
        string message,
        SunderRpcDispatchState dispatchState = SunderRpcDispatchState.NotDispatched,
        SunderRpcRetrySafety retrySafety = SunderRpcRetrySafety.Safe,
        JsonElement? details = null)
    {
        if (details is { } detail)
        {
            if (kind != SunderRpcErrorKind.Domain)
            {
                throw new ArgumentException("Only domain RPC errors may carry details.", nameof(details));
            }
            if (detail.ValueKind == JsonValueKind.Undefined)
            {
                throw new ArgumentException("RPC error details must be a defined JSON value.", nameof(details));
            }
        }

        Kind = kind;
        Code = code;
        Message = message;
        DispatchState = dispatchState;
        RetrySafety = retrySafety;
        Details = details is { } value ? value.Clone() : null;
    }

    /// <summary>Gets the error kind.</summary>
    public SunderRpcErrorKind Kind { get; }

    /// <summary>Gets the stable error code.</summary>
    public string Code { get; }

    /// <summary>Gets the sanitized diagnostic message.</summary>
    public string Message { get; }

    /// <summary>Gets whether provider dispatch may have occurred.</summary>
    public SunderRpcDispatchState DispatchState { get; }

    /// <summary>Gets whether retrying may duplicate externally visible effects.</summary>
    public SunderRpcRetrySafety RetrySafety { get; }

    /// <summary>Gets optional immutable machine-readable domain error details.</summary>
    public JsonElement? Details { get; }
}

/// <summary>Represents a typed RPC failure.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed class SunderRpcException : Exception
{
    private readonly bool _hostAuthenticated;

    /// <summary>Creates an exception from a safe typed error.</summary>
    public SunderRpcException(SunderRpcError error)
        : base((error ?? throw new ArgumentNullException(nameof(error))).Message)
    {
        Error = error;
    }

    internal SunderRpcException(SunderRpcError error, bool hostAuthenticated)
        : this(error)
    {
        _hostAuthenticated = hostAuthenticated;
    }

    /// <summary>Gets the safe typed error.</summary>
    public SunderRpcError Error { get; }

    internal bool IsHostAuthenticated => _hostAuthenticated;

    internal SunderRpcException WithDispatchMetadata(
        SunderRpcDispatchState dispatchState,
        SunderRpcRetrySafety retrySafety)
        => Error.DispatchState == dispatchState && Error.RetrySafety == retrySafety
            ? this
            : new SunderRpcException(
                new SunderRpcError(
                    Error.Kind,
                    Error.Code,
                    Error.Message,
                    dispatchState,
                    retrySafety,
                    Error.Details),
                _hostAuthenticated);

    internal static SunderRpcException Infrastructure(
        SunderRpcErrorKind kind,
        string code,
        string message)
        => new(new SunderRpcError(kind, code, message), hostAuthenticated: true);
}

/// <summary>Controls whether a content reference can be acquired once or repeatedly.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public enum SunderRpcContentRepeatability
{
    /// <summary>The content may be acquired once.</summary>
    SingleUse,
    /// <summary>The content may be acquired up to its host-owned use limit.</summary>
    Repeatable,
}

/// <summary>Describes host-mediated content without exposing a stream or filesystem path.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed record SunderRpcContentReference(
    string Id,
    long Length,
    string Sha256,
    string MediaType,
    string FileName,
    DateTimeOffset ExpiresAtUtc,
    SunderRpcContentRepeatability Repeatability)
{
    internal string ExactExpiresAtUtcText { get; init; } = ExpiresAtUtc.ToUniversalTime().ToString(
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        CultureInfo.InvariantCulture);
}

/// <summary>Controls registration of bounded Host-mediated RPC content.</summary>
/// <param name="MediaType">The content media type.</param>
/// <param name="FileName">A display-only file name without path semantics.</param>
/// <param name="Length">The expected content length, or <see langword="null"/> when unknown.</param>
/// <param name="ExpiresAtUtc">An optional expiry no later than the invocation deadline and Host policy limit.</param>
/// <param name="Repeatability">Whether the content can be acquired once or repeatedly.</param>
/// <param name="MaximumUses">The bounded acquisition count.</param>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed record SunderRpcContentRegistrationOptions(
    string MediaType,
    string FileName,
    long? Length = null,
    DateTimeOffset? ExpiresAtUtc = null,
    SunderRpcContentRepeatability Repeatability = SunderRpcContentRepeatability.SingleUse,
    int MaximumUses = 1);

/// <summary>Contains host-stamped information for one provider invocation.</summary>
/// <remarks>
/// The Host creates this context and revokes its content authority when the handler or stream ends.
/// Retaining a context never extends an invocation. Returned streams are local conveniences and
/// never cross the RPC boundary.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed class SunderRpcInvocationContext
{
    private readonly ISunderRpcInvocationAuthority _authority;

    internal SunderRpcInvocationContext(
        string callerPackageId,
        string callerPackageVersion,
        SunderRpcProviderSnapshot provider,
        DateTimeOffset deadlineUtc,
        int callDepth,
        CancellationToken cancellationToken,
        ISunderRpcInvocationAuthority? authority = null)
    {
        CallerPackageId = callerPackageId;
        CallerPackageVersion = callerPackageVersion;
        Provider = provider;
        DeadlineUtc = deadlineUtc;
        CallDepth = callDepth;
        CancellationToken = cancellationToken;
        _authority = authority ?? UnavailableSunderRpcInvocationAuthority.Instance;
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

    /// <summary>Copies bounded provider content into a reference readable by this invocation's caller scope.</summary>
    public ValueTask<SunderRpcContentReference> RegisterContentAsync(
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
        => _authority.RegisterContentAsync(source, options, cancellationToken);

    /// <summary>Copies a bounded provider-owned file into a reference readable by this invocation's caller scope.</summary>
    public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default)
        => _authority.RegisterContentFileAsync(filePath, options, cancellationToken);

    /// <summary>Opens validated request content registered by this invocation's caller scope.</summary>
    public ValueTask<Stream> OpenContentAsync(
        SunderRpcContentReference reference,
        CancellationToken cancellationToken = default)
        => _authority.OpenContentAsync(reference, cancellationToken);

    internal void Revoke() => _authority.Revoke();
}

internal interface ISunderRpcInvocationAuthority
{
    CancellationToken RevocationToken { get; }

    ValueTask<SunderRpcContentReference> RegisterContentAsync(
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken);

    ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken);

    ValueTask<Stream> OpenContentAsync(
        SunderRpcContentReference reference,
        CancellationToken cancellationToken);

    void Revoke();
}

internal sealed class UnavailableSunderRpcInvocationAuthority : ISunderRpcInvocationAuthority
{
    public static UnavailableSunderRpcInvocationAuthority Instance { get; } = new();

    private UnavailableSunderRpcInvocationAuthority()
    {
    }

    public CancellationToken RevocationToken => CancellationToken.None;

    public ValueTask<SunderRpcContentReference> RegisterContentAsync(
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
        => ValueTask.FromException<SunderRpcContentReference>(Unavailable());

    public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken)
        => ValueTask.FromException<SunderRpcContentReference>(Unavailable());

    public ValueTask<Stream> OpenContentAsync(
        SunderRpcContentReference reference,
        CancellationToken cancellationToken)
        => ValueTask.FromException<Stream>(Unavailable());

    public void Revoke()
    {
    }

    private static SunderRpcException Unavailable()
        => SunderRpcException.Infrastructure(
            SunderRpcErrorKind.Unavailable,
            "rpc.content.authority-unavailable",
            "Host-mediated RPC content authority is unavailable for this invocation.");
}

/// <summary>
/// Supplies an optional requested deadline for an RPC call. The Host always honors earlier
/// deadlines and may honor a later deadline only for a target that declares extended-deadline support.
/// </summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed record SunderRpcCallOptions(DateTimeOffset? DeadlineUtc = null);

/// <summary>Describes the validated Host resource limits effective for RPC calls.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public sealed record SunderRpcResourceProfile(
    string EffectiveProfileHash,
    int MaximumRpcValueBytes,
    int MaximumValueDepth,
    int MaximumValueNodes,
    int MaximumObjectMembers,
    int MaximumInlineDecodedBytes,
    int MaximumContentReferenceBytes);

/// <summary>Handles validated JSON RPC requests for one declared provider.</summary>
/// <remarks>Handlers are activation-scoped and may be called concurrently. They must observe cancellation.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
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

/// <summary>Exposes the immutable domain-error declarations implemented by a generated RPC provider adapter.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public interface ISunderRpcDeclaredErrorMetadata
{
    /// <summary>Gets error codes keyed by exact service id and method id.</summary>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors { get; }
}

/// <summary>Discovers and invokes providers visible to the current host-stamped package activation.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public interface ISunderRpcClient
{
    /// <summary>Gets the validated Host resource profile, or <see langword="null"/> when none was reported.</summary>
    SunderRpcResourceProfile? ResourceProfile => null;

    /// <summary>Creates caller-owned authority for calls and Host-mediated request or response content.</summary>
    /// <remarks>Dispose the scope to cancel its work and revoke all remaining content references.</remarks>
    ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromException<ISunderRpcCallScope>(new NotSupportedException(
            "This RPC client does not provide caller-owned call scopes."));

    /// <summary>Gets a visible exact provider snapshot by opaque endpoint reference.</summary>
    ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(
        SunderRpcEndpointReference endpoint,
        CancellationToken cancellationToken = default);

    /// <summary>Attempts to fault the exact active provider endpoint after a caller-detected invariant violation.</summary>
    ValueTask<bool> TryReportInvariantViolationAsync(
        SunderRpcEndpointReference endpoint,
        Exception exception,
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

/// <summary>Owns bounded RPC call and content authority until asynchronously disposed.</summary>
/// <remarks>
/// A scope may call more than one endpoint. Request content registration names its exact target;
/// response content is readable only when it was produced for this scope. Disposal cancels active
/// work and revokes every unconsumed content reference owned by the scope.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public interface ISunderRpcCallScope : ISunderRpcClient, IAsyncDisposable
{
    /// <summary>
    /// Gets the Host-clamped scope deadline. A target without extended-deadline support may receive
    /// an earlier per-call deadline.
    /// </summary>
    DateTimeOffset DeadlineUtc { get; }

    /// <summary>Registers bounded request content for one exact target endpoint.</summary>
    ValueTask<SunderRpcContentReference> RegisterContentAsync(
        SunderRpcEndpointReference endpoint,
        Stream source,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Registers a bounded caller-owned file as request content for one exact target endpoint.</summary>
    ValueTask<SunderRpcContentReference> RegisterContentFileAsync(
        SunderRpcEndpointReference endpoint,
        string filePath,
        SunderRpcContentRegistrationOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Opens validated response content produced for this scope.</summary>
    ValueTask<Stream> OpenContentAsync(
        SunderRpcContentReference reference,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides generated-binding-friendly typed JSON adapters.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV2)]
public static class SunderRpcClientExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static readonly JsonSerializerOptions V2JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SunderRpcV2JsonConverterFactory() },
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
               ?? throw SunderRpcException.Infrastructure(
                   SunderRpcErrorKind.Protocol,
                   "rpc.binding.null-response",
                   "The RPC response could not be deserialized as the generated response type.");
    }

    /// <summary>Serializes and deserializes typed unary values using schema-derived root nullability.</summary>
    public static async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        this ISunderRpcClient client,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        TRequest request,
        bool requestAllowsNull,
        bool responseAllowsNull,
        bool useV2WireFormat = false,
        SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (request is null && !requestAllowsNull)
        {
            throw new ArgumentNullException(nameof(request));
        }
        var jsonOptions = useV2WireFormat ? V2JsonOptions : JsonOptions;
        var requestElement = JsonSerializer.SerializeToElement(request, jsonOptions);
        var response = await client.InvokeAsync(
            endpoint,
            serviceId,
            methodId,
            requestElement,
            options,
            cancellationToken).ConfigureAwait(false);
        var result = response.Deserialize<TResponse>(jsonOptions);
        if (result is null && !responseAllowsNull)
        {
            throw SunderRpcException.Infrastructure(
                SunderRpcErrorKind.Protocol,
                "rpc.binding.null-response",
                "The RPC response could not be deserialized as the generated response type.");
        }
        return result!;
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
                         ?? throw SunderRpcException.Infrastructure(
                             SunderRpcErrorKind.Protocol,
                             "rpc.binding.null-event",
                             "An RPC event could not be deserialized as the generated event type.");
        }
    }

    /// <summary>Serializes and deserializes typed stream values using schema-derived root nullability.</summary>
    public static async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        this ISunderRpcClient client,
        SunderRpcEndpointReference endpoint,
        string serviceId,
        string methodId,
        TRequest request,
        bool requestAllowsNull,
        bool eventAllowsNull,
        bool useV2WireFormat = false,
        SunderRpcCallOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (request is null && !requestAllowsNull)
        {
            throw new ArgumentNullException(nameof(request));
        }
        var jsonOptions = useV2WireFormat ? V2JsonOptions : JsonOptions;
        var requestElement = JsonSerializer.SerializeToElement(request, jsonOptions);
        await foreach (var item in client.SubscribeAsync(
                           endpoint,
                           serviceId,
                           methodId,
                           requestElement,
                           options,
                           cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var result = item.Deserialize<TEvent>(jsonOptions);
            if (result is null && !eventAllowsNull)
            {
                throw SunderRpcException.Infrastructure(
                    SunderRpcErrorKind.Protocol,
                    "rpc.binding.null-event",
                    "An RPC event could not be deserialized as the generated event type.");
            }
            yield return result!;
        }
    }
}
