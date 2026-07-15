using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    public Task<RuntimePackageSnapshot> GetPackageSnapshotAsync(CancellationToken token = default)
        => GetRequiredAsync<RuntimePackageSnapshot>(
            "packages/snapshot",
            RuntimeProtocolFeatures.AtomicPackageSnapshotV1,
            token);

    public Task<RuntimePackageStageStatus> GetPackageStageStatusAsync(
        string stageId,
        CancellationToken token = default)
        => GetRequiredAsync<RuntimePackageStageStatus>(
            $"packages/stages/{Uri.EscapeDataString(stageId)}",
            RuntimeProtocolFeatures.PackageStageStatusV1,
            token);
}
