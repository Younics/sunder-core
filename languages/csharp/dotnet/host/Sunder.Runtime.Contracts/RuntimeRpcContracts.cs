using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sunder.Runtime.Contracts;

public enum RuntimeRpcPermissionState
{
    Pending,
    Granted,
    Denied,
}

public sealed record RuntimeRpcPermissionKey(
    string CallerPackageId,
    string CallerPackageVersion,
    string ManifestSha256,
    string CallerRole,
    string ContractId,
    string ContractVersion,
    string AbiSha256,
    string Action);

public sealed record RuntimeRpcPermissionDescriptor(
    RuntimeRpcPermissionKey Key,
    bool Requested,
    RuntimeRpcPermissionState State,
    bool Effective,
    DateTimeOffset? UpdatedAtUtc);

public sealed record RuntimeRpcPermissionSnapshot(
    long Revision,
    IReadOnlyList<RuntimeRpcPermissionDescriptor> Permissions)
{
    private IReadOnlyList<RuntimeRpcPermissionDescriptor> _permissions =
        RuntimeContractCollections.Freeze(Permissions);

    public IReadOnlyList<RuntimeRpcPermissionDescriptor> Permissions
    {
        get => _permissions;
        init => _permissions = RuntimeContractCollections.Freeze(value);
    }
}

public sealed record RuntimeRpcPermissionUpdateRequest(RuntimeRpcPermissionKey Key);

public enum RuntimeRpcProviderState
{
    Active,
    Inactive,
    Faulted,
}

public sealed record RuntimeRpcProviderDescriptor(
    string PackageId,
    string PackageVersion,
    string ProviderId,
    string ContractId,
    string ContractVersion,
    string DescriptorSha256,
    string AbiSha256,
    Guid ActivationId,
    long ActivationEpoch,
    long SessionGeneration,
    string EndpointReference,
    long CatalogRevision,
    RuntimeRpcProviderState State,
    string? FaultCode);

public sealed record RuntimeRpcCatalogSnapshot(
    long Revision,
    long Sequence,
    IReadOnlyList<RuntimeRpcProviderDescriptor> Providers,
    bool ResetRequired)
{
    private IReadOnlyList<RuntimeRpcProviderDescriptor> _providers = RuntimeContractCollections.Freeze(Providers);

    public IReadOnlyList<RuntimeRpcProviderDescriptor> Providers
    {
        get => _providers;
        init => _providers = RuntimeContractCollections.Freeze(value);
    }
}

public enum RuntimeRpcCatalogEventKind
{
    Added,
    Removed,
    Activated,
    Deactivated,
    Faulted,
    ResetRequired,
}

public sealed record RuntimeRpcCatalogEventDescriptor(
    long Revision,
    long Sequence,
    RuntimeRpcCatalogEventKind Kind,
    RuntimeRpcProviderDescriptor? Provider);

public sealed record RuntimeRpcCatalogEventPage(
    long Revision,
    long Sequence,
    IReadOnlyList<RuntimeRpcCatalogEventDescriptor> Events,
    bool ResetRequired)
{
    private IReadOnlyList<RuntimeRpcCatalogEventDescriptor> _events = RuntimeContractCollections.Freeze(Events);

    public IReadOnlyList<RuntimeRpcCatalogEventDescriptor> Events
    {
        get => _events;
        init => _events = RuntimeContractCollections.Freeze(value);
    }
}

public sealed record RuntimeRpcAppSessionOpenRequest(
    string PackageId,
    string PackageVersion,
    string ManifestSha256,
    PackageTargetDescriptor Target,
    long SessionGeneration,
    Guid AppGenerationId)
{
    public string? LoadPermitId { get; init; }
}

public sealed record RuntimeRpcAppSessionDescriptor(string SessionId);

public sealed record RuntimeRpcAppSessionCloseRequest(string SessionId);

public sealed record RuntimeRpcAppCallScopeOpenRequest(
    string SessionId,
    DateTimeOffset? DeadlineUtc);

public sealed record RuntimeRpcAppCallScopeDescriptor(
    string CallScopeId,
    DateTimeOffset DeadlineUtc);

public sealed record RuntimeRpcAppCallScopeCloseRequest(
    string SessionId,
    string CallScopeId);

public sealed record RuntimeRpcAppDiscoverRequest(
    string SessionId,
    string ContractId,
    string? CallScopeId = null);

public sealed record RuntimeRpcAppProviderRequest(
    string SessionId,
    string EndpointReference,
    string? CallScopeId = null);

public sealed record RuntimeRpcAppInvariantViolationRequest(
    string SessionId,
    string EndpointReference,
    string ExceptionMessage,
    string? CallScopeId = null);

public sealed record RuntimeRpcAppWatchRequest(
    string SessionId,
    long AfterRevision,
    long AfterSequence,
    string? CallScopeId = null);

public sealed record RuntimeRpcAppInvokeRequest(
    string SessionId,
    string EndpointReference,
    string ServiceId,
    string MethodId,
    JsonElement Request,
    DateTimeOffset? DeadlineUtc,
    string? CallScopeId = null);

public enum RuntimeRpcContentRepeatability
{
    SingleUse,
    Repeatable,
}

public sealed record RuntimeRpcContentReferenceDescriptor(
    string Id,
    long Length,
    string Sha256,
    string MediaType,
    string FileName,
    DateTimeOffset ExpiresAtUtc,
    RuntimeRpcContentRepeatability Repeatability);

public sealed record RuntimeRpcAppContentRegisterMetadata(
    string SessionId,
    string CallScopeId,
    string EndpointReference,
    string MediaType,
    string FileName,
    long? Length,
    DateTimeOffset? ExpiresAtUtc,
    RuntimeRpcContentRepeatability Repeatability,
    int MaximumUses);

public sealed record RuntimeRpcAppContentOpenRequest(
    string SessionId,
    string CallScopeId,
    RuntimeRpcContentReferenceDescriptor Reference);

public enum RuntimeRpcErrorKind
{
    Domain,
    PermissionDenied,
    NotFound,
    StaleEndpoint,
    Validation,
    DeadlineExceeded,
    Cancelled,
    ResourceExhausted,
    Unavailable,
    ProviderFaulted,
    Protocol,
}

public enum RuntimeRpcDispatchState
{
    NotDispatched,
    PossiblyDispatched,
}

public enum RuntimeRpcRetrySafety
{
    Safe,
    Unsafe,
}

public sealed record RuntimeRpcErrorDescriptor
{
    [JsonConstructor]
    public RuntimeRpcErrorDescriptor(
        RuntimeRpcErrorKind kind,
        string code,
        string message,
        RuntimeRpcDispatchState dispatchState = RuntimeRpcDispatchState.NotDispatched,
        RuntimeRpcRetrySafety retrySafety = RuntimeRpcRetrySafety.Safe,
        JsonElement? details = null)
    {
        if (details is { } detail)
        {
            if (kind != RuntimeRpcErrorKind.Domain)
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

    public RuntimeRpcErrorKind Kind { get; }
    public string Code { get; }
    public string Message { get; }
    public RuntimeRpcDispatchState DispatchState { get; }
    public RuntimeRpcRetrySafety RetrySafety { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Details { get; }
}

public sealed record RuntimeRpcAppDiscoverResponse(
    RuntimeRpcCatalogSnapshot? Snapshot,
    RuntimeRpcErrorDescriptor? Error);

public sealed record RuntimeRpcAppProviderResponse(
    RuntimeRpcProviderDescriptor? Provider,
    RuntimeRpcErrorDescriptor? Error);

public sealed record RuntimeRpcAppInvariantViolationResponse(
    bool Accepted,
    RuntimeRpcErrorDescriptor? Error);

public sealed record RuntimeRpcAppInvokeResponse(
    JsonElement? Value,
    RuntimeRpcErrorDescriptor? Error);

public sealed record RuntimeRpcAppContentRegisterResponse(
    RuntimeRpcContentReferenceDescriptor? Content,
    RuntimeRpcErrorDescriptor? Error);

public static class RuntimeRpcAppStreamFrameTypes
{
    public const string Event = "event";
    public const string Completed = "completed";
    public const string Error = "error";
}

public sealed record RuntimeRpcAppCatalogFrame(
    string Type,
    RuntimeRpcCatalogEventDescriptor? Event,
    RuntimeRpcErrorDescriptor? Error);

public sealed record RuntimeRpcAppSubscriptionFrame(
    string Type,
    JsonElement? Event,
    RuntimeRpcErrorDescriptor? Error);
