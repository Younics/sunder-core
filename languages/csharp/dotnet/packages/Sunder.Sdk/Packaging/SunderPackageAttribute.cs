using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Packaging;

/// <summary>Declares immutable runtime identity and display metadata for one package assembly.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
[SunderSdkCapability(SunderSdkCapabilities.PackagingV1)]
public sealed class SunderPackageAttribute : Attribute
{
    /// <summary>Gets or sets the lowercase dot-separated package identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the user-facing package name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional one-sentence package summary.</summary>
    public string? Summary { get; set; }

    /// <summary>Gets or sets an optional forward-slash relative asset path within the package.</summary>
    public string? Icon { get; set; }
}
