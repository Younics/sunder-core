namespace Sunder.Runtime.Contracts;

public sealed record SystemStatusResponse(
    string Name,
    string Version,
    bool IsReady,
    DateTimeOffset StartedAtUtc)
{
    public RuntimeBootstrapState State { get; init; } = IsReady
        ? RuntimeBootstrapState.Ready
        : RuntimeBootstrapState.Starting;

    public RuntimeStartupDiagnostic? StartupFailure { get; init; }
}

public sealed record RuntimeStartupDiagnostic(string Code, string Message);

public sealed record RuntimeResetChallengeResponse(string Challenge, DateTimeOffset ExpiresAtUtc);

public sealed record RuntimeResetConfirmRequest(string Challenge);

public sealed record RuntimeResetDrainResponse(IReadOnlyList<string> Categories);
