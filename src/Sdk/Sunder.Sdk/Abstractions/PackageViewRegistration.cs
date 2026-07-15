using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Defines stable metadata and default shell behavior for an App package view.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
public sealed record PackageViewRegistration
{
    /// <summary>Creates view registration metadata.</summary>
    /// <param name="id">Stable globally unique id used for persistence and navigation; conventionally prefixed with the package id.</param>
    /// <param name="name">User-facing view title.</param>
    /// <param name="iconAssetPath">Optional forward-slash package asset path.</param>
    /// <param name="defaultPlacement">Initial hotbar region; defaults to the middle region.</param>
    /// <param name="showInHotbarByDefault">Whether first activation creates a hotbar entry; defaults to true.</param>
    public PackageViewRegistration(
        string id,
        string name,
        string? iconAssetPath = null,
        PackageViewPlacement defaultPlacement = PackageViewPlacement.Middle,
        bool showInHotbarByDefault = true)
    {
        Id = id;
        Name = name;
        IconAssetPath = iconAssetPath;
        DefaultPlacement = defaultPlacement;
        ShowInHotbarByDefault = showInHotbarByDefault;
    }

    /// <summary>Gets the stable globally unique view id.</summary>
    public string Id { get; }

    /// <summary>Gets the user-facing view title.</summary>
    public string Name { get; }

    /// <summary>Gets the optional package asset path; <see langword="null"/> requests host fallback.</summary>
    public string? IconAssetPath { get; }

    /// <summary>Gets the placement used before the user customizes the shell.</summary>
    public PackageViewPlacement DefaultPlacement { get; }

    /// <summary>Gets whether first activation creates a hotbar entry.</summary>
    public bool ShowInHotbarByDefault { get; }
}

/// <summary>Specifies the default shell region for a package view.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
public enum PackageViewPlacement
{
    /// <summary>Upper section of the left hotbar.</summary>
    LeftTop = 0,
    /// <summary>Central primary hotbar section.</summary>
    Middle = 1,
    /// <summary>Upper section of the right hotbar.</summary>
    RightTop = 2,
    /// <summary>Lower section of the left hotbar.</summary>
    LeftBottom = 3,
    /// <summary>Lower section of the right hotbar.</summary>
    RightBottom = 4,
}
