namespace Sunder.Sdk.Worker.Internal;

internal sealed record WorkerLimits
{
    public static WorkerLimits Default { get; } = new();

    public int MaximumFrameBytes { get; init; } = 1024 * 1024;
    public int MaximumHeaderBytes { get; init; } = 8 * 1024;
    public int MaximumMessageDepth { get; init; } = 64;
    public int MaximumInboundCalls { get; init; } = 64;
    public int MaximumOutboundCalls { get; init; } = 32;
    public int MaximumLoggingCalls { get; init; } = 32;
    public int MaximumCallScopes { get; init; } = 64;
    public int MaximumContentHandles { get; init; } = 128;
    public int MaximumPayloadHandles { get; init; } = 16;
    public int MaximumControlCalls { get; init; } = 320;
    public int MaximumStreamQueueMessages { get; init; } = 32;
    public int MaximumWriteQueueMessages { get; init; } = 128;
    public int MaximumRememberedHostIds { get; init; } = 2048;
    public int MaximumDiagnosticCharacters { get; init; } = 4096;
    public long MaximumContentBytes { get; init; } = 64L * 1024 * 1024;
    public int MaximumPayloadBytes { get; init; } = 1024 * 1024;
    public int MaximumFilePayloadBytes { get; init; } = 16 * 1024 * 1024;
    public int MaximumContentUses { get; init; } = 32;

    public WorkerLimits Validate()
    {
        if (MaximumFrameBytes <= 0
            || MaximumHeaderBytes <= 0
            || MaximumMessageDepth <= 0
            || MaximumInboundCalls <= 0
            || MaximumOutboundCalls <= 0
            || MaximumLoggingCalls <= 0
            || MaximumCallScopes <= 0
            || MaximumContentHandles <= 0
            || MaximumPayloadHandles <= 0
            || MaximumControlCalls < MaximumCallScopes + MaximumContentHandles + MaximumPayloadHandles
            || MaximumStreamQueueMessages <= 0
            || MaximumWriteQueueMessages <= 0
            || MaximumRememberedHostIds <= 0
            || MaximumDiagnosticCharacters <= 0
            || MaximumContentBytes <= 0
            || MaximumPayloadBytes <= 0
            || MaximumFilePayloadBytes < MaximumPayloadBytes
            || MaximumContentUses <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WorkerLimits), "Worker protocol limits must be positive.");
        }

        return this;
    }
}
