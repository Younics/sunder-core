using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Protocol;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

public sealed partial class PackageCatalogItemViewModel : PackageIconItemViewModel, IPackageOperationStateViewModel
{
    private readonly Action<PackageCatalogItemViewModel> _onSelect;

    public PackageCatalogItemViewModel(
        SessionPackageDescriptor? sessionPackage,
        InstalledPackageDescriptor? installedPackage,
        RegistryPackageUpdate? update,
        Uri? iconUri,
        Action<PackageCatalogItemViewModel> onSelect)
        : base(iconUri)
    {
        PackageId = sessionPackage?.PackageId ?? installedPackage!.PackageId;
        DisplayName = sessionPackage?.DisplayName ?? installedPackage!.Name;
        Version = sessionPackage?.Version ?? installedPackage!.Version;
        Glyph = ToGlyph(sessionPackage?.Icon ?? installedPackage?.Icon, DisplayName);
        StatusText = ToStatusText(sessionPackage, installedPackage);
        IsEnabled = sessionPackage?.IsEnabled ?? installedPackage?.IsEnabled ?? false;
        IsFailed = sessionPackage?.Readiness == PackageReadinessState.Failed;
        IsInstalled = installedPackage is not null;
        SourceLabel = installedPackage is null ? "Dev package" : "Installed package";
        ViewCount = sessionPackage?.Views.Count ?? 0;
        LastError = sessionPackage?.LastError;
        FailureOrigin = sessionPackage?.FailureOrigin;
        CanEnable = installedPackage is { IsEnabled: false };
        CanDisable = installedPackage is { IsEnabled: true };
        CanUninstall = installedPackage is not null;
        AvailableVersion = update?.AvailableVersion;
        DeprecatedUpdateMessage = update?.DeprecatedMessage;
        OperationHint = ToOperationHint(installedPackage, update);
        _onSelect = onSelect;
    }

    public string PackageId { get; }

    public string DisplayName { get; }

    public string Version { get; }

    public string Glyph { get; }

    public bool ShowOperationStatus => HasActiveOperation;

    public string StatusText { get; }

    public bool IsEnabled { get; }

    public bool IsFailed { get; }

    public bool IsInstalled { get; }

    public string SourceLabel { get; }

    public int ViewCount { get; }

    public string? LastError { get; }

    public PackageFailureOrigin? FailureOrigin { get; }

    public bool CanEnable { get; }

    public bool CanDisable { get; }

    public bool CanUninstall { get; }

    public string? AvailableVersion { get; }

    public string? DeprecatedUpdateMessage { get; }

    public bool HasUpdate => !string.IsNullOrWhiteSpace(AvailableVersion);

    public string UpdateText => HasUpdate ? $"Update {AvailableVersion}" : string.Empty;

    public string OperationHint { get; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _hasActiveOperation;

    [ObservableProperty]
    private bool _operationCanCancel;

    [ObservableProperty]
    private bool _operationIsIndeterminate = true;

    [ObservableProperty]
    private double _operationProgressPercent;

    [ObservableProperty]
    private string _operationStatusText = string.Empty;

    partial void OnHasActiveOperationChanged(bool value)
        => OnPropertyChanged(nameof(ShowOperationStatus));

    [RelayCommand]
    private void Select() => _onSelect(this);

    private static string ToGlyph(PackageIconDescriptor? icon, string displayName)
    {
        if (!string.IsNullOrWhiteSpace(icon?.Glyph))
        {
            return icon.Glyph!;
        }

        return string.IsNullOrWhiteSpace(displayName)
            ? "?"
            : displayName[0].ToString().ToUpperInvariant();
    }

    private static string ToStatusText(SessionPackageDescriptor? sessionPackage, InstalledPackageDescriptor? installedPackage)
    {
        if (sessionPackage is not null)
        {
            if (sessionPackage.IsEnabled)
            {
                return sessionPackage.Readiness switch
                {
                    PackageReadinessState.Ready => "Ready",
                    PackageReadinessState.NeedsConfiguration => "Needs configuration",
                    PackageReadinessState.Degraded => "Degraded",
                    PackageReadinessState.Failed => "Failed",
                    _ => "Unknown",
                };
            }

            if (sessionPackage.Readiness == PackageReadinessState.Disabled)
            {
                return "Disabled";
            }

            var origin = sessionPackage.FailureOrigin switch
            {
                PackageFailureOrigin.AppActivation => "app activation",
                PackageFailureOrigin.AppHostedView => "hosted view",
                PackageFailureOrigin.AppUnhandledUi => "UI interaction",
                PackageFailureOrigin.RuntimeActivation => "runtime activation",
                PackageFailureOrigin.RuntimeConfiguration => "runtime configuration",
                PackageFailureOrigin.RuntimeAuthentication => "runtime auth",
                _ => "package fault",
            };

            return $"Failed · {origin}";
        }

        if (installedPackage is not null)
        {
            return installedPackage.IsEnabled ? "Installed" : "Disabled";
        }

        return "Unknown";
    }

    private static string ToOperationHint(InstalledPackageDescriptor? installedPackage, RegistryPackageUpdate? update)
    {
        if (installedPackage is null)
        {
            return "Dev packages are managed by the current app launch arguments.";
        }

        if (update is not null)
        {
            return $"Update available: {update.CurrentVersion} -> {update.AvailableVersion}.";
        }

        return installedPackage.IsEnabled
            ? "Disable or uninstall this package. Running shell changes apply live when possible."
            : "Enable or uninstall this package. Running shell changes apply live when possible.";
    }
}
