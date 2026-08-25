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
    IRuntimeStacksClient runtimeApiClient,
    RegistryPackageInstallService registryInstallService,
    Func<RuntimePackageStamp, CancellationToken, Task> waitUntilPresentationAppliedAsync,
    Func<Uri, IRegistryApiClient> registryClientFactory,
    string registryUrlText) : ViewModelBase, IDisposable, IAsyncDisposable
{
    private readonly MarketplacePackageProfileViewModel _stackProfile = BuildStackProfile(stack);
    private readonly object _lifecycleGate = new();
    private IReadOnlyList<SunderStackPackageRequirement> _packageRequirements = [];
    private bool _manifestLoaded;
    private bool _installPlanReady;
    private bool _installPlanHasErrors;
    private readonly LatestAsyncRequest _previewRequest = new();
    private readonly OwnedTaskObserver _tasks = new(nameof(UseStackWizardViewModel));
    private string? _importPlanId;
    private TaskCompletionSource? _planOperationsDrained;
    private Task? _cleanupTask;
    private int _activePlanOperations;
    private volatile bool _disposed;

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
        if (_disposed || _manifestLoaded || IsBusy)
        {
            return;
        }

        using var lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _tasks.Token);
        cancellationToken = lifetimeCancellation.Token;
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
            if (!_disposed)
            {
                IsBusy = false;
                NotifyWizardStateChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        using var planOperation = TryBeginPlanOperation();
        if (planOperation is null)
        {
            return;
        }

        var cancellationToken = _tasks.Token;
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
                    progress => StatusText = progress.StatusText,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ApplyInstallResult(installResult);
                if (!installResult.Success)
                {
                    NotifyWizardStateChanged();
                    return;
                }

                if (installResult.ImpactedPackageIds.Count > 0 && installResult.CommittedStamp is not null)
                {
                    await waitUntilPresentationAppliedAsync(installResult.CommittedStamp, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
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
                if (!_disposed)
                {
                    IsBusy = false;
                }
            }

            await RefreshInstallPlanAsync(cancellationToken);
        }

        await RefreshImportPreviewCoreAsync(cancellationToken);
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return;
        }

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
            .Where(action => action.IsSelected)
            .Select(action => action.ActionId)
            .ToArray();

        if (selectedActionIds.Length == 0)
        {
            await TryDiscardImportPlanAsync(DetachCurrentImportPlan());
            StatusText = "No Stack setup actions were needed.";
            CloseRequested?.Invoke(true);
            return;
        }
        var importPlanId = DetachCurrentImportPlan();
        if (string.IsNullOrWhiteSpace(importPlanId))
        {
            StatusText = "Stack setup preview did not return an import plan.";
            NotifyWizardStateChanged();
            return;
        }

        IsBusy = true;
        ImportWarnings.Clear();
        ImportErrors.Clear();
        ImportedItems.Clear();
        var closeAfterImport = false;
        try
        {
            StatusText = "Importing Stack setup...";
            var result = await runtimeApiClient.ImportStackAsync(new RuntimeStackImportRequest(
                importPlanId,
                GetSelectedFragmentIds(),
                selectedActionIds), cancellationToken);
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

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

            StatusText = result.Outcome switch
            {
                RuntimeStackImportOutcome.Completed => $"Imported {result.ImportedItems.Count} Stack setup item{StackDisplayFormatters.Plural(result.ImportedItems.Count)}.",
                RuntimeStackImportOutcome.Partial => $"Partially imported {result.ImportedItems.Count} Stack setup item{StackDisplayFormatters.Plural(result.ImportedItems.Count)}; review contributor errors.",
                _ => result.Errors.FirstOrDefault() ?? "Stack setup import failed.",
            };
            if (result.Outcome == RuntimeStackImportOutcome.Completed)
            {
                closeAfterImport = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ImportErrors.Add(ex.Message);
            StatusText = ex.Message;
        }
        finally
        {
            await TryDiscardImportPlanAsync(importPlanId);
            if (!_disposed)
            {
                IsBusy = false;
                NotifyWizardStateChanged();
            }
        }

        if (closeAfterImport && !_disposed)
        {
            CloseRequested?.Invoke(true);
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        using var planOperation = TryBeginPlanOperation();
        if (planOperation is null)
        {
            return;
        }

        _previewRequest.Invalidate();
        await TryDiscardImportPlanAsync(DetachCurrentImportPlan());
        if (!_disposed)
        {
            CloseRequested?.Invoke(false);
        }
    }

}
