using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimePackageActivationState(
    string PackageId,
    string Name,
    string Version,
    PackageHostRoles HostRoles,
    string? Icon,
    SunderPackageTargetKey? SelectedTargetKey = null,
    SunderPackageTargetManifest? SelectedTarget = null,
    IReadOnlyList<SunderPackageContractUseManifest?>? RpcContractUses = null);
