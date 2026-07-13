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
    private void ApplyManifest(
        SunderStackManifest manifest,
        IReadOnlyDictionary<string, StackPackageInfo> packageInfo)
    {
        _packageRequirements = manifest.Packages ?? [];
        PackageRows.Clear();
        foreach (var package in _packageRequirements)
        {
            var packageId = package.PackageId ?? "unknown";
            packageInfo.TryGetValue(packageId, out var info);
            PackageRows.Add(new UseStackPackageReviewViewModel(package, info));
        }

        BuildSetupPackageGroups(manifest, packageInfo);
        ApplyManifestRequiredInputs(manifest);

        _manifestLoaded = true;
        StatusText = SetupPackageGroups.SelectMany(group => group.Items).Any() == false
            ? "This Stack has no fragments to import."
            : "Stack manifest loaded.";
        NotifyWizardStateChanged();
    }

    private async Task NotifyStackImportAppliedAsync(
        IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor> appliedContributions,
        CancellationToken cancellationToken)
    {
        if (appliedContributions.Count == 0)
        {
            return;
        }

        var warnings = await notifyStackImportAppliedAsync(appliedContributions, cancellationToken);
        foreach (var warning in warnings)
        {
            ImportWarnings.Add(warning);
        }
    }

    private static IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor> ToAppliedContributions(
        IReadOnlyList<RuntimeStackImportContributorResultDescriptor> results)
        => results
            .Where(result => result.ImportedItems.Count > 0)
            .Select(result => new RuntimeStackImportAppliedContributionDescriptor(
                result.OwnerPackageId,
                result.ContributorId,
                result.FragmentIds,
                result.ImportedItems))
            .ToArray();

    private void BuildSetupPackageGroups(
        SunderStackManifest manifest,
        IReadOnlyDictionary<string, StackPackageInfo> packageInfo)
    {
        SetupPackageGroups.Clear();
        var localDetails = stack.Details ?? LocalStackLibraryService.BuildDetailsFromManifest(manifest);
        var detailPackages = localDetails.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        foreach (var group in (manifest.Fragments ?? []).GroupBy(fragment => fragment.OwnerPackageId ?? "unknown package", StringComparer.OrdinalIgnoreCase))
        {
            var packageId = group.Key;
            packageInfo.TryGetValue(packageId, out var info);
            detailPackages.TryGetValue(packageId, out var detailPackage);
            var displayName = string.IsNullOrWhiteSpace(detailPackage?.DisplayName) || string.Equals(detailPackage.DisplayName, packageId, StringComparison.OrdinalIgnoreCase)
                ? info?.DisplayName ?? packageId
                : detailPackage!.DisplayName;
            var glyph = !string.IsNullOrWhiteSpace(detailPackage?.Glyph) ? detailPackage!.Glyph : StackDisplayFormatters.PackageGlyph(info?.Icon, displayName, packageId);
            var iconUri = info?.IconUri ?? (!string.IsNullOrWhiteSpace(detailPackage?.IconAssetPath)
                ? runtimeApiClient.CreatePackageAssetUri(packageId, detailPackage!.IconAssetPath!)
                : null);
            var items = group
                .Select(fragment => new UseStackSetupItemViewModel(
                    fragment,
                    FindDetailItem(detailPackage, fragment),
                    OnSetupItemSelectionChanged))
                .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SetupPackageGroups.Add(new UseStackSetupPackageGroupViewModel(packageId, displayName, glyph, iconUri, items));
        }
    }

    private static LocalStackDetailItem? FindDetailItem(LocalStackDetailPackage? detailPackage, SunderStackFragmentManifest fragment)
    {
        if (detailPackage is null)
        {
            return null;
        }

        return detailPackage.Items.FirstOrDefault(item => string.Equals(item.ItemId, fragment.FragmentId, StringComparison.OrdinalIgnoreCase))
               ?? detailPackage.Items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(fragment.Preview?.SourceItemId)
                                                            && string.Equals(item.ItemId, fragment.Preview.SourceItemId, StringComparison.OrdinalIgnoreCase))
               ?? detailPackage.Items.FirstOrDefault(item => !string.IsNullOrWhiteSpace(fragment.DisplayName)
                                                            && string.Equals(item.DisplayName, fragment.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyManifestRequiredInputs(SunderStackManifest manifest)
    {
        RequiredInputs.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in (manifest.Fragments ?? []).SelectMany(fragment => (fragment.RequiredInputs ?? []).Select(input => (Input: input, OwnerPackageId: fragment.OwnerPackageId, ContributorId: fragment.ContributorId ?? fragment.OwnerPackageId ?? "stack"))))
        {
            var inputId = input.Input.InputId ?? input.Input.Label ?? "input";
            if (!seen.Add(inputId))
            {
                continue;
            }

            RequiredInputs.Add(new UseStackRequiredInputValueViewModel(
                new RuntimeStackRequiredInputDescriptor(
                    inputId,
                    input.ContributorId,
                    input.Input.Label ?? inputId,
                    input.Input.Required != false,
                    input.Input.Description,
                    input.Input.DefaultValue)
                {
                    OwnerPackageId = input.OwnerPackageId,
                },
                null,
                OnRequiredInputChanged));
        }
    }

    private async Task RefreshInstallPlanAsync(CancellationToken cancellationToken = default)
    {
        InstallPlanItems.Clear();
        InstallPlanWarnings.Clear();
        InstallPlanErrors.Clear();
        _installPlanReady = false;
        _installPlanHasErrors = false;

        if (_packageRequirements.Count == 0)
        {
            _installPlanReady = true;
            StatusText = "This Stack has no package requirements.";
            NotifyWizardStateChanged();
            return;
        }

        if (!TryResolveRegistryUrl(out var registryUrl))
        {
            return;
        }

        try
        {
            StatusText = "Resolving Stack package graph...";
            using var registryClient = registryClientFactory(registryUrl);
            var plan = await registryInstallService.ResolveInstallPlanForPackagesAsync(
                _packageRequirements,
                registryClient,
                runtimeApiClient,
                progress => StatusText = progress.StatusText,
                cancellationToken);
            foreach (var item in plan.Items)
            {
                InstallPlanItems.Add(new StackPackageInstallPlanItemViewModel(item));
            }

            foreach (var warning in plan.Warnings)
            {
                InstallPlanWarnings.Add(warning);
            }

            foreach (var error in plan.Errors.Concat(plan.Conflicts.Select(conflict => conflict.Message)))
            {
                InstallPlanErrors.Add(error);
            }

            _installPlanReady = true;
            _installPlanHasErrors = !plan.Success;
            ApplyPackageInstallPlan(plan);
            StatusText = plan.Success
                ? InstallPlanItems.Count == 0
                    ? "All Stack package requirements are already satisfied."
                    : $"Stack package graph resolved {InstallPlanItems.Count} package change{StackDisplayFormatters.Plural(InstallPlanItems.Count)}."
                : InstallPlanErrors.FirstOrDefault() ?? "Stack package graph resolution failed.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            InstallPlanErrors.Add(ex.Message);
            _installPlanReady = true;
            _installPlanHasErrors = true;
            StatusText = ex.Message;
        }
        finally
        {
            NotifyWizardStateChanged();
        }
    }

}
