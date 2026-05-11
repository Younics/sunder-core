using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
public sealed record PackageViewRegistration
{
    public PackageViewRegistration(
        string id,
        string name,
        string? icon = null,
        PackageViewPlacement defaultPlacement = PackageViewPlacement.Middle,
        bool showInHotbarByDefault = true)
    {
        Id = id;
        Name = name;
        Icon = icon;
        DefaultPlacement = defaultPlacement;
        ShowInHotbarByDefault = showInHotbarByDefault;
    }

    public string Id { get; init; }

    public string Name { get; init; }

    public string? Icon { get; init; }

    public PackageViewPlacement DefaultPlacement { get; init; }

    public bool ShowInHotbarByDefault { get; init; }
}

[SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
public enum PackageViewPlacement
{
    LeftTop = 0,
    Middle = 1,
    RightTop = 2,
    LeftBottom = 3,
    RightBottom = 4,
}
