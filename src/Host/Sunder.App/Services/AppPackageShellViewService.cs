using System.Runtime.ExceptionServices;
using Avalonia.Threading;
using Sunder.App.ViewModels;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

public sealed class AppPackageShellViewService : IPackageShellViewService
{
    private static readonly TimeSpan SynchronousUiInvokeTimeout = TimeSpan.FromSeconds(5);
    private MainWindowViewModel? _viewModel;

    public void Attach(MainWindowViewModel viewModel)
        => _viewModel = viewModel;

    public void Detach(MainWindowViewModel viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
        {
            _viewModel = null;
        }
    }

    public IReadOnlyList<PackageHotbarView> ListHotbarViews()
        => Invoke(() => _viewModel?.ListHotbarViews() ?? [], Array.Empty<PackageHotbarView>());

    public bool IsViewInHotbar(string viewId)
        => Invoke(() => _viewModel?.IsViewInHotbar(viewId) == true, fallback: false);

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

    private static T Invoke<T>(Func<T> action, T fallback)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }

        using var completion = new ManualResetEventSlim();
        var result = fallback;
        Exception? exception = null;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                completion.Set();
            }
        });

        if (!completion.Wait(SynchronousUiInvokeTimeout))
        {
            return fallback;
        }

        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        return result;
    }

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

        if (Dispatcher.UIThread.CheckAccess())
        {
            return await action(viewModel);
        }

        return await Dispatcher.UIThread.InvokeAsync(async () => await action(viewModel));
    }
}
