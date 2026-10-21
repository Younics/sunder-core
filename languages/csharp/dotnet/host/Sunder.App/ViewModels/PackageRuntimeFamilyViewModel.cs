using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.App.Services;
using Sunder.Runtime.Contracts;

namespace Sunder.App.ViewModels;

internal enum PackageRuntimeFamilyAction { None, Install, Update, Repair }

public sealed partial class PackageRuntimeFamilyViewModel : ViewModelBase
{
    internal PackageRuntimeFamilyViewModel(ConfiguredOfficialSourceId source, string title, string description)
    {
        Source = source;
        Title = title;
        Description = description;
    }

    public ConfiguredOfficialSourceId Source { get; }
    public string Title { get; }
    public string Description { get; }
    public string Glyph => Source == ConfiguredOfficialSourceId.DotNet ? ".NET" : "JS";
    internal InstalledPackageRuntimeItem? Installed { get; private set; }
    internal AvailablePackageRuntimeItem? Available { get; private set; }
    internal PreparedPackageRuntimeItem? Prepared { get; private set; }
    internal PackageRuntimeFamilyAction Action { get; private set; }
    private bool _inventoryLoaded;
    private string? _catalogError;
    private string? _operationText;
    private string? _operationError;

    [ObservableProperty] private string _status = "Checking…";
    [ObservableProperty] private string _versionText = string.Empty;
    [ObservableProperty] private string _usageText = string.Empty;
    [ObservableProperty] private string _details = string.Empty;
    [ObservableProperty] private string _primaryActionText = string.Empty;
    [ObservableProperty] private string _issueText = string.Empty;
    [ObservableProperty] private bool _hasPrimaryAction;
    [ObservableProperty] private bool _hasPreparedUpdate;
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private bool _canRemove;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private double? _progressPercent;

    public bool HasIssue => !string.IsNullOrWhiteSpace(IssueText);
    public bool HasUsageText => !string.IsNullOrWhiteSpace(UsageText);
    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public bool IsProgressIndeterminate => ProgressPercent is null;
    public double ProgressValue => ProgressPercent ?? 0;
    partial void OnIssueTextChanged(string value) => OnPropertyChanged(nameof(HasIssue));
    partial void OnUsageTextChanged(string value) => OnPropertyChanged(nameof(HasUsageText));
    partial void OnDetailsChanged(string value) => OnPropertyChanged(nameof(HasDetails));
    partial void OnProgressPercentChanged(double? value)
    {
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(ProgressValue));
    }

    internal void Apply(InstalledPackageRuntimeItem? installed, AvailablePackageRuntimeItem? available,
        PreparedPackageRuntimeItem? prepared, bool inventoryLoaded, string? catalogError)
    {
        Installed = installed;
        Available = available;
        Prepared = prepared;
        _inventoryLoaded = inventoryLoaded;
        _catalogError = catalogError;
        RefreshPresentation();
    }

    internal void SetOperation(string? text, double? progress = null, string? error = null)
    {
        _operationText = text;
        _operationError = error;
        IsWorking = text is not null;
        ProgressPercent = progress;
        RefreshPresentation();
    }

    private void RefreshPresentation()
    {
        IsInstalled = Installed is not null;
        CanRemove = Installed?.CanRemove == true && !IsWorking;
        HasPreparedUpdate = Prepared?.CanApply == true && !IsWorking;
        VersionText = Installed is { } installed
            ? $"Version {installed.Runtime.Identity.VendorVersion} · {installed.Runtime.Support.SupportLine}"
            : Available is { } available ? $"Latest LTS · {available.Entry.Artifact.Identity.VendorVersion}" : "Latest supported LTS";
        UsageText = Installed is null ? Description : string.Empty;
        Details = Installed?.Details ?? Available?.Details ?? string.Empty;
        IssueText = _operationError ?? _catalogError ?? string.Empty;
        Action = PackageRuntimeFamilyAction.None;

        if (IsWorking) Status = _operationText!;
        else if (!_inventoryLoaded) Status = "Status unavailable";
        else if (Installed is null)
        {
            Status = "Not installed";
            if (Available?.Entry.Artifact.Support.State == RuntimeExecutionRuntimeSupportState.Supported)
                Action = PackageRuntimeFamilyAction.Install;
        }
        else if (Installed.Runtime.Status != RuntimeExecutionRuntimeStatuses.Ready || Installed.Runtime.Health.RepairRecommended)
        {
            Status = "Needs repair";
            IssueText = Installed.Health;
            Action = PackageRuntimeFamilyAction.Repair;
        }
        else if (HasPreparedUpdate)
        {
            Status = "Update downloaded";
            UsageText = "Waiting for packages to stop. Apply safely, or restart them when you are ready.";
        }
        else if (Prepared?.Update.State == RuntimeExecutionRuntimeUpdateState.ReconciliationPending)
        {
            Status = "Finishing update";
            UsageText = "Runtime changes are waiting for the running session to catch up.";
        }
        else if (Available is { } latest && latest.Entry.Artifact.Support.State == RuntimeExecutionRuntimeSupportState.Supported
            && CompareVersions(latest.Entry.Artifact.Identity.VendorVersion, Installed.Runtime.Identity.VendorVersion) > 0)
        {
            Status = "Update available";
            VersionText = $"{Installed.Runtime.Identity.VendorVersion} → {latest.Entry.Artifact.Identity.VendorVersion} · LTS";
            Action = PackageRuntimeFamilyAction.Update;
        }
        else Status = Available?.Entry.Artifact.Support.State == RuntimeExecutionRuntimeSupportState.Supported
            && _catalogError is null ? "Up to date" : "Installed";

        if (Prepared?.Update.State is RuntimeExecutionRuntimeUpdateState.Error or RuntimeExecutionRuntimeUpdateState.Blocked or RuntimeExecutionRuntimeUpdateState.Stale)
            IssueText = Prepared.Status;
        PrimaryActionText = Action switch
        {
            PackageRuntimeFamilyAction.Install => "Install",
            PackageRuntimeFamilyAction.Update => "Download & Install",
            PackageRuntimeFamilyAction.Repair => "Repair",
            _ => string.Empty,
        };
        HasPrimaryAction = Action != PackageRuntimeFamilyAction.None && !IsWorking;
    }

    internal static int CompareVersions(string left, string right)
        => Version.TryParse(left, out var first) && Version.TryParse(right, out var second) ? first.CompareTo(second) : 0;
}
