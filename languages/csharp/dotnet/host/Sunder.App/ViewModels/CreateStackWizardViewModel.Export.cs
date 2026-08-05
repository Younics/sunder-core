using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class CreateStackWizardViewModel
{
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (CurrentStep != CreateStackWizardStep.Packages)
        {
            CurrentStep = CurrentStep == CreateStackWizardStep.Review
                ? CreateStackWizardStep.Items
                : CreateStackWizardStep.Packages;
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next()
    {
        if (CurrentStep != CreateStackWizardStep.Review)
        {
            CurrentStep = CurrentStep == CreateStackWizardStep.Packages
                ? CreateStackWizardStep.Items
                : CreateStackWizardStep.Review;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllPackages))]
    private void SelectAllPackages()
    {
        foreach (var group in PackageGroups)
        {
            group.IsSelected = true;
        }

        NotifyWizardStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUnselectAllPackages))]
    private void UnselectAllPackages()
    {
        foreach (var group in PackageGroups)
        {
            group.IsSelected = false;
        }

        NotifyWizardStateChanged();
    }

    [RelayCommand]
    private void Cancel()
        => CloseRequested?.Invoke(false);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        var selection = CreateStackExportSelectionProjector.Project(PackageGroups);
        if (selection.PackageIds.Count == 0)
        {
            StatusText = "Select at least one package.";
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "V1", "create", Guid.NewGuid().ToString("N"), SunderStackFormat.BuildStackFileName(StackId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        IsBusy = true;
        Errors.Clear();
        Warnings.Clear();
        try
        {
            StatusText = IsEditMode ? "Saving Stack changes..." : "Saving local Stack...";
            var result = await runtimeApiClient.ExportStackAsync(new RuntimeStackExportRequest(
                StackId: StackId.Trim(),
                Name: StackName.Trim(),
                Summary: NormalizeOptionalText(StackShortDescription),
                SelectedItems: selection.Items,
                ReadmeMarkdown: null,
                Media: null,
                SelectedPackages: selection.PackageIds), _lifetime.Token);

            foreach (var warning in result.Warnings)
            {
                Warnings.Add(warning);
            }

            foreach (var error in result.Errors)
            {
                Errors.Add(error);
            }

            if (!result.Success)
            {
                StatusText = Errors.FirstOrDefault() ?? "Stack export failed.";
                return;
            }

            if (result.Download is null)
            {
                StatusText = "Runtime did not provide the exported Stack content.";
                return;
            }
            await runtimeApiClient.DownloadContentAsync(result.Download, path, _lifetime.Token);

            var saved = IsEditMode
                ? await SaveEditedStackAsync(path, Path.GetDirectoryName(path)!, _lifetime.Token)
                : await library.ImportAsync(path, _lifetime.Token);
            if (!IsEditMode)
            {
                await library.UpdateDetailsAsync(saved.StackId, BuildLocalDetails(), _lifetime.Token);
            }

            CreatedStackId = saved.StackId;
            StatusText = IsEditMode
                ? $"Saved local Stack '{saved.Name}'."
                : $"Created local Stack '{saved.Name}'.";
            CloseRequested?.Invoke(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Errors.Add(ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            TryDeleteDirectory(Path.GetDirectoryName(path));
            NotifyWizardStateChanged();
        }
    }

    private IReadOnlyList<LocalStackDetailPackage> BuildLocalDetails()
        => PackageGroups
            .Where(group => group.IsSelected && group.HasSelectedItems)
            .Select(group => new LocalStackDetailPackage(
                group.PackageId,
                group.DisplayName,
                group.Glyph,
                group.Items
                    .Where(item => item.IsSelected && item.HasSelectedDetails)
                    .Select(item => new LocalStackDetailItem(
                        item.ItemId,
                        item.DisplayName,
                        item.SummaryText,
                        item.Details
                            .Where(detail => detail.IsSelected)
                            .Select(detail => new LocalStackDetailValue(
                                detail.Label,
                                detail.SelectedExportBehavior == "Ask on import" ? "Importer will provide this value." : detail.Value,
                                detail.SelectedExportBehavior))
                            .ToArray(),
                        item.Kind))
                    .ToArray(),
                group.IconAssetPath))
            .ToArray();

    private async Task<LocalStackLibraryItem> SaveEditedStackAsync(
        string generatedStackPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (_editContext is null || _editManifest is null)
        {
            throw new InvalidOperationException("Edit mode is missing the local Stack being edited.");
        }

        var generatedManifest = await library.ReadManifestAsync(generatedStackPath, cancellationToken);
        var preservedFragments = GetSelectedPreservedFragments();
        var finalManifest = BuildEditedManifest(generatedManifest, preservedFragments);
        var mergedPath = Path.Combine(workingDirectory, SunderStackFormat.BuildStackFileName(StackId.Trim() + "-edited"));
        await SunderStackArchiveWriter.MergeAsync(
            generatedStackPath,
            _editContext.Stack.LocalPath,
            finalManifest,
            mergedPath,
            BuildPreservedArchivePaths(preservedFragments),
            cancellationToken);
        return await library.ReplaceAsync(mergedPath, _editContext.Stack.StackId, cancellationToken);
    }

    private SunderStackManifest BuildEditedManifest(
        SunderStackManifest generatedManifest,
        IReadOnlyList<SunderStackFragmentManifest> preservedFragments)
    {
        var selectedPackageIds = PackageGroups
            .Where(group => group.IsSelected)
            .Select(group => group.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generatedFragments = generatedManifest.Fragments ?? [];
        var generatedFragmentIds = generatedFragments
            .Select(fragment => fragment.FragmentId)
            .Where(fragmentId => !string.IsNullOrWhiteSpace(fragmentId))
            .Select(fragmentId => fragmentId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fragments = generatedFragments
            .Concat(preservedFragments.Where(fragment => string.IsNullOrWhiteSpace(fragment.FragmentId) || !generatedFragmentIds.Contains(fragment.FragmentId!)))
            .ToArray();

        return new SunderStackManifest
        {
            SchemaVersion = generatedManifest.SchemaVersion ?? _editManifest?.SchemaVersion ?? SunderStackFormat.CurrentSchemaVersion,
            MinReaderVersion = generatedManifest.MinReaderVersion ?? _editManifest?.MinReaderVersion ?? SunderStackFormat.CurrentReaderVersion,
            Features = _editManifest?.Features ?? generatedManifest.Features,
            RequiredFeatures = _editManifest?.RequiredFeatures ?? generatedManifest.RequiredFeatures,
            StackId = StackId.Trim(),
            Name = StackName.Trim(),
            Summary = NormalizeOptionalText(StackShortDescription),
            ReadmeMarkdown = _editManifest?.ReadmeMarkdown,
            CreatedAtUtc = _editManifest?.CreatedAtUtc ?? _editContext?.Stack.CreatedAtUtc ?? generatedManifest.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Packages = MergePackageRequirements(
                generatedManifest.Packages ?? [],
                (_editManifest?.Packages ?? []).Where(package => !string.IsNullOrWhiteSpace(package.PackageId) && selectedPackageIds.Contains(package.PackageId!))),
            Fragments = fragments,
            Media = _editManifest?.Media,
        };
    }

    private static IReadOnlyList<SunderStackPackageRequirement> MergePackageRequirements(
        IReadOnlyList<SunderStackPackageRequirement> generatedPackages,
        IEnumerable<SunderStackPackageRequirement> preservedPackages)
    {
        var packages = new Dictionary<string, SunderStackPackageRequirement>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in preservedPackages.Concat(generatedPackages))
        {
            if (string.IsNullOrWhiteSpace(package.PackageId) || packages.ContainsKey(package.PackageId!))
            {
                continue;
            }

            packages[package.PackageId!] = package;
        }

        return packages.Values
            .OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<string> BuildPreservedArchivePaths(IReadOnlyList<SunderStackFragmentManifest> preservedFragments)
    {
        var paths = new List<string>();
        foreach (var fragment in preservedFragments)
        {
            if (!string.IsNullOrWhiteSpace(fragment.PayloadPath))
            {
                paths.Add(fragment.PayloadPath!);
            }

            if (!string.IsNullOrWhiteSpace(fragment.FragmentId))
            {
                paths.Add($"{SunderStackFormat.FragmentFileRoot}{fragment.FragmentId}");
            }
        }

        foreach (var media in _editManifest?.Media ?? [])
        {
            if (!string.IsNullOrWhiteSpace(media.Path))
            {
                paths.Add(media.Path!);
            }
        }

        return paths;
    }

    private IReadOnlyList<SunderStackFragmentManifest> GetSelectedPreservedFragments()
    {
        if (_editManifest is null || PreservedFragments.Count == 0)
        {
            return [];
        }

        var selectedPackageIds = PackageGroups
            .Where(group => group.IsSelected)
            .Select(group => group.PackageId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return PreservedFragments
            .Select(fragment => fragment.Fragment)
            .Where(fragment => string.IsNullOrWhiteSpace(fragment.OwnerPackageId) || selectedPackageIds.Contains(fragment.OwnerPackageId!))
            .ToArray();
    }

}
