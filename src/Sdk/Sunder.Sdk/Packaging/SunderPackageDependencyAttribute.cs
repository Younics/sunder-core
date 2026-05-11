using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Packaging;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
[SunderSdkCapability(SunderSdkCapabilities.PackagingV1)]
public sealed class SunderPackageDependencyAttribute : Attribute
{
    public string PackageId { get; set; } = string.Empty;

    public string VersionRange { get; set; } = string.Empty;
}
