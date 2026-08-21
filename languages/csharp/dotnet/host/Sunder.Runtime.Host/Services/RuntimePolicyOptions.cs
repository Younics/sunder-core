using Sunder.Runtime.LocalState;
using Sunder.Sdk.Callbacks;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimeTransportPolicyOptions
{
    internal const long MaximumRpcContentBytesPerCall = 1024L * 1024 * 1024;
    internal const long MaximumRpcContentBytesPerPackage = 4L * 1024 * 1024 * 1024;
    internal const long MaximumRpcContentBytesPerRuntime = 8L * 1024 * 1024 * 1024;
    internal const int MaximumRpcContentReferencesPerCall = 256;
    internal const int MaximumRpcContentReferencesPerPackage = 1024;
    internal const int MaximumRpcContentReferencesPerRuntime = 2048;

    public TimeSpan RegistryRequestTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public long MaxRegistryJsonBytes { get; init; } = 1024 * 1024;
    public long MaxRegistryErrorBytes { get; init; } = 64 * 1024;
    public TimeSpan ContentTransferLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan ContentTransferSweepInterval { get; init; } = TimeSpan.FromMinutes(1);
    public long MaxRpcContentBytes { get; init; } = RuntimeResourcePolicies.Standard.MaximumContentReferenceBytes;
    public long MaxRpcContentBytesPerCall { get; init; } = 256L * 1024 * 1024;
    public long MaxRpcContentBytesPerPackage { get; init; } = 1024L * 1024 * 1024;
    public long MaxRpcContentBytesPerRuntime { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaxRpcContentReferencesPerCall { get; init; } = 64;
    public int MaxRpcContentReferencesPerPackage { get; init; } = 256;
    public int MaxRpcContentReferencesPerRuntime { get; init; } = 512;
    public int MaxRpcContentUses { get; init; } = 32;

    internal static RuntimeTransportPolicyOptions FromResourcePolicy(RuntimeResourcePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var contentBytes = (long)policy.MaximumContentReferenceBytes;
        return new RuntimeTransportPolicyOptions
        {
            MaxRpcContentBytes = contentBytes,
            MaxRpcContentBytesPerCall = Math.Min(MaximumRpcContentBytesPerCall, checked(contentBytes * 4)),
            MaxRpcContentBytesPerPackage = Math.Min(MaximumRpcContentBytesPerPackage, checked(contentBytes * 16)),
            MaxRpcContentBytesPerRuntime = Math.Min(MaximumRpcContentBytesPerRuntime, checked(contentBytes * 32)),
            MaxRpcContentReferencesPerCall = ScaleReferenceLimit(
                64,
                policy.MaximumContentReferenceBytes,
                MaximumRpcContentReferencesPerCall),
            MaxRpcContentReferencesPerPackage = ScaleReferenceLimit(
                256,
                policy.MaximumContentReferenceBytes,
                MaximumRpcContentReferencesPerPackage),
            MaxRpcContentReferencesPerRuntime = ScaleReferenceLimit(
                512,
                policy.MaximumContentReferenceBytes,
                MaximumRpcContentReferencesPerRuntime),
        };
    }

    private static int ScaleReferenceLimit(int standard, int contentBytes, int ceiling)
    {
        var scaled = ((long)standard * contentBytes
                      + RuntimeResourcePolicies.Standard.MaximumContentReferenceBytes - 1)
                     / RuntimeResourcePolicies.Standard.MaximumContentReferenceBytes;
        return (int)Math.Clamp(scaled, 1, ceiling);
    }
}

internal sealed record RuntimeAuthPolicyOptions
{
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan TerminalSessionRetention { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan SessionSweepInterval { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRegistrySessions { get; init; } = 16;
    public int MaxCallbackRequestLineBytes { get; init; } = 4096;
    public int MaxCallbackHeaderLineBytes { get; init; } = 8192;
    public int MaxCallbackHeaderCount { get; init; } = 64;
    public int MaxCallbackHeaderBytes { get; init; } = 32768;
    public int PackageCallbackPort { get; init; } = 1455;
    public int PackageCallbackFallbackPort { get; init; } = 1457;
    public int MaxConcurrentPackageCallbacks { get; init; } = 16;
    public int MaxPackageCallbackSessions { get; init; } = 32;
    public int MaxPackageCallbackQueryValues { get; init; } = PackageCallbackParameters.MaximumCount;
    public int MaxPackageCallbackQueryKeyLength { get; init; } = PackageCallbackParameters.MaximumKeyLength;
    public int MaxPackageCallbackQueryValueLength { get; init; } = PackageCallbackParameters.MaximumValueLength;
    public int MaxPackageCallbackQueryCharacters { get; init; } = PackageCallbackParameters.MaximumTotalLength;
    public TimeSpan PackageCallbackStartTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PackageCallbackCompletionTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan PackageCallbackCancellationTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PackageCallbackShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

internal sealed record RuntimeStackPolicyOptions
{
    public TimeSpan ImportPlanLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan ImportPlanSweepInterval { get; init; } = TimeSpan.FromMinutes(1);
    public long MaxExportPayloadFileBytes { get; init; } = 64L * 1024 * 1024;
    public long MaxExportPayloadTotalBytes { get; init; } = 256L * 1024 * 1024;
}

internal sealed record RuntimeRpcPolicyOptions
{
    public int MaxPayloadBytes { get; init; } = RuntimeResourcePolicies.Standard.MaximumRpcValueBytes;
    public int MaxPayloadDepth { get; init; } = RuntimeResourcePolicies.Standard.MaximumValueDepth;
    public int MaxPayloadNodes { get; init; } = RuntimeResourcePolicies.Standard.MaximumValueNodes;
    public int MaxObjectMembers { get; init; } = RuntimeResourcePolicies.Standard.MaximumObjectMembers;
    public int MaxInlineDecodedBytes { get; init; } = RuntimeResourcePolicies.Standard.MaximumInlineDecodedBytes;
    public int MaxConcurrentCallsPerCaller { get; init; } = 32;
    public int MaxConcurrentCallsPerCallee { get; init; } = 64;
    public int MaxNestedCallDepth { get; init; } = 8;
    public int MaxStreamEvents { get; init; } = 100_000;
    public int MaxStreamEventsPerSecond { get; init; } = 1000;
    public int MaxAppCallerSessions { get; init; } = 128;
    public int MaxCallScopesPerCaller { get; init; } = 64;
    public TimeSpan DefaultDeadline { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaximumExtendedDeadline { get; init; } = TimeSpan.FromMinutes(35);

    internal static RuntimeRpcPolicyOptions FromResourcePolicy(RuntimeResourcePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new RuntimeRpcPolicyOptions
        {
            MaxPayloadBytes = policy.MaximumRpcValueBytes,
            MaxPayloadDepth = policy.MaximumValueDepth,
            MaxPayloadNodes = policy.MaximumValueNodes,
            MaxObjectMembers = policy.MaximumObjectMembers,
            MaxInlineDecodedBytes = policy.MaximumInlineDecodedBytes,
        };
    }
}

internal sealed record RuntimeProcessPolicyOptions
{
    public int MaxHeaderBytes { get; init; } = 8 * 1024;
    public RuntimeResourcePolicy? EffectiveResourcePolicy { get; init; }
    public int MaxOutstandingHostCalls { get; init; } = 64;
    public int MaxOutstandingWorkerCalls { get; init; } = 32;
    public int MaxOutstandingWorkerLoggingCalls { get; init; } = 32;
    public int MaxOutstandingWorkerCallScopes { get; init; } = 64;
    public int MaxMaterializedWorkerContentHandles { get; init; } = 128;
    public int MaxWorkerPayloadHandles { get; init; } = 16;
    public int MaxWorkerPayloadBytes { get; init; } = 1024 * 1024;
    public int MaxWorkerFilePayloadBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxOutstandingWorkerControlCalls { get; init; } = 320;
    public int MaxWriteQueueMessages { get; init; } = 128;
    public int MaxRememberedMessageIds { get; init; } = 2048;
    public int MaxStderrBytes { get; init; } = 256 * 1024;
    public int MaxStderrLineCharacters { get; init; } = 4096;
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CancellationDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

internal sealed record RuntimeLifecyclePolicyOptions
{
    internal static readonly TimeSpan DefaultPackageRuntimeGenerationActivationTimeout = TimeSpan.FromMinutes(2);

    public TimeSpan PendingStageLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan StageSweepInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan TerminalStageRetention { get; init; } = TimeSpan.FromMinutes(15);
    public int MaximumTerminalStageStatuses { get; init; } = 256;
    public TimeSpan SessionDrainTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PackageWorkerStartupTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PackageRuntimeGenerationActivationTimeout { get; init; } = DefaultPackageRuntimeGenerationActivationTimeout;
    public int PackageRuntimeGenerationActivationAttempts { get; init; } = 2;
    public TimeSpan PackageWorkerCleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ShutdownTimeout { get; init; } = RuntimeShutdownDeadline.DefaultTimeout;
    public TimeSpan DevPackageOwnerLeaseLifetime { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DevPackageOwnerSweepInterval { get; init; } = TimeSpan.FromSeconds(5);
}

internal sealed record RuntimePackageRecoveryPolicyOptions
{
    private static readonly IReadOnlyList<TimeSpan> DefaultRestartDelays = Array.AsReadOnly(new[]
    {
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1000),
        TimeSpan.FromMilliseconds(2000),
        TimeSpan.FromMilliseconds(4000),
    });

    public bool Enabled { get; init; } = true;

    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(1);

    public IReadOnlyList<TimeSpan> RestartDelays { get; init; } = DefaultRestartDelays;

    public int SuspensionFailureCount { get; init; } = 6;

    public TimeSpan DependencyRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}
