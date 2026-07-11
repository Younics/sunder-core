using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Controls user-customizable App shell hotbar placement and panel visibility.</summary>
/// <remarks>Methods marshal shell mutations to the UI thread. A false result means the view is unknown or no state changed.</remarks>
[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public interface IPackageShellViewService
{
    /// <summary>Returns an immutable ordered snapshot of current hotbar views.</summary>
    IReadOnlyList<PackageHotbarView> ListHotbarViews();

    /// <summary>Determines whether a registered view currently has a hotbar entry.</summary>
    bool IsViewInHotbar(string viewId);

    /// <summary>Adds a view at its registration default and optionally opens it.</summary>
    ValueTask<bool> AddViewToDefaultHotbarAsync(
        string viewId,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Adds or moves a view to a placement and optional zero-based index, optionally opening it with copied navigation parameters.</summary>
    ValueTask<bool> AddViewToHotbarAsync(
        string viewId,
        PackageHotbarPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a hotbar entry and closes its panel, preserving the underlying view registration.</summary>
    ValueTask<bool> RemoveViewFromHotbarAsync(
        string viewId,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a registered panel with optional copied navigation parameters.</summary>
    ValueTask<bool> OpenViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Closes an open panel without removing its hotbar entry.</summary>
    ValueTask<bool> CloseViewPanelAsync(
        string viewId,
        CancellationToken cancellationToken = default);
}

/// <summary>Provides an immutable snapshot of one shell hotbar entry.</summary>
/// <param name="ViewId">Stable package-scoped view id.</param>
/// <param name="PackageId">Owning package id.</param>
/// <param name="PackageDisplayName">User-facing owner name.</param>
/// <param name="Title">User-facing view title.</param>
/// <param name="Glyph">Host-resolved glyph or asset descriptor.</param>
/// <param name="Placement">Current shell region.</param>
/// <param name="Order">Zero-based order within the region.</param>
/// <param name="IsOpen">Whether the panel is visible.</param>
[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public sealed record PackageHotbarView(
    string ViewId,
    string PackageId,
    string PackageDisplayName,
    string Title,
    string Glyph,
    PackageHotbarPlacement Placement,
    int Order,
    bool IsOpen);

/// <summary>Specifies a shell hotbar region.</summary>
[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public enum PackageHotbarPlacement
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
