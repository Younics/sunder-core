namespace Sunder.Registry.Contracts;

public sealed record RegistryPublishLocalStackRequest(string StackPath);

public sealed record RegistryPublishStackResponse(
    bool Success,
    string? StackId,
    string? Message,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
    public bool NotFound { get; init; }
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

public sealed record RegistryAddStackMaintainerRequest(string UserId);

public sealed record RegistryStackMaintainerOperationResponse(
    bool Success,
    string? Message,
    IReadOnlyList<string> Errors)
{
    public bool Forbidden { get; init; }
    public bool NotFound { get; init; }
}
