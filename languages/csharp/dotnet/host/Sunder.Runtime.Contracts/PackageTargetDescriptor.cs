namespace Sunder.Runtime.Contracts;

public sealed record PackageTargetDescriptor(
    string Role,
    string Rid,
    string Kind,
    string EntryPoint,
    string? TargetFramework,
    string? SdkVersion,
    IReadOnlyList<string> RequiredHostCapabilities,
    IReadOnlyList<PackageWebViewDescriptor>? Views = null)
{
    public IReadOnlyList<string> RequiredHostCapabilities { get; }
        = RuntimeContractCollections.Freeze(RequiredHostCapabilities);

    public IReadOnlyList<PackageWebViewDescriptor> Views { get; }
        = RuntimeContractCollections.Freeze(Views ?? []);
}

public sealed record PackageWebViewDescriptor(
    string ViewId,
    string DisplayName,
    string Route,
    string? Icon,
    string DefaultPlacement,
    bool ShowInHotbar);
