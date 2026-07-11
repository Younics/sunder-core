using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class StacksWindowViewModel
{
    private async Task RefreshSelectedInstallPlanAsync(int selectionVersion)
    {
        ClearSelectedInstallPlan();
        if (_selectedPackageRequirements.Count == 0)
        {
            NotifySelectedDetailsChanged();
            return;
        }

        if (!TryResolveRegistryUrl(out var registryUrl))
        {
            return;
        }

        try
        {
            StatusText = "Resolving Stack package graph...";
            using var registryClient = _registryClientFactory(registryUrl);
            var plan = await _registryInstallService.ResolveInstallPlanForPackagesAsync(
                _selectedPackageRequirements,
                registryClient,
                _runtimeApiClient,
                progress => StatusText = progress.StatusText);
            if (selectionVersion != _selectionVersion || _disposed)
            {
                return;
            }

            ApplySelectedInstallPlan(plan);
        }
        catch (Exception ex)
        {
            if (selectionVersion == _selectionVersion && !_disposed)
            {
                SelectedInstallPlanErrors.Add(ex.Message);
                _selectedInstallPlanHasErrors = true;
                _selectedInstallPlanReady = true;
                StatusText = ex.Message;
                NotifySelectedDetailsChanged();
            }
        }
    }

    private void ApplySelectedInstallPlan(RegistryResolveInstallPlanResponse plan)
    {
        ClearSelectedInstallPlan();
        var presentation = StackInstallPlanProjector.Project(plan);
        foreach (var item in presentation.Items)
        {
            SelectedInstallPlanItems.Add(item);
        }

        foreach (var warning in presentation.Warnings)
        {
            SelectedInstallPlanWarnings.Add(warning);
        }

        foreach (var error in presentation.Errors)
        {
            SelectedInstallPlanErrors.Add(error);
        }

        _selectedInstallPlanReady = true;
        _selectedInstallPlanHasErrors = !presentation.Success;
        StatusText = presentation.Success
            ? plan.Items.Count == 0
                ? "All Stack package requirements are already satisfied."
                : $"Stack package graph resolved {plan.Items.Count} package change{(plan.Items.Count == 1 ? string.Empty : "s")}."
            : SelectedInstallPlanErrors.FirstOrDefault() ?? "Stack package graph resolution failed.";
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
        ClearSelectedImportPreview();
    }

    private void ClearSelectedInstallPlan()
    {
        SelectedInstallPlanItems.Clear();
        SelectedInstallPlanWarnings.Clear();
        SelectedInstallPlanErrors.Clear();
        _selectedInstallPlanReady = false;
        _selectedInstallPlanHasErrors = false;
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private async Task RefreshSelectedImportPreviewAsync(int selectionVersion)
    {
        var existingInputValues = GetImportInputValues(includeEmptyValues: true);
        ClearSelectedImportPreview();
        if (SelectedStack is null || !HasSelectedFragments)
        {
            NotifySelectedDetailsChanged();
            return;
        }

        try
        {
            StatusText = "Previewing Stack setup actions...";
            var upload = await _runtimeApiClient.UploadStackAsync(SelectedStack.LocalPath);
            var preview = await _runtimeApiClient.PreviewStackImportAsync(new RuntimeStackImportPreviewRequest(
                upload.UploadId,
                GetSelectedFragmentIds(),
                existingInputValues.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>()));
            if (selectionVersion != _selectionVersion || _disposed)
            {
                return;
            }

            var presentation = StackImportPreviewProjector.Project(
                preview,
                existingInputValues,
                NotifyImportRequiredInputChanged);
            foreach (var action in presentation.Actions)
            {
                SelectedImportActions.Add(action);
            }

            foreach (var input in presentation.RequiredInputs)
            {
                SelectedImportRequiredInputs.Add(input);
            }

            foreach (var warning in presentation.Warnings)
            {
                SelectedImportWarnings.Add(warning);
            }

            foreach (var error in presentation.Errors)
            {
                SelectedImportErrors.Add(error);
            }

            StatusText = presentation.Success
                ? SelectedImportActions.Count == 0
                    ? "No actions are needed for the selected fragments."
                    : $"Previewed {SelectedImportActions.Count} setup action{(SelectedImportActions.Count == 1 ? string.Empty : "s")}."
                : SelectedImportErrors.FirstOrDefault() ?? "Stack setup preview failed.";
            NotifySelectedDetailsChanged();
            NotifyCommandStateChanged();
        }
        catch (Exception ex)
        {
            if (selectionVersion == _selectionVersion && !_disposed)
            {
                SelectedImportErrors.Add(ex.Message);
                StatusText = ex.Message;
                NotifySelectedDetailsChanged();
                NotifyCommandStateChanged();
            }
        }
    }

    private void ClearSelectedImportPreview()
    {
        SelectedImportActions.Clear();
        SelectedImportRequiredInputs.Clear();
        SelectedImportWarnings.Clear();
        SelectedImportErrors.Clear();
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private void ApplyInstallResult(RegistryPackageInstallExecutionResult result)
    {
        SelectedInstallPlanWarnings.Clear();
        foreach (var warning in result.Warnings)
        {
            SelectedInstallPlanWarnings.Add(warning);
        }

        SelectedInstallPlanErrors.Clear();
        foreach (var error in result.Errors)
        {
            SelectedInstallPlanErrors.Add(error);
        }

        StatusText = result.Success
            ? result.PlanItems.Count == 0
                ? "Stack package requirements are already installed. Fragment import will be enabled when package contributors are wired."
                : $"Installed {result.PlanItems.Count} Stack package change{(result.PlanItems.Count == 1 ? string.Empty : "s")}. Fragment import will be enabled when package contributors are wired."
            : result.Message;
        NotifySelectedDetailsChanged();
    }

    private async Task ApplySelectedStackImportAsync()
    {
        if (SelectedStack is null)
        {
            return;
        }

        if (SelectedImportActions.Count == 0 && !HasSelectedImportErrors)
        {
            await RefreshSelectedImportPreviewAsync(_selectionVersion);
        }

        if (HasSelectedImportErrors)
        {
            StatusText = SelectedImportErrors.FirstOrDefault() ?? "Resolve Stack setup preview errors before importing.";
            return;
        }

        var selectedActionIds = SelectedImportActions
            .Where(action => action.IsSelected)
            .Select(action => action.ActionId)
            .ToArray();
        if (selectedActionIds.Length == 0)
        {
            StatusText = "No Stack setup actions are selected.";
            return;
        }

        var missingRequiredInput = SelectedImportRequiredInputs.FirstOrDefault(input => input.IsMissingRequiredValue);
        if (missingRequiredInput is not null)
        {
            StatusText = $"Enter a value for '{missingRequiredInput.Label}' before importing this Stack.";
            return;
        }

        var upload = await _runtimeApiClient.UploadStackAsync(SelectedStack.LocalPath);
        var result = await _runtimeApiClient.ImportStackAsync(new RuntimeStackImportRequest(
            upload.UploadId,
            GetSelectedFragmentIds(),
            GetImportInputValues(),
            new Dictionary<string, string>(),
            selectedActionIds));

        SelectedImportWarnings.Clear();
        foreach (var warning in result.Warnings)
        {
            SelectedImportWarnings.Add(warning);
        }

        SelectedImportErrors.Clear();
        foreach (var error in result.Errors)
        {
            SelectedImportErrors.Add(error);
        }

        await NotifyStackImportAppliedAsync(result.AppliedContributions, CancellationToken.None);

        StatusText = result.Success
            ? $"Imported {result.ImportedItems.Count} Stack setup item{(result.ImportedItems.Count == 1 ? string.Empty : "s")}."
            : result.Errors.FirstOrDefault() ?? "Stack setup import failed.";
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
    }

    private async Task NotifyStackImportAppliedAsync(
        IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor> appliedContributions,
        CancellationToken cancellationToken)
    {
        if (appliedContributions.Count == 0)
        {
            return;
        }

        var warnings = await _notifyStackImportAppliedAsync(appliedContributions, cancellationToken);
        foreach (var warning in warnings)
        {
            SelectedImportWarnings.Add(warning);
        }
    }

    private bool TryResolveRegistryUrl(out Uri registryUrl)
    {
        if (RegistryUrlHelper.TryParse(RegistryUrlText, out registryUrl!) && registryUrl is not null)
        {
            return true;
        }

        StatusText = "Enter a valid HTTP Registry URL before resolving or using this Stack.";
        SelectedInstallPlanErrors.Clear();
        SelectedInstallPlanErrors.Add(StatusText);
        _selectedInstallPlanHasErrors = true;
        _selectedInstallPlanReady = true;
        NotifySelectedDetailsChanged();
        NotifyCommandStateChanged();
        return false;
    }

    private bool TryResolveRegistryUrlForRegistryAction(out Uri registryUrl)
    {
        if (RegistryUrlHelper.TryParse(RegistryUrlText, out registryUrl!) && registryUrl is not null)
        {
            return true;
        }

        StatusText = "Enter a valid HTTP Registry URL before using Registry Stacks.";
        NotifyRegistryStackStateChanged();
        return false;
    }

    private bool TryResolvePublishedRegistryUrl(LocalStackLibraryItemViewModel stack, out Uri registryUrl)
    {
        if (RegistryUrlHelper.TryParse(stack.RegistryUrl, out registryUrl!) && registryUrl is not null)
        {
            return true;
        }

        return TryResolveRegistryUrlForRegistryAction(out registryUrl);
    }

    private static bool MatchesSearch(LocalStackLibraryItem item, string query)
        => string.IsNullOrWhiteSpace(query)
           || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
           || item.StackId.Contains(query, StringComparison.OrdinalIgnoreCase)
           || (item.Summary?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

    private void QueueRegistrySearch(TimeSpan? delay = null)
        => _registrySearchScheduler.Queue(delay);

    private void CancelQueuedRegistrySearch()
        => _registrySearchScheduler.Cancel();
}
