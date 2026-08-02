using System.Text.Json;

namespace Sunder.Runtime.Contracts;

public enum RuntimeRpcPermissionState
{
    Pending,
    Granted,
    Denied,
}

public sealed record RuntimeRpcPermissionDescriptor(
    string CallerPackageId,
    string CallerPackageVersion,
    string ManifestSha256,
    string ContractId,
    string Action,
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

public sealed record RuntimeRpcPermissionUpdateRequest(
    string CallerPackageId,
    string ContractId,
    string Action);

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
    string ContractSha256,
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
    Guid AppGenerationId);

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

public sealed record RuntimeRpcErrorDescriptor(
    RuntimeRpcErrorKind Kind,
    string Code,
    string Message);

public sealed record RuntimeRpcAppDiscoverResponse(
    RuntimeRpcCatalogSnapshot? Snapshot,
    RuntimeRpcErrorDescriptor? Error);

public sealed record RuntimeRpcAppProviderResponse(
    RuntimeRpcProviderDescriptor? Provider,
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
