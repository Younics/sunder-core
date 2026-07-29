using Avalonia.Controls;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed class AppPackageHostedViewFacade(
    AppPackageViewRegistry viewRegistry,
    Func<string, bool> isPackageDisabled,
    Action<string, string, Exception> reportHostedViewFailure)
{
    private readonly object _operationSyncRoot = new();
    private readonly Dictionary<string, CancellationTokenSource> _navigationCancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _viewOperationGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _viewEpochs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource> _viewResetBarriers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ActiveViewOperation> _activeOperations = [];
    private readonly HashSet<string> _warmedViewIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _presentedViewIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _operationCancellation = new();
    private readonly AsyncLocal<ViewOperationScope?> _operationScope = new();
    private bool _operationsStopping;

    public Control? GetOrCreateView(string viewId)
        => viewRegistry.GetOrCreateView(viewId, isPackageDisabled, reportHostedViewFailure);

    public bool IsViewPrepared(string viewId)
    {
        lock (_operationSyncRoot)
        {
            return _warmedViewIds.Contains(viewId) || _presentedViewIds.Contains(viewId);
        }
    }

    public bool SupportsNavigationPreparation(string viewId)
        => GetOrCreateView(viewId) is { } view
           && AppPackageViewNavigator.SupportsNavigationPreparation(view);

    public async ValueTask<Control?> WarmupViewAsync(
        string viewId,
        CancellationToken cancellationToken,
        Func<Control, bool>? retainView = null)
    {
        return await RunViewOperationAsync(
            viewId,
            ViewOperationKind.Warmup,
            cancellationToken,
            staleResult: static () => null,
            async (_, operationCancellation) =>
            {
                operationCancellation.ThrowIfCancellationRequested();
                lock (_operationSyncRoot)
                {
                    if (_warmedViewIds.Contains(viewId) || _presentedViewIds.Contains(viewId))
                    {
                        operationCancellation.ThrowIfCancellationRequested();
                        var retainedView = GetOrCreateView(viewId);
                        operationCancellation.ThrowIfCancellationRequested();
                        return retainedView is not null
                            && (retainView is null || retainView(retainedView))
                                ? retainedView
                                : null;
                    }
                }

                operationCancellation.ThrowIfCancellationRequested();
                var view = GetOrCreateView(viewId);
                if (view is null)
                {
                    return null;
                }

                try
                {
                    await AppPackageViewNavigator.WarmupViewAsync(view, operationCancellation);
                    operationCancellation.ThrowIfCancellationRequested();
                    lock (_operationSyncRoot)
                    {
                        _warmedViewIds.Add(viewId);
                        operationCancellation.ThrowIfCancellationRequested();
                        if (retainView is not null && !retainView(view))
                        {
                            return null;
                        }
                    }
                    return view;
                }
                catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ReportViewFailure(viewId, $"warmup failed: {ex.Message}", "warmup failed", ex);
                    return null;
                }
            });
    }

    public async ValueTask<bool> PrepareViewAsync(
        string viewId,
        Func<Control?, bool> presentView,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentView);
        return await RunViewOperationAsync(
            viewId,
            ViewOperationKind.Presentation,
            cancellationToken,
            staleResult: static () => false,
            async (_, operationCancellation) =>
            {
                operationCancellation.ThrowIfCancellationRequested();
                bool requiresWarmup;
                lock (_operationSyncRoot)
                {
                    requiresWarmup = !_warmedViewIds.Contains(viewId)
                        && !_presentedViewIds.Contains(viewId);
                }

                var view = GetOrCreateView(viewId);
                if (view is not null && requiresWarmup)
                {
                    try
                    {
                        await AppPackageViewNavigator.WarmupViewAsync(
                            view,
                            operationCancellation);
                        operationCancellation.ThrowIfCancellationRequested();
                        lock (_operationSyncRoot)
                        {
                            _warmedViewIds.Add(viewId);
                        }
                    }
                    catch (OperationCanceledException) when (
                        operationCancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ReportViewFailure(
                            viewId,
                            $"warmup failed: {ex.Message}",
                            "warmup failed",
                            ex);
                        return false;
                    }
                }

                operationCancellation.ThrowIfCancellationRequested();
                var presented = presentView(view);
                if (presented && view is not null)
                {
                    lock (_operationSyncRoot)
                    {
                        _presentedViewIds.Add(viewId);
                    }
                }
                return presented;
            });
    }

    public async ValueTask<Control?> ReloadViewAsync(
        string viewId,
        CancellationToken cancellationToken)
        => await ResetViewAsync(
            viewId,
            cancellationToken,
            operationCancellation =>
            {
                operationCancellation.ThrowIfCancellationRequested();
                viewRegistry.RemoveCachedView(viewId);
                lock (_operationSyncRoot)
                {
                    _warmedViewIds.Remove(viewId);
                    _presentedViewIds.Remove(viewId);
                }
                return Task.FromResult(GetOrCreateView(viewId));
            });

    public async ValueTask<bool> InvalidateViewAsync(
        string viewId,
        CancellationToken cancellationToken)
        => await ResetViewAsync(
            viewId,
            cancellationToken,
            operationCancellation =>
            {
                operationCancellation.ThrowIfCancellationRequested();
                var invalidated = viewRegistry.RemoveCachedView(viewId);
                lock (_operationSyncRoot)
                {
                    _warmedViewIds.Remove(viewId);
                    _presentedViewIds.Remove(viewId);
                }
                return Task.FromResult(invalidated);
            });

    public async ValueTask<bool> NotifyViewNavigatedAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        CancellationToken cancellationToken,
        Func<bool>? canStart = null)
    {
        var currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previousCancellation;
        lock (_operationSyncRoot)
        {
            _navigationCancellations.Remove(viewId, out previousCancellation);
            _navigationCancellations[viewId] = currentCancellation;
        }

        TryCancel(previousCancellation);
        try
        {
            return await RunViewOperationAsync(
                viewId,
                ViewOperationKind.Navigation,
                currentCancellation.Token,
                staleResult: static () => false,
                async (_, operationCancellation) =>
                {
                    if (canStart is not null && !canStart())
                    {
                        return false;
                    }

                    var view = GetOrCreateView(viewId);
                    if (view is null)
                    {
                        return true;
                    }

                    try
                    {
                        await AppPackageViewNavigator.NotifyViewNavigatedAsync(
                            view,
                            viewId,
                            parameters,
                            operationCancellation);
                        operationCancellation.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        ReportViewFailure(
                            viewId,
                            $"navigation failed: {ex.Message}",
                            "navigation failed",
                            ex);
                    }

                    return true;
                },
                navigationCancellation: currentCancellation);
        }
        finally
        {
            lock (_operationSyncRoot)
            {
                if (_navigationCancellations.TryGetValue(viewId, out var activeCancellation)
                    && ReferenceEquals(activeCancellation, currentCancellation))
                {
                    _navigationCancellations.Remove(viewId);
                }
            }

            currentCancellation.Dispose();
        }
    }

    public async ValueTask<bool> PrepareNavigationForPresentationAsync(
        string viewId,
        IReadOnlyDictionary<string, string?>? parameters,
        Func<Control, bool> stageView,
        Func<Control, bool> presentView,
        Action<Control> unstageView,
        CancellationToken cancellationToken,
        Func<bool>? canStart = null)
    {
        ArgumentNullException.ThrowIfNull(stageView);
        ArgumentNullException.ThrowIfNull(presentView);
        ArgumentNullException.ThrowIfNull(unstageView);

        var currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previousCancellation;
        lock (_operationSyncRoot)
        {
            _navigationCancellations.Remove(viewId, out previousCancellation);
            _navigationCancellations[viewId] = currentCancellation;
        }

        TryCancel(previousCancellation);
        try
        {
            return await RunViewOperationAsync(
                viewId,
                ViewOperationKind.Navigation,
                currentCancellation.Token,
                staleResult: static () => false,
                async (_, operationCancellation) =>
                {
                    if (canStart is not null && !canStart())
                    {
                        return false;
                    }

                    var view = GetOrCreateView(viewId);
                    if (view is null
                        || !AppPackageViewNavigator.SupportsNavigationPreparation(view)
                        || !stageView(view))
                    {
                        return false;
                    }

                    var presented = false;
                    try
                    {
                        AppPackageViewNavigator.NavigationPreparationStatus status;
                        try
                        {
                            status = await AppPackageViewNavigator.PrepareViewNavigationAsync(
                                view,
                                viewId,
                                parameters,
                                operationCancellation);
                        }
                        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            ReportViewFailure(
                                viewId,
                                $"navigation preparation failed: {ex.Message}",
                                "navigation preparation failed",
                                ex);
                            return false;
                        }

                        operationCancellation.ThrowIfCancellationRequested();
                        if (status != AppPackageViewNavigator.NavigationPreparationStatus.Ready
                            || canStart is not null && !canStart())
                        {
                            return false;
                        }

                        presented = presentView(view);
                        if (!presented)
                        {
                            return false;
                        }

                        lock (_operationSyncRoot)
                        {
                            _warmedViewIds.Add(viewId);
                            _presentedViewIds.Add(viewId);
                        }

                        try
                        {
                            await AppPackageViewNavigator.NotifyViewNavigationPresentedAsync(
                                view,
                                viewId,
                                parameters,
                                operationCancellation);
                        }
                        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            ReportViewFailure(
                                viewId,
                                $"presentation acknowledgment failed: {ex.Message}",
                                "presentation acknowledgment failed",
                                ex);
                        }

                        return true;
                    }
                    finally
                    {
                        if (!presented)
                        {
                            unstageView(view);
                        }
                    }
                },
                navigationCancellation: currentCancellation);
        }
        finally
        {
            lock (_operationSyncRoot)
            {
                if (_navigationCancellations.TryGetValue(viewId, out var activeCancellation)
                    && ReferenceEquals(activeCancellation, currentCancellation))
                {
                    _navigationCancellations.Remove(viewId);
                }
            }

            currentCancellation.Dispose();
        }
    }

    public void CancelViewNavigation(string viewId)
    {
        CancellationTokenSource? cancellation;
        lock (_operationSyncRoot)
        {
            _navigationCancellations.Remove(viewId, out cancellation);
        }

        TryCancel(cancellation);
    }

    public async Task CancelViewNavigationAsync(string viewId)
    {
        CancellationTokenSource? cancellation;
        lock (_operationSyncRoot)
        {
            var currentNavigation = _operationScope.Value?.Find(
                viewId,
                ViewOperationKind.Navigation,
                _activeOperations);
            if (_navigationCancellations.TryGetValue(viewId, out var registeredCancellation)
                && ReferenceEquals(
                    registeredCancellation,
                    currentNavigation?.NavigationCancellation))
            {
                cancellation = null;
            }
            else
            {
                _navigationCancellations.Remove(viewId, out cancellation);
            }
        }

        await TryCancelAsync(cancellation).ConfigureAwait(false);
        await WaitForOperationsAsync(
            viewId,
            ViewOperationKind.Navigation,
            _operationScope.Value).ConfigureAwait(false);
    }

    public void CancelAllViewNavigations()
    {
        CancellationTokenSource[] cancellations;
        lock (_operationSyncRoot)
        {
            cancellations = _navigationCancellations.Values.ToArray();
            _navigationCancellations.Clear();
        }

        foreach (var cancellation in cancellations)
        {
            TryCancel(cancellation);
        }
    }

    public async Task CancelAllViewNavigationsAsync()
    {
        CancellationTokenSource[] cancellations;
        lock (_operationSyncRoot)
        {
            cancellations = _navigationCancellations.Values.ToArray();
            _navigationCancellations.Clear();
        }

        await Task.WhenAll(cancellations.Select(TryCancelAsync)).ConfigureAwait(false);
        await WaitForOperationsAsync(
            viewId: null,
            ViewOperationKind.Navigation,
            _operationScope.Value).ConfigureAwait(false);
    }

    public async Task CancelAllViewOperationsAsync()
        => await BeginCancelAllViewOperations().ConfigureAwait(false);

    internal Task BeginCancelAllViewOperations()
    {
        CancellationTokenSource[] navigationCancellations;
        ActiveViewOperation[] operations;
        lock (_operationSyncRoot)
        {
            _operationsStopping = true;
            navigationCancellations = _navigationCancellations.Values.ToArray();
            _navigationCancellations.Clear();
            operations = _activeOperations
                .Where(operation => _operationScope.Value?.Contains(operation) != true)
                .ToArray();
        }

        var cancellationSignals = navigationCancellations
            .Append(_operationCancellation)
            .Concat(operations.Select(static operation => operation.Cancellation))
            .Select(static cancellation => TryCancelAsync(cancellation))
            .ToArray();
        return DrainAllViewOperationsAsync(cancellationSignals, operations);
    }

    public async Task CancelPackageViewOperationsAsync(string packageId)
    {
        ActiveViewOperation[] operations;
        CancellationTokenSource[] navigationCancellations;
        lock (_operationSyncRoot)
        {
            operations = _activeOperations
                .Where(operation => string.Equals(
                    viewRegistry.GetPackageId(operation.ViewId),
                    packageId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var viewIds = operations
                .Select(operation => operation.ViewId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            navigationCancellations = viewIds
                .Select(viewId => _navigationCancellations.Remove(viewId, out var cancellation)
                    ? cancellation
                    : null)
                .OfType<CancellationTokenSource>()
                .ToArray();
        }

        await Task.WhenAll(navigationCancellations.Select(TryCancelAsync)).ConfigureAwait(false);
        await Task.WhenAll(operations.Select(operation => TryCancelAsync(operation.Cancellation)))
            .ConfigureAwait(false);
        await Task.WhenAll(operations.Select(operation => operation.Completion.Task))
            .ConfigureAwait(false);
    }

    private async Task<T> ResetViewAsync<T>(
        string viewId,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> reset)
    {
        lock (_operationSyncRoot)
        {
            if (_operationScope.Value?.Find(viewId, kind: null, _activeOperations) is not null)
            {
                throw new InvalidOperationException(
                    $"Package view '{viewId}' cannot be reset from its own lifecycle callback.");
            }
        }

        ViewReset viewReset;
        lock (_operationSyncRoot)
        {
            var epoch = _viewEpochs.GetValueOrDefault(viewId) + 1;
            _viewEpochs[viewId] = epoch;
            var previousBarrier = _viewResetBarriers.TryGetValue(viewId, out var activeBarrier)
                ? activeBarrier.Task
                : Task.CompletedTask;
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _viewResetBarriers[viewId] = barrier;
            _navigationCancellations.Remove(viewId, out var navigationCancellation);
            viewReset = new ViewReset(
                epoch,
                previousBarrier,
                barrier,
                navigationCancellation,
                _activeOperations
                    .Where(operation => string.Equals(
                        operation.ViewId,
                        viewId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray());
        }

        try
        {
            await viewReset.PreviousBarrier.WaitAsync(cancellationToken).ConfigureAwait(false);
            await TryCancelAsync(viewReset.NavigationCancellation).ConfigureAwait(false);
            await Task.WhenAll(viewReset.Operations.Select(operation => TryCancelAsync(operation.Cancellation)))
                .ConfigureAwait(false);
            await Task.WhenAll(viewReset.Operations.Select(operation => operation.Completion.Task))
                .ConfigureAwait(false);
            return await RunViewOperationAsync(
                viewId,
                ViewOperationKind.Reset,
                cancellationToken,
                staleResult: static () => throw new InvalidOperationException("A reset operation cannot become stale."),
                (_, operationCancellation) => reset(operationCancellation),
                waitForResetBarrier: false,
                enforceEpoch: false).ConfigureAwait(false);
        }
        finally
        {
            await viewReset.PreviousBarrier.ConfigureAwait(false);
            lock (_operationSyncRoot)
            {
                if (_viewResetBarriers.TryGetValue(viewId, out var activeBarrier)
                    && ReferenceEquals(activeBarrier, viewReset.Barrier))
                {
                    _viewResetBarriers.Remove(viewId);
                }
            }
            viewReset.Barrier.TrySetResult();
        }
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Package view navigation cancellation callback failed.", ex);
        }
    }

    private static async Task TryCancelAsync(CancellationTokenSource? cancellation)
    {
        try
        {
            if (cancellation is not null)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppSessionLog.WriteError("Package view navigation cancellation callback failed.", ex);
        }
    }

    private static async Task DrainAllViewOperationsAsync(
        IReadOnlyList<Task> cancellationSignals,
        IReadOnlyList<ActiveViewOperation> operations)
    {
        await Task.WhenAll(cancellationSignals).ConfigureAwait(false);
        await Task.WhenAll(operations.Select(static operation => operation.Completion.Task))
            .ConfigureAwait(false);
    }

    private Task<T> RunViewOperationAsync<T>(
        string viewId,
        ViewOperationKind kind,
        CancellationToken cancellationToken,
        Func<T> staleResult,
        Func<long, CancellationToken, Task<T>> operation,
        bool waitForResetBarrier = true,
        bool enforceEpoch = true,
        CancellationTokenSource? navigationCancellation = null)
    {
        CancellationTokenSource linkedCancellation;
        SemaphoreSlim operationGate;
        ActiveViewOperation activeOperation;
        Task resetBarrier;
        lock (_operationSyncRoot)
        {
            if (_operationsStopping)
            {
                return Task.FromCanceled<T>(new CancellationToken(canceled: true));
            }

            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _operationCancellation.Token);
            if (!_viewOperationGates.TryGetValue(viewId, out operationGate!))
            {
                operationGate = new SemaphoreSlim(1, 1);
                _viewOperationGates.Add(viewId, operationGate);
            }

            var epoch = _viewEpochs.GetValueOrDefault(viewId);
            resetBarrier = waitForResetBarrier
                && _viewResetBarriers.TryGetValue(viewId, out var activeResetBarrier)
                    ? activeResetBarrier.Task
                    : Task.CompletedTask;
            activeOperation = new ActiveViewOperation(
                viewId,
                kind,
                epoch,
                linkedCancellation,
                navigationCancellation);
            _activeOperations.Add(activeOperation);
        }

        return RunCoreAsync();

        async Task<T> RunCoreAsync()
        {
            var previousScope = _operationScope.Value;
            bool isReentrant;
            lock (_operationSyncRoot)
            {
                isReentrant = previousScope?.Find(viewId, kind: null, _activeOperations) is not null;
            }
            try
            {
                if (!isReentrant)
                {
                    await resetBarrier.WaitAsync(linkedCancellation.Token);
                    await operationGate.WaitAsync(linkedCancellation.Token);
                }
                try
                {
                    _operationScope.Value = new ViewOperationScope(activeOperation, previousScope);
                    if (enforceEpoch)
                    {
                        lock (_operationSyncRoot)
                        {
                            if (_viewEpochs.GetValueOrDefault(viewId) != activeOperation.Epoch)
                            {
                                return staleResult();
                            }
                        }
                    }
                    return await operation(activeOperation.Epoch, linkedCancellation.Token);
                }
                finally
                {
                    _operationScope.Value = previousScope;
                    if (!isReentrant)
                    {
                        operationGate.Release();
                    }
                }
            }
            finally
            {
                lock (_operationSyncRoot)
                {
                    _activeOperations.Remove(activeOperation);
                }
                linkedCancellation.Dispose();
                activeOperation.Completion.TrySetResult();
            }
        }
    }

    private async Task WaitForOperationsAsync(
        string? viewId,
        ViewOperationKind? kind,
        ViewOperationScope? excludedScope)
    {
        Task[] operations;
        lock (_operationSyncRoot)
        {
            operations = _activeOperations
                .Where(item =>
                    (viewId is null || string.Equals(item.ViewId, viewId, StringComparison.OrdinalIgnoreCase))
                    && (kind is null || item.Kind == kind)
                    && excludedScope?.Contains(item) != true)
                .Select(item => item.Completion.Task)
                .ToArray();
        }

        try
        {
            await Task.WhenAll(operations).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the expected way package view operations are drained.
        }
    }

    private void ReportViewFailure(
        string viewId,
        string packageMessage,
        string logMessage,
        Exception exception)
    {
        var packageId = viewRegistry.GetPackageId(viewId);
        if (packageId is not null)
        {
            reportHostedViewFailure(
                packageId,
                $"Package view '{viewId}' {packageMessage}",
                exception);
            return;
        }

        AppSessionLog.WriteError($"Package view '{viewId}' {logMessage}.", exception);
    }

    private enum ViewOperationKind
    {
        Warmup,
        Presentation,
        Navigation,
        Reset,
    }

    private sealed class ActiveViewOperation(
        string viewId,
        ViewOperationKind kind,
        long epoch,
        CancellationTokenSource cancellation,
        CancellationTokenSource? navigationCancellation)
    {
        public string ViewId { get; } = viewId;

        public ViewOperationKind Kind { get; } = kind;

        public long Epoch { get; } = epoch;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public CancellationTokenSource? NavigationCancellation { get; } = navigationCancellation;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ViewReset(
        long Epoch,
        Task PreviousBarrier,
        TaskCompletionSource Barrier,
        CancellationTokenSource? NavigationCancellation,
        IReadOnlyList<ActiveViewOperation> Operations);

    private sealed record ViewOperationScope(ActiveViewOperation Operation, ViewOperationScope? Parent)
    {
        public bool Contains(string viewId)
            => string.Equals(Operation.ViewId, viewId, StringComparison.OrdinalIgnoreCase)
                || Parent?.Contains(viewId) == true;

        public bool Contains(string viewId, ViewOperationKind kind)
            => Operation.Kind == kind
                && string.Equals(Operation.ViewId, viewId, StringComparison.OrdinalIgnoreCase)
                || Parent?.Contains(viewId, kind) == true;

        public bool Contains(ActiveViewOperation operation)
            => ReferenceEquals(Operation, operation) || Parent?.Contains(operation) == true;

        public ActiveViewOperation? Find(
            string viewId,
            ViewOperationKind? kind,
            IReadOnlySet<ActiveViewOperation> activeOperations)
            => activeOperations.Contains(Operation)
                && string.Equals(Operation.ViewId, viewId, StringComparison.OrdinalIgnoreCase)
                && (kind is null || Operation.Kind == kind)
                    ? Operation
                    : Parent?.Find(viewId, kind, activeOperations);
    }

    public bool HasSettingsView(string packageId)
        => viewRegistry.HasSettingsView(packageId);

    public IReadOnlyList<PackageSettingsViewDescriptor> ListSettingsViewPackages()
        => viewRegistry.ListSettingsViewPackages(isPackageDisabled);

    public Control? GetOrCreateSettingsView(string packageId)
        => viewRegistry.GetOrCreateSettingsView(packageId, isPackageDisabled, reportHostedViewFailure);

    public AppPackageSettingsViewNavigationTarget? GetSettingsViewForNavigation(
        string packageId,
        bool requireReplacement)
        => viewRegistry.GetSettingsViewForNavigation(
            packageId,
            requireReplacement,
            isPackageDisabled,
            reportHostedViewFailure);

    public IReadOnlyList<PackageViewDescriptor> GetPackageViewDescriptors(string packageId)
        => viewRegistry.GetPackageViewDescriptors(packageId);
}
