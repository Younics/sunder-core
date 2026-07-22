namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimeTransportPolicyOptions
{
    public TimeSpan RegistryRequestTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public long MaxRegistryJsonBytes { get; init; } = 1024 * 1024;
    public long MaxRegistryErrorBytes { get; init; } = 64 * 1024;
    public TimeSpan ContentTransferLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan ContentTransferSweepInterval { get; init; } = TimeSpan.FromMinutes(1);
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
    public int MaxPackageCallbackQueryValues { get; init; } = 32;
    public int MaxPackageCallbackQueryKeyLength { get; init; } = 128;
    public int MaxPackageCallbackQueryValueLength { get; init; } = 4096;
    public int MaxPackageCallbackQueryCharacters { get; init; } = 16384;
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

internal sealed record RuntimePackageOperationPolicyOptions
{
    public int MaxRequestBytes { get; init; } = 1024 * 1024;
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxEventBytes { get; init; } = 1024 * 1024;
    public int MaxStreamRecordBytes { get; init; } = 1024 * 1024 + 16 * 1024;
    public int MaxStreamErrorMessageCharacters { get; init; } = 4096;
    public TimeSpan SessionDrainTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

internal sealed record RuntimeLifecyclePolicyOptions
{
    public TimeSpan PendingStageLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan StageSweepInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan TerminalStageRetention { get; init; } = TimeSpan.FromMinutes(15);
    public int MaximumTerminalStageStatuses { get; init; } = 256;
    public TimeSpan PackageBackgroundServiceStartupTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PackageBackgroundServiceCleanupTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ShutdownTimeout { get; init; } = RuntimeShutdownDeadline.DefaultTimeout;
    public TimeSpan DevPackageOwnerLeaseLifetime { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DevPackageOwnerSweepInterval { get; init; } = TimeSpan.FromSeconds(5);
}
