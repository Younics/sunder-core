using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Services;

namespace Sunder.App.ViewModels;

public sealed partial class PackageOperationPresentationViewModel : ViewModelBase
{
    private readonly PackageOperationStatePresenter _statePresenter;
    private readonly PackageWarningsViewModel _warnings = new();
    private int _busyLeaseCount;

    internal PackageOperationPresentationViewModel(IPackageOperationExecutor operationExecutor)
    {
        _statePresenter = new PackageOperationStatePresenter(operationExecutor);
    }

    public ObservableCollection<string> WarningLines => _warnings.Lines;

    public bool HasWarnings => _warnings.HasWarnings;

    public bool HasActivePackageStoreOperation => _statePresenter.HasActivePackageStoreOperation;

    public bool ShowSelectedPackageOperationStatus => SelectedPackageHasActiveOperation;

    public bool ShowCancelSelectedPackageOperation
        => SelectedPackageHasActiveOperation && SelectedPackageOperationCanCancel;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Search the marketplace or inspect installed packages.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSelectedPackageOperationStatus))]
    [NotifyPropertyChangedFor(nameof(ShowCancelSelectedPackageOperation))]
    private bool _selectedPackageHasActiveOperation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCancelSelectedPackageOperation))]
    private bool _selectedPackageOperationCanCancel;

    [ObservableProperty]
    private bool _selectedPackageOperationIsIndeterminate = true;

    [ObservableProperty]
    private double _selectedPackageOperationProgressPercent;

    [ObservableProperty]
    private string _selectedPackageOperationStatusText = string.Empty;

    public IDisposable EnterBusy()
    {
        _busyLeaseCount++;
        IsBusy = true;
        return new BusyLease(this);
    }

    internal void RefreshRows(
        IEnumerable<RegistryPackageSearchItemViewModel> marketplacePackages,
        IEnumerable<PackageCatalogItemViewModel> installedPackages)
        => _statePresenter.RefreshPackageRows(marketplacePackages, installedPackages);

    internal void ApplySelectedPackage(string? packageId)
    {
        var state = _statePresenter.GetSelectedPackageState(packageId);
        SelectedPackageHasActiveOperation = state.HasActiveOperation;
        SelectedPackageOperationCanCancel = state.CanCancel;
        SelectedPackageOperationIsIndeterminate = state.IsIndeterminate;
        SelectedPackageOperationProgressPercent = state.ProgressPercent;
        SelectedPackageOperationStatusText = state.StatusText;
    }

    internal bool CancelActiveOperationForPackage(string? packageId)
        => _statePresenter.CancelActiveOperationForPackage(packageId);

    internal void ClearWarnings()
    {
        _warnings.Clear();
        OnPropertyChanged(nameof(HasWarnings));
    }

    internal void AddWarning(string warning)
    {
        _warnings.Add(warning);
        OnPropertyChanged(nameof(HasWarnings));
    }

    private void ReleaseBusy()
    {
        if (_busyLeaseCount > 0)
        {
            _busyLeaseCount--;
        }

        IsBusy = _busyLeaseCount > 0;
    }

    private sealed class BusyLease(PackageOperationPresentationViewModel owner) : IDisposable
    {
        private PackageOperationPresentationViewModel? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.ReleaseBusy();
    }
}
