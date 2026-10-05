namespace Sunder.Registry.Contracts;

public sealed record RegistryPublishLocalStackRequest(string StackPath, string? IdempotencyKey = null, long? ExpectedPublicationAuthorityRevision = null);

public sealed record RegistryPublishStackResponse(
    bool Success,
    string? StackId,
    string? Message,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
    public bool NotFound { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RegistryStackPolicySnapshot? Policy { get; init; }
}

public sealed record RegistryStackManagementOperationResponse(
    bool Success,
    string? Message,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
    public bool NotFound { get; init; }
}

public sealed record RegistryStackStarResponse(
    bool Success,
    string? Message,
    RegistryStackStats? Stats,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
    public bool NotFound { get; init; }
}

/// <summary>A Stack maintainer identified by an opaque Registry principal ID, never a provider subject or handle.</summary>
public sealed record RegistryStackMaintainer(
    string UserId,
    bool IsOwner,
    DateTimeOffset AddedAtUtc,
    string? Username = null,
    string? DisplayName = null,
    string? Email = null,
    string? AvatarUrl = null);

public sealed record RegistryStackMaintainersResponse(
    string StackId,
    IReadOnlyList<RegistryStackMaintainer> Maintainers);

/// <summary>Selects an existing Registry principal by its exact opaque ID.</summary>
public sealed record RegistryAddStackMaintainerRequest(string UserId);

public sealed record RegistryStackMaintainerOperationResponse(
    bool Success,
    string? Message,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
    public bool NotFound { get; init; }
}
