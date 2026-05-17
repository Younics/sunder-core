using Avalonia.Media;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.ViewModels;

internal sealed record PackageSelectionDetails(
    string Title,
    string Subtitle,
    string Status,
    string Summary,
    string Glyph,
    IImage? IconImage,
    string IconLoadError,
    bool HasError,
    string Error,
    string OperationHint,
    string MarketplaceLatestVersion,
    string MarketplaceInstalledVersion,
    string MarketplaceSelectedVersion)
{
    public static PackageSelectionDetails FromInstalled(PackageCatalogItemViewModel item)
        => new(
            item.DisplayName,
            $"{item.PackageId} · v{item.Version} · {item.SourceLabel}",
            item.StatusText,
            item.HasUpdate
                ? $"Views: {item.ViewCount} · State: {item.StatusText} · Update available: {item.AvailableVersion}"
                : $"Views: {item.ViewCount} · State: {item.StatusText}",
            item.Glyph,
            item.IconImage,
            item.IconLoadError ?? string.Empty,
            !string.IsNullOrWhiteSpace(item.LastError),
            item.LastError ?? string.Empty,
            item.OperationHint,
            MarketplaceLatestVersion: "-",
            MarketplaceInstalledVersion: "Not installed",
            MarketplaceSelectedVersion: "Latest");

    public static PackageSelectionDetails NoInstalledMatch()
        => new(
            "No package selected",
            "No installed packages match the current filter.",
            string.Empty,
            "Adjust the filter or install packages to inspect session status.",
            "?",
            IconImage: null,
            IconLoadError: string.Empty,
            HasError: false,
            Error: string.Empty,
            "Install a .sunderpkg from disk or use the marketplace tab.",
            MarketplaceLatestVersion: "-",
            MarketplaceInstalledVersion: "Not installed",
            MarketplaceSelectedVersion: "Latest");

    public static PackageSelectionDetails FromMarketplace(RegistryPackageSearchItemViewModel item)
        => new(
            item.Name,
            item.PackageId,
            string.Empty,
            item.Summary ?? "No package summary provided.",
            item.Glyph,
            item.IconImage,
            item.IconLoadError ?? string.Empty,
            HasError: false,
            Error: string.Empty,
            OperationHint: string.Empty,
            item.LatestVersion ?? "-",
            item.InstalledVersion ?? "Not installed",
            MarketplaceSelectedVersion: "Latest");

    public static PackageSelectionDetails NoMarketplaceMatch()
        => new(
            "No package selected",
            "No marketplace packages match the current search.",
            string.Empty,
            "Adjust the search or registry URL to browse packages.",
            "?",
            IconImage: null,
            IconLoadError: string.Empty,
            HasError: false,
            Error: string.Empty,
            OperationHint: string.Empty,
            MarketplaceLatestVersion: "-",
            MarketplaceInstalledVersion: "Not installed",
            MarketplaceSelectedVersion: "Latest");
}

internal sealed record SelectedPackageIconState(string Glyph, IImage? IconImage, string IconLoadError)
{
    public static SelectedPackageIconState Empty { get; } = new("?", null, string.Empty);
}

internal sealed record SelectedPackageOperationState(
    bool HasActiveOperation,
    bool CanCancel,
    bool IsIndeterminate,
    double ProgressPercent,
    string StatusText)
{
    public static SelectedPackageOperationState FromSnapshot(BackgroundProcessSnapshot? operation)
        => new(
            operation?.IsActive == true,
            operation is { CanCancel: true, State: not BackgroundProcessState.Cancelling },
            operation?.ProgressPercent is null,
            operation?.ProgressPercent ?? 0,
            operation is null ? string.Empty : PackageOperationStateProjector.FormatStatus(operation));
}
