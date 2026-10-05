using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Packaging;

/// <summary>Declares a package UI view for generated App registration.</summary>
/// <remarks>The attributed type must be a concrete Avalonia control. The declaration never constructs a control during analysis or registration.</remarks>
/// <param name="viewId">The stable globally unique view identifier.</param>
/// <param name="displayName">The user-visible view name.</param>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
public sealed class SunderPackageViewAttribute(string viewId, string displayName) : Attribute
{
    /// <summary>Gets the stable, globally unique view identity.</summary>
    public string ViewId { get; } = viewId;

    /// <summary>Gets the display name shown in the shell.</summary>
    public string DisplayName { get; } = displayName;

    /// <summary>Gets or sets the optional view-model type constructed by App dependency injection.</summary>
    public Type? ViewModel { get; set; }

    /// <summary>Gets or sets the optional package-relative icon path.</summary>
    public string? IconPath { get; set; }

    /// <summary>Gets or sets the initial shell placement. Defaults to the middle pane.</summary>
    public PackageViewPlacement DefaultPlacement { get; set; } = PackageViewPlacement.Middle;
}

/// <summary>Declares the package settings control for generated App registration.</summary>
/// <remarks>A package has at most one settings view. Its view model and dependencies belong to the App role.</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
[SunderSdkCapability(SunderSdkCapabilities.SettingsViewsV1)]
public sealed class SunderPackageSettingsViewAttribute : Attribute
{
    /// <summary>Gets or sets the optional view-model type constructed by App dependency injection.</summary>
    public Type? ViewModel { get; set; }
}
