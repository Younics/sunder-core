namespace Sunder.Registry.Contracts;

/// <summary>A Registry-issued identity lease. OwnerPrincipalId is an opaque issuer-qualified principal reference.</summary>
public sealed record RegistryIdentityReservation(
    string Kind,
    string ResourceId,
    Guid LeaseId,
    long Revision,
    string OwnerPrincipalId,
    string State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? RenewedAtUtc,
    DateTimeOffset? CancelledAtUtc);

/// <summary>Changes only the exact observed lease/revision, preventing stale requests from changing a replacement lease.</summary>
public sealed record RegistryChangeIdentityReservationRequest(Guid LeaseId, long ExpectedRevision);

public sealed record RegistryIdentityReservationOperationResponse(
    bool Success,
    RegistryIdentityReservation? Reservation,
    string? ErrorCode,
    IReadOnlyList<string> Errors);
