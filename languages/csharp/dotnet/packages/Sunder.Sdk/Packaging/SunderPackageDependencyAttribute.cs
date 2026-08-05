using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Packaging;

/// <summary>Declares a required installed runtime package and strict SemVer range.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
[SunderSdkCapability(SunderSdkCapabilities.PackagingV1)]
public sealed class SunderPackageDependencyAttribute : Attribute
{
    /// <summary>Gets or sets the lowercase dot-separated dependency package identifier.</summary>
    public string PackageId { get; set; } = string.Empty;

    /// <summary>Gets or sets the strict SemVer range accepted by the package.</summary>
    public string VersionRange { get; set; } = string.Empty;
}
