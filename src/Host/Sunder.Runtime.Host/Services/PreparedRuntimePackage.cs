using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed record PreparedRuntimePackage(
    string SourceFolder,
    RuntimePackageSource Source,
    string ShadowFolder,
    string LibraryFolder,
    string PackageId,
    string Version,
    PackageHostRoles HostRoles,
    RuntimePackageActivationState Activation,
    string? EntryAssemblyPath,
    SunderPackageTargetKey? SelectedTargetKey,
    SunderPackageTargetManifest? SelectedTarget,
    IReadOnlyList<PackageDependencyDescriptor> Dependencies,
    IReadOnlyDictionary<string, SunderRpcContractDescriptor>? RpcContracts = null,
    string? ManifestSha256 = null)
{
    public DevProcessLaunchMetadata? DevProcessLaunch { get; init; }

    public string SessionId { get; init; } = Guid.NewGuid().ToString("N");
}
