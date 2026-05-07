namespace Sunder.Protocol;

public sealed record SystemStatusResponse(
    string Name,
    string Version,
    bool IsReady,
    DateTimeOffset StartedAtUtc);
