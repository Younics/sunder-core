using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Defines stable metadata and default shell behavior for an App package view.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ViewsV1)]
public sealed record PackageViewRegistration
{
    /// <summary>Creates view registration metadata.</summary>
    /// <param name="id">Stable package-scoped id used for persistence and navigation.</param>
    /// <param name="name">User-facing view title.</param>
    /// <param name="icon">Optional forward-slash package asset path or host glyph.</param>
    /// <param name="defaultPlacement">Initial hotbar region; defaults to the middle region.</param>
    /// <param name="showInHotbarByDefault">Whether first activation creates a hotbar entry; defaults to true.</param>
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

    /// <summary>Gets the stable package-scoped view id.</summary>
    public string Id { get; init; }

    /// <summary>Gets the user-facing view title.</summary>
    public string Name { get; init; }

    /// <summary>Gets the optional asset path or glyph; <see langword="null"/> requests host fallback.</summary>
    public string? Icon { get; init; }

    /// <summary>Gets the placement used before the user customizes the shell.</summary>
    public PackageViewPlacement DefaultPlacement { get; init; }

    /// <summary>Gets whether first activation creates a hotbar entry.</summary>
    public bool ShowInHotbarByDefault { get; init; }
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
