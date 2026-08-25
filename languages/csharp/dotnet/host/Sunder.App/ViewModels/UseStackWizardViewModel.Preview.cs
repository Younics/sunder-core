using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class UseStackWizardViewModel
{
    private async Task RefreshImportPreviewCoreAsync(CancellationToken cancellationToken)
    {
        using var planOperation = TryBeginPlanOperation();
        if (planOperation is null)
        {
            return;
        }

        LatestAsyncRequest.Lease request;
        try
        {
            request = _previewRequest.Start(cancellationToken);
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            return;
        }

        using var requestLease = request;
        string? pendingPlanId = null;
        string? adoptedPlanId = null;
        var existingInputs = RequiredInputs.ToArray();
        var existingInputValues = GetInputValues(existingInputs, includeEmptyValues: true);
        var existingActionSelections = ImportActions.ToDictionary(
            action => action.ActionId,
            action => action.IsSelected,
            StringComparer.OrdinalIgnoreCase);
        ImportActions.Clear();
        RequiredInputs.Clear();
        ImportWarnings.Clear();
        ImportErrors.Clear();
        IsBusy = true;
        try
        {
            await DiscardCurrentImportPlanAsync();
            request.Token.ThrowIfCancellationRequested();
            if (!HasSelectedSetupItems)
            {
                StatusText = HasPackageRequirements
                    ? "No Stack setup actions are needed for package-only requirements."
                    : "Select at least one setup item to preview actions.";
                return;
            }

            StatusText = "Previewing Stack setup actions...";
            var upload = await runtimeApiClient.UploadStackAsync(stack.LocalPath, request.Token);
            var preview = await runtimeApiClient.PreviewStackImportAsync(new RuntimeStackImportPreviewRequest(
                upload.UploadId,
                GetSelectedFragmentIds(),
                existingInputValues.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>()), request.Token);
            pendingPlanId = preview.PlanId;
            if (!request.IsCurrent)
            {
                return;
            }

            foreach (var action in preview.Actions)
            {
                var actionViewModel = new UseStackImportActionViewModel(action, OnImportActionSelectionChanged);
                if (existingActionSelections.TryGetValue(action.ActionId, out var isSelected))
                {
                    actionViewModel.IsSelected = isSelected;
                }

                ImportActions.Add(actionViewModel);
            }

            foreach (var input in preview.RequiredInputs)
            {
                RequiredInputs.Add(new UseStackRequiredInputValueViewModel(
                    input,
                    existingInputs.FirstOrDefault(existing => existing.Matches(input))?.Value,
                    OnRequiredInputChanged));
            }

            foreach (var warning in preview.Warnings)
            {
                ImportWarnings.Add(warning);
            }

            foreach (var conflict in preview.Conflicts)
            {
                var prefix = string.Equals(conflict.Severity, "Error", StringComparison.OrdinalIgnoreCase) ? "Error" : "Warning";
                (prefix == "Error" ? ImportErrors : ImportWarnings).Add($"{prefix}: {conflict.Message}");
            }

            foreach (var error in preview.Errors)
            {
                ImportErrors.Add(error);
            }

            StatusText = preview.Success
                ? ImportActions.Count == 0
                    ? "No actions are needed for the selected fragments."
                    : $"Previewed {ImportActions.Count} setup action{StackDisplayFormatters.Plural(ImportActions.Count)}."
                : ImportErrors.FirstOrDefault() ?? "Stack setup preview failed.";

            if (preview.Success && !string.IsNullOrWhiteSpace(pendingPlanId))
            {
                adoptedPlanId = pendingPlanId;
                pendingPlanId = null;
                var supersededPlanId = Interlocked.Exchange(ref _importPlanId, adoptedPlanId);
                await DiscardImportPlanAsync(supersededPlanId);
                if (!request.IsCurrent)
                {
                    await TryDiscardOwnedImportPlanAsync(adoptedPlanId);
                    adoptedPlanId = null;
                    return;
                }

                adoptedPlanId = null;
            }
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ImportErrors.Add(ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            await TryDiscardImportPlanAsync(pendingPlanId);
            await TryDiscardOwnedImportPlanAsync(adoptedPlanId);
            if (request.IsCurrent)
            {
                IsBusy = false;
                NotifyWizardStateChanged();
            }
        }
    }

    private string? DetachCurrentImportPlan()
        => Interlocked.Exchange(ref _importPlanId, null);

    private async Task DiscardCurrentImportPlanAsync()
        => await DiscardImportPlanAsync(DetachCurrentImportPlan());

    private async Task DiscardImportPlanAsync(string? planId)
    {
        if (!string.IsNullOrWhiteSpace(planId))
        {
            await runtimeApiClient.DiscardStackImportPlanAsync(planId);
        }
    }

    private async Task TryDiscardImportPlanAsync(string? planId)
    {
        try
        {
            await DiscardImportPlanAsync(planId);
        }
        catch (Exception exception) when (AppPackageUiFaultPolicy.IsRecoverable(exception))
        {
            AppSessionLog.WriteError("Failed to discard a Stack import preview plan.", exception);
        }
    }

    private async Task TryDiscardOwnedImportPlanAsync(string? planId)
    {
        if (string.IsNullOrWhiteSpace(planId)
            || !ReferenceEquals(Interlocked.CompareExchange(ref _importPlanId, null, planId), planId))
        {
            return;
        }

        await TryDiscardImportPlanAsync(planId);
    }

    private void ApplyInstallResult(RegistryPackageInstallExecutionResult result)
    {
        InstallPlanWarnings.Clear();
        foreach (var warning in result.Warnings)
        {
            InstallPlanWarnings.Add(warning);
        }

        InstallPlanErrors.Clear();
        foreach (var error in result.Errors)
        {
            InstallPlanErrors.Add(error);
        }

        StatusText = result.Success
            ? $"Installed {result.PlanItems.Count} Stack package change{StackDisplayFormatters.Plural(result.PlanItems.Count)}."
            : result.Message;
    }

    private void ApplyPackageInstallPlan(RuntimeRegistryResolveInstallPlanResponse plan)
    {
        foreach (var row in PackageRows)
        {
            var item = plan.Items.FirstOrDefault(item => string.Equals(item.PackageId, row.PackageId, StringComparison.OrdinalIgnoreCase));
            row.ApplyInstallPlan(item, plan.Success);
        }
    }

    private Task<IReadOnlyDictionary<string, StackPackageInfo>> LoadPackageInfoAsync(CancellationToken cancellationToken)
        => new StackPackageInfoProvider(runtimeApiClient).LoadAsync(cancellationToken);

    private void OnSetupItemSelectionChanged()
    {
        NotifyWizardStateChanged();
        if (!IsBusy && !_disposed)
        {
            _tasks.Observe(RefreshImportPreviewCoreAsync(_tasks.Token), "refreshing the Stack import preview");
        }
    }

    private void OnRequiredInputChanged()
        => NotifyWizardStateChanged();

    private void OnImportActionSelectionChanged()
        => NotifyWizardStateChanged();

    private bool TryResolveRegistryUrl(out Uri registryUrl)
    {
        if (RegistryUrlHelper.TryParse(registryUrlText, out registryUrl!) && registryUrl is not null)
        {
            return true;
        }

        StatusText = "Enter a valid HTTP Registry URL before using this Stack.";
        InstallPlanErrors.Clear();
        InstallPlanErrors.Add(StatusText);
        _installPlanReady = true;
        _installPlanHasErrors = true;
        NotifyWizardStateChanged();
        return false;
    }

    private IReadOnlyList<string> GetSelectedFragmentIds()
        => SetupPackageGroups
            .SelectMany(group => group.Items)
            .Where(item => item.IsSelected)
            .Select(item => item.FragmentId)
            .ToArray();

    private static Dictionary<string, string> GetInputValues(
        IEnumerable<UseStackRequiredInputValueViewModel> inputs,
        bool includeEmptyValues = false)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (input.IsHostScoped && (includeEmptyValues || !string.IsNullOrWhiteSpace(input.Value)))
            {
                values[input.InputId] = input.Value;
            }
        }

        return values;
    }

    private void NotifyWizardStateChanged()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(HasPackages));
        OnPropertyChanged(nameof(HasInstallPlanItems));
        OnPropertyChanged(nameof(HasInstallPlanWarnings));
        OnPropertyChanged(nameof(HasInstallPlanErrors));
        OnPropertyChanged(nameof(ShowNoInstallPlanChanges));
        OnPropertyChanged(nameof(HasSetupPackageGroups));
        OnPropertyChanged(nameof(HasSetupItems));
        OnPropertyChanged(nameof(HasSelectedSetupItems));
        OnPropertyChanged(nameof(HasPackageRequirements));
        OnPropertyChanged(nameof(HasApplicableContent));
        OnPropertyChanged(nameof(HasImportActions));
        OnPropertyChanged(nameof(HasRequiredInputs));
        OnPropertyChanged(nameof(HasMissingRequiredInputs));
        OnPropertyChanged(nameof(HasImportWarnings));
        OnPropertyChanged(nameof(HasImportErrors));
        OnPropertyChanged(nameof(HasImportedItems));
        OnPropertyChanged(nameof(HasStackProfileMedia));
        OnPropertyChanged(nameof(HasStackReadme));
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private PlanOperationLease? TryBeginPlanOperation()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return null;
            }

            _activePlanOperations++;
            return new PlanOperationLease(this);
        }
    }

    private void EndPlanOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_lifecycleGate)
        {
            _activePlanOperations--;
            if (_activePlanOperations == 0)
            {
                drained = _planOperationsDrained;
                _planOperationsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private Task StartCleanup()
    {
        TaskCompletionSource<(Task OwnedTasks, Exception? Failure)> localCleanupStarted;
        Task cleanupTask;
        lock (_lifecycleGate)
        {
            if (_cleanupTask is not null)
            {
                return _cleanupTask;
            }

            _disposed = true;
            var operationsDrained = _activePlanOperations == 0
                ? Task.CompletedTask
                : (_planOperationsDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            var importPlanId = DetachCurrentImportPlan();
            localCleanupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cleanupTask = CompleteCleanupAsync(localCleanupStarted.Task, operationsDrained, importPlanId);
            _cleanupTask = cleanupTask;
        }

        var ownedTasks = Task.CompletedTask;
        Exception? localFailure = null;
        localFailure = TryRunLocalCleanup(_previewRequest.Dispose, localFailure);
        localFailure = TryRunLocalCleanup(() => ownedTasks = _tasks.StopAsync(), localFailure);
        localFailure = TryRunLocalCleanup(_tasks.Dispose, localFailure);
        localFailure = TryRunLocalCleanup(_stackProfile.Dispose, localFailure);
        localCleanupStarted.TrySetResult((ownedTasks, localFailure));
        GC.SuppressFinalize(this);
        return cleanupTask;
    }

    private async Task CompleteCleanupAsync(
        Task<(Task OwnedTasks, Exception? Failure)> localCleanupStarted,
        Task operationsDrained,
        string? importPlanId)
    {
        var localCleanup = await localCleanupStarted.ConfigureAwait(false);
        var failure = localCleanup.Failure;
        try
        {
            await Task.WhenAll(operationsDrained, localCleanup.OwnedTasks).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = CombineCleanupFailures(failure, exception);
        }

        await TryDiscardImportPlanAsync(importPlanId).ConfigureAwait(false);
        await TryDiscardImportPlanAsync(DetachCurrentImportPlan()).ConfigureAwait(false);

        if (failure is null)
        {
            return;
        }

        if (AppPackageUiFaultPolicy.IsRecoverable(failure))
        {
            AppSessionLog.WriteError("Failed to stop local Stack wizard work during disposal.", failure);
            return;
        }

        throw failure;
    }

    private static Exception? TryRunLocalCleanup(Action cleanup, Exception? failure)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            return CombineCleanupFailures(failure, exception);
        }

        return failure;
    }

    private static Exception CombineCleanupFailures(Exception? current, Exception next)
        => current is null ? next : new AggregateException(current, next);

    public void Dispose()
    {
        var cleanup = StartCleanup();
        if (!cleanup.IsCompletedSuccessfully)
        {
            AppCleanupQuarantine.Retain(cleanup, "disposing a Use Stack wizard");
        }
    }

    public ValueTask DisposeAsync() => new(StartCleanup());

    private static MarketplacePackageProfileViewModel BuildStackProfile(LocalStackLibraryItem stack)
    {
        var profile = new MarketplacePackageProfileViewModel();
        profile.Apply(new RegistryStackProfile(
            stack.StackId,
            stack.Summary,
            stack.ReadmeMarkdown,
            WebsiteUrl: null,
            SourceUrl: null,
            IssueTrackerUrl: null,
            License: null,
            Tags: [],
            Media: (stack.Media ?? [])
                .OrderBy(media => media.SortOrder)
                .Select(media => new RegistryStackMedia(
                    Guid.NewGuid(),
                    media.FileName,
                    media.ContentType,
                    media.Size,
                    media.AltText,
                    media.SortOrder,
                    new Uri(media.LocalPath).AbsoluteUri))
                .ToArray(),
            stack.UpdatedAtUtc));
        return profile;
    }

    private sealed class PlanOperationLease(UseStackWizardViewModel owner) : IDisposable
    {
        private UseStackWizardViewModel? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.EndPlanOperation();
    }

}
