using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;

namespace Sunder.App.ViewModels;

public sealed partial class PackageCatalogItemViewModel : PackageIconItemViewModel, IPackageOperationStateViewModel
{
    private readonly Action<PackageCatalogItemViewModel> _onSelect;
    private readonly PackageCatalogItemState _state;

    public PackageCatalogItemViewModel(
        SessionPackageDescriptor? sessionPackage,
        InstalledPackageDescriptor? installedPackage,
        RegistryPackageUpdate? update,
        Uri? iconUri,
        Action<PackageCatalogItemViewModel> onSelect)
        : this(CreateState(sessionPackage, installedPackage, update, iconUri), onSelect)
    {
    }

    internal PackageCatalogItemViewModel(
        PackageCatalogItemState state,
        Action<PackageCatalogItemViewModel> onSelect)
        : base(state.IconUri)
    {
        _state = state;
        PackageId = state.PackageId;
        DisplayName = state.DisplayName;
        Version = state.Version;
        Glyph = state.Glyph;
        StatusText = state.StatusText;
        IsEnabled = state.IsEnabled;
        IsFailed = state.IsFailed;
        IsInstalled = state.IsInstalled;
        SourceLabel = state.SourceLabel;
        ViewCount = state.ViewCount;
        LastError = state.LastError;
        FailureOrigin = state.FailureOrigin;
        CanEnable = state.CanEnable;
        CanDisable = state.CanDisable;
        CanUninstall = state.CanUninstall;
        AvailableVersion = state.AvailableVersion;
        DeprecatedUpdateMessage = state.DeprecatedUpdateMessage;
        OperationHint = state.OperationHint;
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

    internal bool HasState(PackageCatalogItemState state) => _state == state;

    internal static PackageCatalogItemState CreateState(
        SessionPackageDescriptor? sessionPackage,
        InstalledPackageDescriptor? installedPackage,
        RegistryPackageUpdate? update,
        Uri? iconUri)
    {
        var packageId = sessionPackage?.PackageId ?? installedPackage!.PackageId;
        var displayName = sessionPackage?.DisplayName ?? installedPackage!.Name;
        return new PackageCatalogItemState(
            packageId,
            displayName,
            sessionPackage?.Version ?? installedPackage!.Version,
            ToGlyph(sessionPackage?.Icon ?? installedPackage?.Icon, displayName),
            ToStatusText(sessionPackage, installedPackage),
            sessionPackage?.IsEnabled ?? installedPackage?.IsEnabled ?? false,
            sessionPackage?.Readiness == PackageReadinessState.Failed,
            installedPackage is not null,
            installedPackage is null ? "Dev package" : "Installed package",
            sessionPackage?.Views.Count ?? 0,
            sessionPackage?.LastError,
            sessionPackage?.FailureOrigin,
            installedPackage is { IsEnabled: false },
            installedPackage is { IsEnabled: true },
            installedPackage is not null,
            update?.AvailableVersion,
            update?.DeprecatedMessage,
            ToOperationHint(installedPackage, update),
            iconUri);
    }

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

internal sealed record PackageCatalogItemState(
    string PackageId,
    string DisplayName,
    string Version,
    string Glyph,
    string StatusText,
    bool IsEnabled,
    bool IsFailed,
    bool IsInstalled,
    string SourceLabel,
    int ViewCount,
    string? LastError,
    PackageFailureOrigin? FailureOrigin,
    bool CanEnable,
    bool CanDisable,
    bool CanUninstall,
    string? AvailableVersion,
    string? DeprecatedUpdateMessage,
    string OperationHint,
    Uri? IconUri);
