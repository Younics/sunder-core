using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class CreateStackWizardViewModel
{
    private void NotifyWizardStateChanged()
    {
        OnPropertyChanged(nameof(IsPackagesStep));
        OnPropertyChanged(nameof(IsItemsStep));
        OnPropertyChanged(nameof(IsReviewStep));
        OnPropertyChanged(nameof(PackagesStepComplete));
        OnPropertyChanged(nameof(ItemsStepComplete));
        OnPropertyChanged(nameof(ReviewStepReady));
        OnPropertyChanged(nameof(ShowNextButton));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(WizardTitle));
        OnPropertyChanged(nameof(WizardDescription));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(CanEditStackId));
        OnPropertyChanged(nameof(IsStackIdReadOnly));
        OnPropertyChanged(nameof(HasPackageGroups));
        OnPropertyChanged(nameof(ShowNoPackageGroups));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasSelectedPackages));
        OnPropertyChanged(nameof(HasPreservedFragments));
        OnPropertyChanged(nameof(SelectedPreservedFragmentCount));
        OnPropertyChanged(nameof(PreservedFragmentsSummary));
        OnPropertyChanged(nameof(HasSelectedPackagesWithItems));
        OnPropertyChanged(nameof(ShowNoItemsStepPackages));
        OnPropertyChanged(nameof(HasSelectedItems));
        OnPropertyChanged(nameof(SelectedPackageCount));
        OnPropertyChanged(nameof(SelectedItemCount));
        OnPropertyChanged(nameof(CanSelectAllPackages));
        OnPropertyChanged(nameof(CanUnselectAllPackages));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(PackagesStepStatus));
        OnPropertyChanged(nameof(ItemsStepStatus));
        OnPropertyChanged(nameof(ReviewStepStatus));
        OnPropertyChanged(nameof(IncludedReviewDetailCount));
        OnPropertyChanged(nameof(ExcludedReviewDetailCount));
        OnPropertyChanged(nameof(HasExcludedReviewDetails));
        OnPropertyChanged(nameof(HasStackShortDescription));
        OnPropertyChanged(nameof(ReviewImpactSummary));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepDescription));
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        SelectAllPackagesCommand.NotifyCanExecuteChanged();
        UnselectAllPackagesCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
    }

    private async Task<IReadOnlyDictionary<string, StackPackageInfo>> LoadPackageInfoAsync(CancellationToken cancellationToken)
        => await new StackPackageInfoProvider(runtimeApiClient).LoadAsync(cancellationToken);

    private async Task<IReadOnlyList<string>> LoadActivePackageIdsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await runtimeApiClient.GetActivePackagesAsync(cancellationToken))
                .Select(package => package.PackageId)
                .Where(packageId => !string.IsNullOrWhiteSpace(packageId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        foreach (var group in PackageGroups)
        {
            group.Dispose();
        }

        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Temporary local Stack creation cleanup is best effort.
        }
    }

    private static string? NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
