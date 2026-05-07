namespace Sunder.Protocol;

public sealed record PackageViewDescriptor(
    string ViewId,
    string PackageId,
    string Title,
    PackageIconDescriptor? Icon,
    string? DefaultPlacement,
    bool ShowInHotbarByDefault = true);
