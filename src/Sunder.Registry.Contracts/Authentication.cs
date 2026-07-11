namespace Sunder.Registry.Contracts;

public sealed record RegistryCliAuthorizeRequest(
    string RedirectUri,
    string State,
    string CodeChallenge,
    string? DisplayName = null);

public sealed record RegistryCliAuthorizeResponse(
    string RedirectUri,
    string Code,
    string State);

public sealed record RegistryCliTokenRequest(
    string Code,
    string CodeVerifier);

public sealed record RegistryCliTokenResponse(
    bool Success,
    string? Token,
    string? UserId,
    DateTimeOffset? ExpiresAtUtc,
    IReadOnlyList<string> Errors);
