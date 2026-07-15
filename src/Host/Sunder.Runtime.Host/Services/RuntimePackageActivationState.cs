using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimePackageActivationState(
    string PackageId,
    string Name,
    string Version,
    PackageHostRoles HostRoles,
    string? Icon);
