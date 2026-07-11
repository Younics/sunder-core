namespace Sunder.Runtime.Contracts;

public sealed record SystemStatusResponse(
    string Name,
    string Version,
    bool IsReady,
    DateTimeOffset StartedAtUtc);

public sealed record RuntimeResetChallengeResponse(string Challenge, DateTimeOffset ExpiresAtUtc);

public sealed record RuntimeResetConfirmRequest(string Challenge);

public sealed record RuntimeResetDrainResponse(IReadOnlyList<string> Categories);
