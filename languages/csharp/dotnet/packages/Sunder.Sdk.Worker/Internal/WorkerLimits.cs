namespace Sunder.Sdk.Worker.Internal;

internal sealed record WorkerLimits
{
    private const int Mebibyte = 1024 * 1024;

    public static WorkerLimits Default => new();

    public int MaximumRpcValueBytes { get; set; } = 32 * Mebibyte;
    public int MaximumValueDepth { get; set; } = 64;
    public int MaximumValueNodes { get; set; } = 524_288;
    public int MaximumObjectMembers { get; set; } = 4_096;
    public int MaximumFrameBytes { get; set; } = 32 * Mebibyte + WorkerProtocol.EnvelopeReserveBytes;
    public int MaximumHeaderBytes { get; init; } = 8 * 1024;
    public int MaximumMessageDepth { get; set; } = 72;
    public int MaximumInboundCalls { get; init; } = 64;
    public int MaximumInboundStreams { get; init; } = 64;
    public int MaximumOutboundCalls { get; init; } = 32;
    public int MaximumOutboundStreams { get; init; } = 32;
    public int MaximumLoggingCalls { get; init; } = 32;
    public int MaximumCallScopes { get; init; } = 64;
    public int MaximumContentHandles { get; init; } = 128;
    public int MaximumPayloadHandles { get; init; } = 16;
    public int MaximumControlCalls { get; init; } = 320;
    public int MaximumStreamQueueMessages { get; init; } = 32;
    public int MaximumWriteQueueMessages { get; init; } = 128;
    public long MaximumStreamQueueBytes { get; set; } = 64L * Mebibyte;
    public long MaximumWriteQueueBytes { get; set; } = 256L * Mebibyte;
    public int MaximumRememberedHostIds { get; init; } = 2048;
    public int MaximumDiagnosticCharacters { get; init; } = 4096;
    public long MaximumContentBytes { get; set; } = 256L * Mebibyte;
    public int MaximumPayloadBytes { get; init; } = 1024 * 1024;
    public int MaximumFilePayloadBytes { get; init; } = 16 * 1024 * 1024;
    public int MaximumContentUses { get; init; } = 32;
    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public WorkerLimits Validate()
    {
        if (MaximumFrameBytes <= 0
            || MaximumRpcValueBytes <= 0
            || MaximumValueDepth <= 0
            || MaximumValueNodes <= 0
            || MaximumObjectMembers <= 0
            || MaximumHeaderBytes <= 0
            || MaximumMessageDepth <= 0
            || MaximumInboundCalls <= 0
            || MaximumInboundStreams <= 0
            || MaximumOutboundCalls <= 0
            || MaximumOutboundStreams <= 0
            || MaximumLoggingCalls <= 0
            || MaximumCallScopes <= 0
            || MaximumContentHandles <= 0
            || MaximumPayloadHandles <= 0
            || MaximumControlCalls < MaximumCallScopes + MaximumContentHandles + MaximumPayloadHandles
            || MaximumStreamQueueMessages <= 0
            || MaximumWriteQueueMessages <= 0
            || MaximumStreamQueueBytes <= 0
            || MaximumWriteQueueBytes <= 0
            || MaximumRememberedHostIds <= 0
            || MaximumDiagnosticCharacters <= 0
            || MaximumContentBytes <= 0
            || MaximumPayloadBytes <= 0
            || MaximumFilePayloadBytes < MaximumPayloadBytes
            || MaximumContentUses <= 0
            || ShutdownDrainTimeout <= TimeSpan.Zero
            || ShutdownDrainTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(WorkerLimits), "Worker protocol limits must be positive.");
        }

        return this;
    }

    public void ApplyResourceProfile(WorkerResourceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        MaximumRpcValueBytes = profile.RpcValueBytes;
        MaximumValueDepth = profile.ValueDepth;
        MaximumValueNodes = profile.ValueNodes;
        MaximumObjectMembers = profile.ObjectMembers;
        MaximumFrameBytes = checked(profile.RpcValueBytes + WorkerProtocol.EnvelopeReserveBytes);
        MaximumMessageDepth = checked(profile.ValueDepth + 8);
        MaximumContentBytes = profile.ContentReferenceBytes;
        MaximumWriteQueueBytes = checked((long)profile.RpcValueBytes * 8);
        MaximumStreamQueueBytes = checked((long)profile.RpcValueBytes * 2);
        Validate();
    }
}
