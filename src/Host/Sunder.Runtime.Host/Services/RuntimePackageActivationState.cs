namespace Sunder.Runtime.Host.Services;

internal sealed record RuntimePackageActivationState(
    string PackageId,
    string Name,
    string Version,
    string? Icon);
