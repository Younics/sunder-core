using Sunder.Sdk.Abstractions;

namespace Sunder.Runtime.Host.Services;

internal sealed class EmptyPackageShellViewService : IPackageShellViewService
{
    public static EmptyPackageShellViewService Instance { get; } = new();

    public IReadOnlyList<PackageHotbarView> ListHotbarViews() => [];

    public bool IsViewInHotbar(string viewId) => false;

    public ValueTask<bool> AddViewToDefaultHotbarAsync(
        string viewId,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);

    public ValueTask<bool> AddViewToHotbarAsync(
        string viewId,
        PackageHotbarPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);

    public ValueTask<bool> RemoveViewFromHotbarAsync(
        string viewId,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);

    public ValueTask<bool> OpenViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);

    public ValueTask<bool> CloseViewPanelAsync(
        string viewId,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(false);
}
