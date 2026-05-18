using Sunder.App.Features.Shell.State;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.App.ViewModels;

namespace Sunder.App.Features.Shell.Panels;

internal sealed class ShellPanelContentPresenter(
    PackageViewHostService packageViewHostService,
    IReadOnlyList<string> startupWarnings,
    IReadOnlyList<string> startupErrors)
{
    public void Apply(
        ShellPanelViewModel panel,
        RailPlacement placement,
        string? viewId,
        IReadOnlyDictionary<string, ShellPackageView> viewsById,
        int middleBarItemCount,
        bool createHostedView)
    {
        panel.Lines.Clear();

        if (string.IsNullOrWhiteSpace(viewId) || !viewsById.TryGetValue(viewId, out var packageView))
        {
            ApplyEmptyPanelState(placement, panel, middleBarItemCount);
            return;
        }

        panel.Title = packageView.Title.ToUpperInvariant();
        panel.Subtitle = $"{packageView.PackageDisplayName} · {packageView.PackageId} · v{packageView.PackageVersion}";
        panel.Summary = packageView.Readiness == Sunder.Protocol.PackageReadinessState.Ready
            ? $"{ShellPlacementCatalog.ToDisplayName(placement).ToUpperInvariant()} PACKAGE ACTIVE"
            : $"PACKAGE {packageView.Readiness.ToString().ToUpperInvariant()}";
        AddCommonViewLines(panel.Lines, packageView);
        if (!createHostedView)
        {
            panel.SetActiveView(viewId, hostedView: null);
            panel.Lines.Add("Package view will open after the shell finishes rendering.");
            return;
        }

        panel.SetActiveView(viewId, packageViewHostService.GetOrCreateView(viewId));

        if (placement != RailPlacement.Middle)
        {
            return;
        }

        AddStartupLines(panel.Lines, noIssuesMessage: "Runtime composition completed without package load warnings.");
    }

    private void ApplyEmptyPanelState(RailPlacement placement, ShellPanelViewModel panel, int middleBarItemCount)
    {
        panel.ClearActiveView();

        if (placement == RailPlacement.Middle)
        {
            var hasMiddlePackages = middleBarItemCount > 0;
            panel.Title = "WELCOME";
            panel.Subtitle = hasMiddlePackages
                ? "Middle package views are available."
                : "No packages are currently assigned to the middle bar.";
            panel.Summary = hasMiddlePackages
                ? "Click a package icon in the middle bar to open it here."
                : "Install a package or move one into the middle bar to make this workspace active.";

            AddStartupLines(
                panel.Lines,
                hasMiddlePackages
                    ? "The middle workspace is ready. Select a package icon to reopen a view."
                    : "Load a dev package or move a package view into the middle bar to get started.");
            return;
        }

        panel.Title = ShellPlacementCatalog.ToDisplayName(placement).ToUpperInvariant();
        panel.Subtitle = string.Empty;
        panel.Summary = $"Select a package icon to open the {ShellPlacementCatalog.ToDisplayName(placement)} panel.";
        panel.Lines.Add($"No package is currently open in {ShellPlacementCatalog.ToDisplayName(placement)}.");
    }

    private void AddStartupLines(ICollection<string> lines, string noIssuesMessage)
    {
        if (startupWarnings.Count == 0 && startupErrors.Count == 0)
        {
            lines.Add(noIssuesMessage);
            return;
        }

        foreach (var warning in startupWarnings)
        {
            lines.Add($"Warning: {warning}");
        }

        foreach (var error in startupErrors)
        {
            lines.Add($"Error: {error}");
        }
    }

    private static void AddCommonViewLines(ICollection<string> lines, ShellPackageView packageView)
    {
        lines.Add($"Package id: {packageView.PackageId}");
        lines.Add($"Version: {packageView.PackageVersion}");
        lines.Add($"Placement: {ShellPlacementCatalog.ToDisplayName(packageView.Placement)}");
        lines.Add($"Readiness: {ShellPlacementCatalog.ToReadinessDisplay(packageView.Readiness)}");
    }
}
