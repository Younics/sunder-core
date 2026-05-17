using Sunder.App.ViewModels;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed class AppPackageShellViewService : IPackageShellViewService
{
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
        => InvokeAsync(viewModel => viewModel.AddPackageViewToDefaultHotbarAsync(viewId, openPanel, parameters), cancellationToken);

    public ValueTask<bool> AddViewToHotbarAsync(
        string viewId,
        PackageHotbarPlacement placement,
        int? index = null,
        bool openPanel = false,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => InvokeAsync(viewModel => viewModel.AddPackageViewToHotbarAsync(viewId, placement, index, openPanel, parameters), cancellationToken);

    public ValueTask<bool> RemoveViewFromHotbarAsync(
        string viewId,
        CancellationToken cancellationToken = default)
        => InvokeAsync(viewModel => ValueTask.FromResult(viewModel.RemovePackageViewFromHotbar(viewId)), cancellationToken);

    public ValueTask<bool> OpenViewPanelAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters = null,
        CancellationToken cancellationToken = default)
        => InvokeAsync(viewModel => viewModel.OpenPackageViewPanelAsync(viewId, parameters), cancellationToken);

    public ValueTask<bool> CloseViewPanelAsync(
        string viewId,
        CancellationToken cancellationToken = default)
        => InvokeAsync(viewModel => ValueTask.FromResult(viewModel.ClosePackageViewPanel(viewId)), cancellationToken);

    private async ValueTask<bool> InvokeAsync(
        Func<MainWindowViewModel, ValueTask<bool>> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var viewModel = _viewModel;
        if (viewModel is null)
        {
            return false;
        }

        return await UiThread.InvokeAsync(async () => await action(viewModel));
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
