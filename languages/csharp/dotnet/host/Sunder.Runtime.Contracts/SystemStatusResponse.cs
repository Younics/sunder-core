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

public sealed record RuntimeResetChallengeResponse(string Challenge, DateTimeOffset ExpiresAtUtc)
{
    public bool SupportsBoundStateRoot { get; init; }

    public bool SupportsHostExecutedReset { get; init; }
}

public sealed record RuntimeResetConfirmRequest(string Challenge)
{
    public bool RequireBoundStateRoot { get; init; }

    public bool RequireHostExecutedReset { get; init; }

    public bool StartupCacheOnly { get; init; }
}

public sealed record RuntimeResetCategoryResult(
    string Category,
    string Status,
    IReadOnlyList<string>? RelativePaths = null);

public sealed record RuntimeResetDrainResponse(IReadOnlyList<string> Categories)
{
    public IReadOnlyList<RuntimeResetCategoryResult>? CategoryResults { get; init; }

    public bool HostExecutedReset { get; init; }

    public string? ReceiptRelativePath { get; init; }

    public string? RuntimeStateRootPath { get; init; }

    public string? RuntimeStateRootIdentity { get; init; }
}
