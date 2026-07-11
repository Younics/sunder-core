using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveMarkdown.Avalonia;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class UseStackWizardViewModel(
    LocalStackLibraryService library,
    LocalStackLibraryItem stack,
    IRuntimeApiClient runtimeApiClient,
    RegistryPackageInstallService registryInstallService,
    Func<IReadOnlyList<string>, CancellationToken, Task> applyPackageLifecycleChangesAsync,
    Func<IReadOnlyList<RuntimeStackImportAppliedContributionDescriptor>, CancellationToken, Task<IReadOnlyList<string>>> notifyStackImportAppliedAsync,
    Func<Uri, IRegistryApiClient> registryClientFactory,
    string registryUrlText) : ViewModelBase, IDisposable
{
    private readonly MarketplacePackageProfileViewModel _stackProfile = BuildStackProfile(stack);
    private IReadOnlyList<SunderStackPackageRequirement> _packageRequirements = [];
    private bool _manifestLoaded;
    private bool _installPlanReady;
    private bool _installPlanHasErrors;
    private readonly LatestAsyncRequest _previewRequest = new();
    private readonly OwnedTaskObserver _tasks = new(nameof(UseStackWizardViewModel));
    private bool _disposed;

    public event Action<bool?>? CloseRequested;

    public event Func<IReadOnlyList<RegistryPackageMediaItemViewModel>, int, Task>? ImageGalleryRequested
    {
        add => _stackProfile.ImageGalleryRequested += value;
        remove => _stackProfile.ImageGalleryRequested -= value;
    }

    public string StackTitle { get; } = stack.Name;

    public string StackSubtitle { get; } = stack.StackId;

    public string StackSummary { get; } = string.IsNullOrWhiteSpace(stack.Summary) ? "No summary provided." : stack.Summary!;

    public string StackMetadata { get; } = $"{stack.PackageCount} package{StackDisplayFormatters.Plural(stack.PackageCount)} - {stack.FragmentCount} fragment{StackDisplayFormatters.Plural(stack.FragmentCount)}";

    public ObservableCollection<RegistryPackageMediaItemViewModel> StackProfileMedia => _stackProfile.Media;

    public ObservableStringBuilder StackReadmeMarkdownBuilder => _stackProfile.ReadmeMarkdownBuilder;

    public ObservableCollection<UseStackPackageReviewViewModel> PackageRows { get; } = [];

    public ObservableCollection<StackPackageInstallPlanItemViewModel> InstallPlanItems { get; } = [];

    public ObservableCollection<string> InstallPlanWarnings { get; } = [];

    public ObservableCollection<string> InstallPlanErrors { get; } = [];

    public ObservableCollection<UseStackSetupPackageGroupViewModel> SetupPackageGroups { get; } = [];

    public ObservableCollection<UseStackImportActionViewModel> ImportActions { get; } = [];

    public ObservableCollection<UseStackRequiredInputValueViewModel> RequiredInputs { get; } = [];

    public ObservableCollection<string> ImportWarnings { get; } = [];

    public ObservableCollection<string> ImportErrors { get; } = [];

    public ObservableCollection<string> ImportedItems { get; } = [];

    public bool CanApply => !IsBusy
                            && _manifestLoaded
                            && _installPlanReady
                            && !_installPlanHasErrors
                            && HasApplicableContent
                            && !HasMissingRequiredInputs
                            && !HasImportErrors;

    public bool HasPackages => PackageRows.Count > 0;

    public bool HasInstallPlanItems => InstallPlanItems.Count > 0;

    public bool HasInstallPlanWarnings => InstallPlanWarnings.Count > 0;

    public bool HasInstallPlanErrors => InstallPlanErrors.Count > 0;

    public bool ShowNoInstallPlanChanges => _installPlanReady && !HasInstallPlanItems && !HasInstallPlanErrors;

    public bool HasSetupPackageGroups => SetupPackageGroups.Count > 0;

    public bool HasSetupItems => SetupPackageGroups.Any(group => group.Items.Count > 0);

    public bool HasSelectedSetupItems => SetupPackageGroups.SelectMany(group => group.Items).Any(item => item.IsSelected);

    public bool HasPackageRequirements => _packageRequirements.Count > 0;

    public bool HasApplicableContent => HasPackageRequirements || HasSelectedSetupItems;

    public bool HasImportActions => ImportActions.Count > 0;

    public bool HasRequiredInputs => RequiredInputs.Count > 0;

    public bool HasMissingRequiredInputs => RequiredInputs.Any(input => input.IsMissingRequiredValue);

    public bool HasImportWarnings => ImportWarnings.Count > 0;

    public bool HasImportErrors => ImportErrors.Count > 0;

    public bool HasImportedItems => ImportedItems.Count > 0;

    public bool HasStackProfileMedia => _stackProfile.HasMedia;

    public bool HasStackReadme => _stackProfile.HasReadme;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Loading Stack setup plan...";

    partial void OnIsBusyChanged(bool value) => NotifyWizardStateChanged();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_manifestLoaded || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Reading Stack manifest...";
            var manifest = await library.ReadManifestAsync(stack.LocalPath, cancellationToken);
            var packageInfo = await LoadPackageInfoAsync(cancellationToken);
            ApplyManifest(manifest, packageInfo);
            await RefreshInstallPlanAsync(cancellationToken);
            if (!HasInstallPlanItems && !HasInstallPlanErrors)
            {
                await RefreshImportPreviewCoreAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _disposed)
        {
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
            IsBusy = false;
            NotifyWizardStateChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        var missingRequiredInput = RequiredInputs.FirstOrDefault(input => input.IsMissingRequiredValue);
        if (missingRequiredInput is not null)
        {
            StatusText = $"Enter a value for '{missingRequiredInput.Label}' before importing this Stack.";
            NotifyWizardStateChanged();
            return;
        }

        if (!HasApplicableContent)
        {
            StatusText = "Select at least one package or setup item before using this Stack.";
            NotifyWizardStateChanged();
            return;
        }

        if (HasInstallPlanItems)
        {
            if (!TryResolveRegistryUrl(out var registryUrl))
            {
                return;
            }

            IsBusy = true;
            try
            {
                using var registryClient = registryClientFactory(registryUrl);
                var installResult = await registryInstallService.InstallPackagesAsync(
                    _packageRequirements,
                    registryClient,
                    runtimeApiClient,
                    progress => StatusText = progress.StatusText);
                ApplyInstallResult(installResult);
                if (!installResult.Success)
                {
                    NotifyWizardStateChanged();
                    return;
                }

                if (installResult.ImpactedPackageIds.Count > 0)
                {
                    await applyPackageLifecycleChangesAsync(installResult.ImpactedPackageIds, _tasks.Token);
                }
            }
            catch (Exception ex)
            {
                InstallPlanErrors.Add(ex.Message);
                _installPlanHasErrors = true;
                StatusText = ex.Message;
                NotifyWizardStateChanged();
                return;
            }
            finally
            {
                IsBusy = false;
            }

            await RefreshInstallPlanAsync();
        }

        await RefreshImportPreviewCoreAsync(_tasks.Token);

        missingRequiredInput = RequiredInputs.FirstOrDefault(input => input.IsMissingRequiredValue);
        if (missingRequiredInput is not null)
        {
            StatusText = $"Enter a value for '{missingRequiredInput.Label}' before importing this Stack.";
            NotifyWizardStateChanged();
            return;
        }

        if (HasImportErrors)
        {
            StatusText = ImportErrors.FirstOrDefault() ?? "Resolve Stack setup preview errors before importing.";
            NotifyWizardStateChanged();
            return;
        }

        var selectedActionIds = ImportActions
            .Select(action => action.ActionId)
            .ToArray();

        if (selectedActionIds.Length == 0)
        {
            StatusText = "No Stack setup actions were needed.";
            CloseRequested?.Invoke(true);
            return;
        }

        IsBusy = true;
        ImportWarnings.Clear();
        ImportErrors.Clear();
        ImportedItems.Clear();
        try
        {
            StatusText = "Importing Stack setup...";
            var upload = await runtimeApiClient.UploadStackAsync(stack.LocalPath);
            var result = await runtimeApiClient.ImportStackAsync(new RuntimeStackImportRequest(
                upload.UploadId,
                GetSelectedFragmentIds(),
                GetInputValues(),
                new Dictionary<string, string>(),
                selectedActionIds));

            foreach (var warning in result.Warnings)
            {
                ImportWarnings.Add(warning);
            }

            foreach (var error in result.Errors)
            {
                ImportErrors.Add(error);
            }

            foreach (var imported in result.ImportedItems)
            {
                ImportedItems.Add($"{imported.DisplayName} ({imported.Kind})");
            }

            await NotifyStackImportAppliedAsync(result.AppliedContributions, _tasks.Token);

            StatusText = result.Success
                ? $"Imported {result.ImportedItems.Count} Stack setup item{StackDisplayFormatters.Plural(result.ImportedItems.Count)}."
                : result.Errors.FirstOrDefault() ?? "Stack setup import failed.";
            if (result.Success)
            {
                CloseRequested?.Invoke(true);
            }
        }
        catch (Exception ex)
        {
            ImportErrors.Add(ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NotifyWizardStateChanged();
        }
    }

    [RelayCommand]
    private void Cancel()
        => CloseRequested?.Invoke(false);

}
