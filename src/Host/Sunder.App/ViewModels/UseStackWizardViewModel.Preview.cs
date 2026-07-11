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
        using var request = _previewRequest.Start(cancellationToken);
        var existingInputValues = GetInputValues(includeEmptyValues: true);
        ImportActions.Clear();
        RequiredInputs.Clear();
        ImportWarnings.Clear();
        ImportErrors.Clear();
        if (!HasSelectedSetupItems)
        {
            StatusText = HasPackageRequirements
                ? "No Stack setup actions are needed for package-only requirements."
                : "Select at least one setup item to preview actions.";
            NotifyWizardStateChanged();
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Previewing Stack setup actions...";
            var upload = await runtimeApiClient.UploadStackAsync(stack.LocalPath, request.Token);
            var preview = await runtimeApiClient.PreviewStackImportAsync(new RuntimeStackImportPreviewRequest(
                upload.UploadId,
                GetSelectedFragmentIds(),
                existingInputValues.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>()), request.Token);
            if (!request.IsCurrent)
            {
                return;
            }

            foreach (var action in preview.Actions)
            {
                ImportActions.Add(new UseStackImportActionViewModel(action));
            }

            foreach (var input in preview.RequiredInputs)
            {
                RequiredInputs.Add(new UseStackRequiredInputValueViewModel(
                    input,
                    existingInputValues.TryGetValue(input.InputId, out var value) ? value : null,
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
            if (request.IsCurrent)
            {
                IsBusy = false;
                NotifyWizardStateChanged();
            }
        }
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

    private void ApplyPackageInstallPlan(RegistryResolveInstallPlanResponse plan)
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
        if (!IsBusy)
        {
            _tasks.Observe(RefreshImportPreviewCoreAsync(_tasks.Token), "refreshing the Stack import preview");
        }
    }

    private void OnRequiredInputChanged()
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

    private Dictionary<string, string> GetInputValues(bool includeEmptyValues = false)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in RequiredInputs)
        {
            if (includeEmptyValues || !string.IsNullOrWhiteSpace(input.Value))
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _previewRequest.Dispose();
        _tasks.Dispose();
        _stackProfile.Dispose();
        GC.SuppressFinalize(this);
    }

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

}
