using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Runtime.CompilerServices;
using Sunder.App.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Views.Controls;

internal sealed class HostedPackageViewBoundary : Panel,
    IPackageViewNavigationTarget,
    IPackageViewNavigationPreparationTarget,
    IDisposable
{
    private static readonly ConditionalWeakTable<Control, VisualDetachmentFailure>
        FailedVisualDetachments = new();
    private const string ViewFallbackMessage =
        "This package view is unavailable for this app session. Other package views remain active.";
    private const string PackageFallbackMessage =
        "This package's App UI is unavailable for this app session. Installed and runtime state are unchanged.";

    private readonly string _packageId;
    private readonly string _viewId;
    private Control? _hostedView;
    private Func<string, string, HostedPackageViewBoundary, Exception, bool>? _reportFailure;
    private Action<HostedPackageViewBoundary>? _releasedCallback;
    private Control? _fallbackView;
    private bool _faulted;
    private bool _released;
    private bool _cleanupProven = true;

    public HostedPackageViewBoundary(
        string packageId,
        string viewId,
        Control hostedView,
        Action<string, string, Exception> reportFailure)
        : this(
            packageId,
            viewId,
            hostedView,
            (reportedPackageId, reportedViewId, _, exception) =>
            {
                reportFailure(reportedPackageId, reportedViewId, exception);
                return true;
            })
    {
    }

    internal HostedPackageViewBoundary(
        string packageId,
        string viewId,
        Control? hostedView,
        Func<string, string, HostedPackageViewBoundary, Exception, bool> reportFailure,
        Action<HostedPackageViewBoundary>? releasedCallback = null)
    {
        _packageId = packageId;
        _viewId = viewId;
        _hostedView = hostedView;
        _reportFailure = reportFailure;
        _releasedCallback = releasedCallback;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    }

    public Control? HostedView => _hostedView;

    internal string PackageId => _packageId;

    internal string ViewId => _viewId;
    internal Guid? ActivationOwnerId { get; init; }

    public bool IsFaulted => _faulted;

    public string? FaultMessage { get; private set; }

    internal AppPackageUiCleanupOutcome CleanupOutcome
        => _cleanupProven
            ? AppPackageUiCleanupOutcome.Proven
            : AppPackageUiCleanupOutcome.Unproven;

    internal static bool IsVisualCleanupProven(Control control)
        => !FailedVisualDetachments.TryGetValue(control, out _);

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
        var hostedView = _hostedView;
        if (_faulted || _released || hostedView is null)
        {
            return;
        }

        try
        {
            await AppPackageViewNavigator.NotifyViewNavigatedAsync(
                hostedView,
                context.ViewId,
                context.Parameters,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (!TryHandleFault(ex))
            {
                throw;
            }
        }
    }

    public async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        var hostedView = _hostedView;
        if (_faulted || _released || hostedView is null)
        {
            return false;
        }

        try
        {
            var status = await AppPackageViewNavigator.PrepareViewNavigationAsync(
                hostedView,
                context.ViewId,
                context.Parameters,
                cancellationToken);
            if (status == AppPackageViewNavigator.NavigationPreparationStatus.Unsupported)
            {
                await AppPackageViewNavigator.NotifyViewNavigatedAsync(
                    hostedView,
                    context.ViewId,
                    context.Parameters,
                    cancellationToken);
                return true;
            }

            return status == AppPackageViewNavigator.NavigationPreparationStatus.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (!TryHandleFault(ex))
            {
                throw;
            }
            return false;
        }
    }

    public async ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        var hostedView = _hostedView;
        if (_faulted || _released || hostedView is null)
        {
            return;
        }

        try
        {
            await AppPackageViewNavigator.NotifyViewNavigationPresentedAsync(
                hostedView,
                context.ViewId,
                context.Parameters,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (!TryHandleFault(ex))
            {
                throw;
            }
        }
    }

    internal AppPackageUiCleanupOutcome QuarantineView()
        => Quarantine(ViewFallbackMessage);

    internal AppPackageUiCleanupOutcome QuarantinePackage()
        => Quarantine(PackageFallbackMessage);

    public void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        var hostedView = _hostedView;
        _hostedView = null;
        _fallbackView = null;
        _reportFailure = null;
        _cleanupProven &= ClearChildren(hostedView, "release");
        var releasedCallback = Interlocked.Exchange(ref _releasedCallback, null);
        try
        {
            releasedCallback?.Invoke(this);
        }
        catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
        {
            AppSessionLog.WriteError("Failed to release a hosted package view boundary.", ex);
        }
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
            if (_faulted && AppPackageUiFaultPolicy.IsRecoverable(ex))
            {
                AppSessionLog.WriteError("Failed to attach package view fault fallback.", ex);
                return;
            }
            if (!TryHandleFault(ex))
            {
                throw;
            }
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
            if (_faulted && AppPackageUiFaultPolicy.IsRecoverable(ex))
            {
                AppSessionLog.WriteError("Failed to measure package view fault fallback.", ex);
                return default;
            }
            if (!TryHandleFault(ex))
            {
                throw;
            }
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
            if (_faulted && AppPackageUiFaultPolicy.IsRecoverable(ex))
            {
                AppSessionLog.WriteError("Failed to arrange package view fault fallback.", ex);
                return finalSize;
            }
            if (!TryHandleFault(ex))
            {
                throw;
            }
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
        if (content is null)
        {
            return null;
        }
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
            if (!AppPackageUiFaultPolicy.IsRecoverable(ex))
            {
                throw;
            }
            try
            {
                Children.Remove(content);
            }
            catch (Exception removalException) when (
                AppPackageUiFaultPolicy.IsRecoverable(removalException))
            {
                AppSessionLog.WriteError(
                    "Failed to detach package view content after an attachment fault.",
                    removalException);
            }
            if (_faulted)
            {
                AppSessionLog.WriteError("Failed to attach package view fault fallback.", ex);
                return null;
            }
            if (!TryHandleFault(ex))
            {
                throw;
            }
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
        catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
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
        catch (Exception ex) when (AppPackageUiFaultPolicy.IsRecoverable(ex))
        {
            AppSessionLog.WriteError("Failed to arrange package view fault fallback.", ex);
        }
    }

    private bool TryHandleFault(Exception exception)
    {
        if (_faulted)
        {
            return false;
        }
        if (!AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            return false;
        }

        var reportFailure = _reportFailure;
        if (reportFailure is null
            || !reportFailure(_packageId, _viewId, this, exception))
        {
            return false;
        }
        if (!_faulted)
        {
            QuarantineView();
        }
        return true;
    }

    private AppPackageUiCleanupOutcome Quarantine(string fallbackMessage)
    {
        if (_released)
        {
            return CleanupOutcome;
        }

        _faulted = true;
        FaultMessage = fallbackMessage;
        var hostedView = _hostedView;
        _hostedView = null;
        _reportFailure = null;
        _releasedCallback = null;
        _cleanupProven &= ClearChildren(hostedView, "quarantine");
        _fallbackView = null;
        try
        {
            Children.Add(GetFallbackView());
        }
        catch (Exception fallbackException) when (
            AppPackageUiFaultPolicy.IsRecoverable(fallbackException))
        {
            AppSessionLog.WriteError("Failed to show package view fault fallback.", fallbackException);
        }
        return CleanupOutcome;
    }

    private bool ClearChildren(Control? hostedView, string operation)
    {
        var cleanupProven = true;
        try
        {
            Children.Clear();
        }
        catch (Exception exception) when (
            AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            cleanupProven = false;
            AppSessionLog.WriteError(
                $"Failed to {operation} hosted package view children.",
                exception);
        }

        if (hostedView is null)
        {
            return cleanupProven;
        }

        bool containsHostedView;
        try
        {
            containsHostedView = Children.Contains(hostedView);
        }
        catch (Exception exception) when (
            AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            cleanupProven = false;
            containsHostedView = true;
            AppSessionLog.WriteError(
                $"Failed to verify package control detachment during boundary {operation}.",
                exception);
        }

        if (containsHostedView)
        {
            try
            {
                if (!Children.Remove(hostedView))
                {
                    cleanupProven = false;
                }
            }
            catch (Exception exception) when (
                AppPackageUiFaultPolicy.IsRecoverable(exception))
            {
                cleanupProven = false;
                AppSessionLog.WriteError(
                    $"Failed to detach a package control during boundary {operation}.",
                    exception);
            }
        }

        if (!cleanupProven)
        {
            FailedVisualDetachments.GetValue(
                hostedView,
                static _ => new VisualDetachmentFailure());
        }
        return cleanupProven;
    }

    private Control GetFallbackView()
        => _fallbackView ??= new Border
        {
            Padding = new Thickness(24),
            Child = new TextBlock
            {
                Text = FaultMessage ?? ViewFallbackMessage,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
            },
        };

    private sealed class VisualDetachmentFailure;
}
