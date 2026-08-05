using Sunder.App.ViewModels;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed class AppPackageShellViewService(IUiDispatcher? uiDispatcher = null) : IPackageShellViewService
{
    private readonly IUiDispatcher _uiDispatcher = uiDispatcher ?? AvaloniaUiDispatcher.Instance;
    private MainWindowViewModel? _viewModel;
    private PackageHotbarView[] _hotbarViews = [];

    public void Attach(MainWindowViewModel viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            RefreshSnapshot(viewModel);
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.ShellViewStateChanged -= OnShellViewStateChanged;
        }

        _viewModel = viewModel;
        _viewModel.ShellViewStateChanged += OnShellViewStateChanged;
        RefreshSnapshot(viewModel);
    }

    public void Detach(MainWindowViewModel viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            _viewModel.ShellViewStateChanged -= OnShellViewStateChanged;
            _viewModel = null;
            Volatile.Write(ref _hotbarViews, []);
        }
    }

    public IReadOnlyList<PackageHotbarView> ListHotbarViews()
        => Volatile.Read(ref _hotbarViews);

    public bool IsViewInHotbar(string viewId)
        => Volatile.Read(ref _hotbarViews)
            .Any(view => string.Equals(view.ViewId, viewId, StringComparison.OrdinalIgnoreCase));

    public ValueTask<bool> AddViewToDefaultHotbarAsync(
        string viewId,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => InvokeNavigationAsync(
            parameters,
            (viewModel, snapshot) => viewModel.AddPackageViewToDefaultHotbarAsync(
                viewId,
                openPanel,
                snapshot),
            cancellationToken);

    internal ValueTask<bool> AddViewToDefaultHotbarAsync(
        string viewId,
        bool openPanel,
        IReadOnlyDictionary<string, string?>? parameters,
        AppPackageGenerationPublication publication,
        CancellationToken cancellationToken)
        => InvokeNavigationAsync(
            parameters,
            (viewModel, snapshot) => viewModel.AddPackageViewToDefaultHotbarAsync(
                viewId,
                openPanel,
                snapshot),
            cancellationToken,
            publication);

    public ValueTask<bool> AddViewToHotbarAsync(
        string viewId,
        PackageViewPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => InvokeNavigationAsync(
            parameters,
            (viewModel, snapshot) => viewModel.AddPackageViewToHotbarAsync(
                viewId,
                placement,
                index,
                openPanel,
                snapshot),
            cancellationToken);

    internal ValueTask<bool> AddViewToHotbarAsync(
        string viewId,
        PackageViewPlacement placement,
        int? index,
        bool openPanel,
        IReadOnlyDictionary<string, string?>? parameters,
        AppPackageGenerationPublication publication,
        CancellationToken cancellationToken)
        => InvokeNavigationAsync(
            parameters,
            (viewModel, snapshot) => viewModel.AddPackageViewToHotbarAsync(
                viewId,
                placement,
                index,
                openPanel,
                snapshot),
            cancellationToken,
            publication);

    public ValueTask<bool> RemoveViewFromHotbarAsync(
        string viewId,
        CancellationToken cancellationToken = default)
        => InvokeAsync(viewModel => ValueTask.FromResult(viewModel.RemovePackageViewFromHotbar(viewId)), cancellationToken);

    internal ValueTask<bool> RemoveViewFromHotbarAsync(
        string viewId,
        AppPackageGenerationPublication publication,
        CancellationToken cancellationToken)
        => InvokeAsync(
            viewModel => ValueTask.FromResult(viewModel.RemovePackageViewFromHotbar(viewId)),
            cancellationToken,
            publication);

    public ValueTask<bool> OpenViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => InvokeNavigationAsync(
            parameters,
            (viewModel, snapshot) => viewModel.OpenPackageViewPanelAsync(
                viewId,
                snapshot),
            cancellationToken);

    internal ValueTask<bool> OpenViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        AppPackageGenerationPublication publication,
        CancellationToken cancellationToken)
        => InvokeNavigationAsync(
            parameters,
            (viewModel, snapshot) => viewModel.OpenPackageViewPanelAsync(
                viewId,
                snapshot),
            cancellationToken,
            publication);

    public ValueTask<bool> CloseViewPanelAsync(
        string viewId,
        CancellationToken cancellationToken = default)
        => InvokeAsync(viewModel => ValueTask.FromResult(viewModel.ClosePackageViewPanel(viewId)), cancellationToken);

    internal ValueTask<bool> CloseViewPanelAsync(
        string viewId,
        AppPackageGenerationPublication publication,
        CancellationToken cancellationToken)
        => InvokeAsync(
            viewModel => ValueTask.FromResult(viewModel.ClosePackageViewPanel(viewId)),
            cancellationToken,
            publication);

    private ValueTask<bool> InvokeNavigationAsync(
        IReadOnlyDictionary<string, string?>? parameters,
        Func<MainWindowViewModel, IReadOnlyDictionary<string, string?>, ValueTask<bool>> action,
        CancellationToken cancellationToken,
        AppPackageGenerationPublication? publication = null)
    {
        var snapshot = PackageNavigationParameters.Snapshot(parameters);
        return InvokeAsync(viewModel => action(viewModel, snapshot), cancellationToken, publication);
    }

    private async ValueTask<bool> InvokeAsync(
        Func<MainWindowViewModel, ValueTask<bool>> action,
        CancellationToken cancellationToken,
        AppPackageGenerationPublication? publication = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _uiDispatcher.InvokeAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (publication is not null && !publication.IsPublished)
            {
                return false;
            }

            var viewModel = _viewModel;
            return viewModel is not null && await action(viewModel);
        });
    }

    private void OnShellViewStateChanged()
    {
        if (_viewModel is not null)
        {
            RefreshSnapshot(_viewModel);
        }
    }

    private void RefreshSnapshot(MainWindowViewModel viewModel)
        => Volatile.Write(ref _hotbarViews, viewModel.ListHotbarViews().ToArray());
}
