using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using Sunder.App.Views;
using Sunder.App.Views.Controls;

namespace Sunder.App.Services;

internal sealed class InitialShellRenderWaiter(
    IUiDispatcher uiDispatcher,
    TimeSpan? renderWaitTimeout = null
)
{
    private static readonly TimeSpan DefaultRenderWaitTimeout = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _renderWaitTimeout = renderWaitTimeout ?? DefaultRenderWaitTimeout;

    public async Task WaitForInitialViewsReadyAsync(
        MainWindow mainWindow,
        CancellationToken cancellationToken
    )
    {
        var readiness = await uiDispatcher
            .InvokeAsync(
                () =>
                    new ControlReadiness(() => GetReadyControls(mainWindow)),
                cancellationToken
            )
            .ConfigureAwait(false);
        try
        {
            await readiness.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await uiDispatcher.InvokeAsync(readiness.Dispose).ConfigureAwait(false);
        }

        var viewIds = await uiDispatcher
            .InvokeAsync(
                () =>
                    mainWindow
                        .ViewModel?.GetActiveHostedViewControls()
                        .Select(item => item.ViewId)
                        .ToArray()
                    ?? [],
                cancellationToken
            )
            .ConfigureAwait(false);
        AppSessionLog.WriteInfo(
            viewIds.Length == 0
                ? "Initial shell loaded and arranged with no hosted package views."
                : $"Initial hosted package views loaded and arranged: {string.Join(", ", viewIds)}."
        );
    }

    public async Task WaitForRenderedAsync(
        MainWindow mainWindow,
        CancellationToken cancellationToken
    )
    {
        var deadline = DateTimeOffset.UtcNow + _renderWaitTimeout;
        await WaitForAnimationFrameAsync(
            mainWindow,
            GetRemainingTimeout(deadline),
            cancellationToken).ConfigureAwait(false);
        await WaitForCompositionBatchAsync(
            mainWindow,
            GetRemainingTimeout(deadline),
            cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<Control> GetHostedControls(Control control)
    {
        yield return control;
        if (control is HostedPackageViewBoundary boundary)
        {
            if (boundary.HostedView is { } hostedView)
            {
                yield return hostedView;
            }
        }
    }

    private static IReadOnlyList<Control> GetReadyControls(MainWindow mainWindow)
        => mainWindow
            .ViewModel?.GetActiveHostedViewControls()
            .SelectMany(item => GetHostedControls(item.View))
            .Prepend(mainWindow)
            .Distinct()
            .ToArray()
            ?? [mainWindow];

    private async Task WaitForAnimationFrameAsync(
        MainWindow mainWindow,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await uiDispatcher
                .InvokeAsync(
                    () => mainWindow.RequestAnimationFrame(_ => frame.TrySetResult()),
                    cancellationToken
                )
                .ConfigureAwait(false);
            await frame.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            AppSessionLog.WriteInfo("Shell animation frame completed.");
        }
        catch (TimeoutException)
        {
            AppSessionLog.WriteInfo(
                "Shell animation frame was unavailable; continuing with the bounded render fallback."
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppSessionLog.WriteError(
                "Shell animation frame could not be requested; continuing with the render fallback.",
                ex
            );
        }
    }

    private async Task WaitForCompositionBatchAsync(
        MainWindow mainWindow,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var batch = await uiDispatcher
                .InvokeAsync(
                    () =>
                        ElementComposition
                            .GetElementVisual(mainWindow)
                            ?.Compositor.RequestCompositionBatchCommitAsync(),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (batch is null)
            {
                AppSessionLog.WriteInfo(
                    "Shell compositor was unavailable; continuing with the bounded render fallback."
                );
                return;
            }

            await batch
                .Rendered.WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            AppSessionLog.WriteInfo("Shell composition batch rendered.");
        }
        catch (TimeoutException)
        {
            AppSessionLog.WriteInfo(
                "Shell composition render timed out; continuing with the bounded render fallback."
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppSessionLog.WriteError(
                "Shell composition render was unavailable; continuing with the bounded render fallback.",
                ex
            );
        }
    }

    private static TimeSpan GetRemainingTimeout(DateTimeOffset deadline)
    {
        var remaining = deadline - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1);
    }

    internal sealed class ControlReadiness : IDisposable
    {
        private readonly Func<IReadOnlyList<Control>> _getControls;
        private readonly HashSet<Control> _controls = new(ReferenceEqualityComparer.Instance);
        private readonly EventHandler<RoutedEventArgs> _loadedHandler;
        private readonly EventHandler _layoutUpdatedHandler;
        private bool _disposed;

        public ControlReadiness(Func<IReadOnlyList<Control>> getControls)
        {
            _getControls = getControls;
            _loadedHandler = (_, _) => RefreshAndCheckReady();
            _layoutUpdatedHandler = (_, _) => RefreshAndCheckReady();

            RefreshAndCheckReady();
        }

        private TaskCompletionSource CompletionSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => CompletionSource.Task;

        internal bool IsTracking(Control control) => _controls.Contains(control);

        internal void Refresh() => RefreshAndCheckReady();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var control in _controls.ToArray())
            {
                control.Loaded -= _loadedHandler;
                control.LayoutUpdated -= _layoutUpdatedHandler;
            }
            _controls.Clear();
        }

        private void RefreshAndCheckReady()
        {
            if (_disposed || CompletionSource.Task.IsCompleted)
            {
                return;
            }

            var currentControls = new HashSet<Control>(
                _getControls(),
                ReferenceEqualityComparer.Instance);
            foreach (var control in _controls.Where(control => !currentControls.Contains(control)).ToArray())
            {
                control.Loaded -= _loadedHandler;
                control.LayoutUpdated -= _layoutUpdatedHandler;
                _controls.Remove(control);
            }
            foreach (var control in currentControls)
            {
                if (_controls.Add(control))
                {
                    control.Loaded += _loadedHandler;
                    control.LayoutUpdated += _layoutUpdatedHandler;
                }
            }

            if (
                currentControls.All(control =>
                    control.IsLoaded
                    && control.IsAttachedToVisualTree()
                    && control.IsMeasureValid
                    && control.IsArrangeValid
                    && control.Bounds.Width > 0
                    && control.Bounds.Height > 0
                )
            )
            {
                CompletionSource.TrySetResult();
            }
        }
    }
}
