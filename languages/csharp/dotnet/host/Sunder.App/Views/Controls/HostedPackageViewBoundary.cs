using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Sunder.App.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Views.Controls;

internal sealed class HostedPackageViewBoundary : Panel,
    IPackageViewNavigationTarget,
    IPackageViewNavigationPreparationTarget,
    IDisposable
{
    private readonly string _packageId;
    private readonly string _viewId;
    private readonly Control _hostedView;
    private readonly Action<string, string, Exception> _reportFailure;
    private Control? _fallbackView;
    private bool _faulted;
    private bool _released;

    public HostedPackageViewBoundary(
        string packageId,
        string viewId,
        Control hostedView,
        Action<string, string, Exception> reportFailure)
    {
        _packageId = packageId;
        _viewId = viewId;
        _hostedView = hostedView;
        _reportFailure = reportFailure;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public Control HostedView => _hostedView;

    internal string PackageId => _packageId;

    public bool IsFaulted => _faulted;

    public string? FaultMessage { get; private set; }

    public static void ReleaseHostedView(object? hostedView)
    {
        if (hostedView is HostedPackageViewBoundary boundary)
        {
            boundary.Release();
        }
    }

    public async ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        if (_faulted || _released)
        {
            return;
        }

        try
        {
            await AppPackageViewNavigator.NotifyViewNavigatedAsync(
                _hostedView,
                context.ViewId,
                context.Parameters,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            HandleFault(ex);
        }
    }

    public async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        if (_faulted || _released)
        {
            return false;
        }

        try
        {
            var status = await AppPackageViewNavigator.PrepareViewNavigationAsync(
                _hostedView,
                context.ViewId,
                context.Parameters,
                cancellationToken);
            if (status == AppPackageViewNavigator.NavigationPreparationStatus.Unsupported)
            {
                await AppPackageViewNavigator.NotifyViewNavigatedAsync(
                    _hostedView,
                    context.ViewId,
                    context.Parameters,
                    cancellationToken);
                return true;
            }

            return status == AppPackageViewNavigator.NavigationPreparationStatus.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            HandleFault(ex);
            return false;
        }
    }

    public async ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        if (_faulted || _released)
        {
            return;
        }

        try
        {
            await AppPackageViewNavigator.NotifyViewNavigationPresentedAsync(
                _hostedView,
                context.ViewId,
                context.Parameters,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            HandleFault(ex);
        }
    }

    public void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        Children.Clear();
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        try
        {
            base.OnAttachedToVisualTree(e);
            EnsureContentAttached();
        }
        catch (Exception ex)
        {
            HandleFault(ex);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var content = EnsureContentAttached();
        if (content is null)
        {
            return default;
        }

        try
        {
            content.Measure(availableSize);
            return content.DesiredSize;
        }
        catch (Exception ex)
        {
            HandleFault(ex);
            return MeasureFallback(availableSize);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var content = EnsureContentAttached();
        if (content is null)
        {
            return finalSize;
        }

        try
        {
            content.Arrange(new Rect(finalSize));
        }
        catch (Exception ex)
        {
            HandleFault(ex);
            ArrangeFallback(finalSize);
        }

        return finalSize;
    }

    private Control? EnsureContentAttached()
    {
        if (_released)
        {
            return null;
        }

        var content = _faulted ? GetFallbackView() : _hostedView;
        if (Children.Contains(content))
        {
            return content;
        }

        try
        {
            Children.Clear();
            Children.Add(content);
            return content;
        }
        catch (Exception ex)
        {
            Children.Remove(content);
            HandleFault(ex);
            return Children.Count > 0 ? Children[0] : null;
        }
    }

    private Size MeasureFallback(Size availableSize)
    {
        var fallback = EnsureContentAttached();
        if (fallback is null)
        {
            return default;
        }

        try
        {
            fallback.Measure(availableSize);
            return fallback.DesiredSize;
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to measure package view fault fallback.", ex);
            return default;
        }
    }

    private void ArrangeFallback(Size finalSize)
    {
        var fallback = EnsureContentAttached();
        try
        {
            fallback?.Arrange(new Rect(finalSize));
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Failed to arrange package view fault fallback.", ex);
        }
    }

    private void HandleFault(Exception exception)
    {
        if (_faulted)
        {
            return;
        }

        _faulted = true;
        FaultMessage = exception.Message;
        Children.Clear();
        try
        {
            Children.Add(GetFallbackView());
        }
        catch (Exception fallbackException)
        {
            AppSessionLog.WriteError("Failed to show package view fault fallback.", fallbackException);
        }

        try
        {
            _reportFailure(_packageId, _viewId, exception);
        }
        catch (Exception reportException)
        {
            AppSessionLog.WriteError($"Failed to report package view failure for '{_packageId}'.", reportException);
        }
    }

    private Control GetFallbackView()
        => _fallbackView ??= new Border
        {
            Padding = new Thickness(24),
            Child = new TextBlock
            {
                Text = "Package view failed and was disabled for this app session.",
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
            },
        };
}
