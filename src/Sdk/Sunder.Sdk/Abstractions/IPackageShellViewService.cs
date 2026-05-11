using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public interface IPackageShellViewService
{
    IReadOnlyList<PackageHotbarView> ListHotbarViews();

    bool IsViewInHotbar(string viewId);

    ValueTask<bool> AddViewToDefaultHotbarAsync(
        string viewId,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);

    ValueTask<bool> AddViewToHotbarAsync(
        string viewId,
        PackageHotbarPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RemoveViewFromHotbarAsync(
        string viewId,
        CancellationToken cancellationToken = default);

    ValueTask<bool> OpenViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default);

    ValueTask<bool> CloseViewPanelAsync(
        string viewId,
        CancellationToken cancellationToken = default);
}

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

[SunderSdkCapability(SunderSdkCapabilities.ShellViewV1)]
public enum PackageHotbarPlacement
{
    LeftTop = 0,
    Middle = 1,
    RightTop = 2,
    LeftBottom = 3,
    RightBottom = 4,
}
