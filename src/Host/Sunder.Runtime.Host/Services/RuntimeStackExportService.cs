using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeStackExportService
{
    private readonly RuntimeSessionOwner _sessions;
    private readonly StackExportArchiveBuilder _archiveBuilder;

    public RuntimeStackExportService(
        RuntimeSessionOwner sessions,
        RuntimeContentTransferStore transfers,
        RuntimePackagePaths paths,
        TimeProvider? timeProvider = null)
    {
        _sessions = sessions;
        _archiveBuilder = new StackExportArchiveBuilder(transfers, paths, timeProvider ?? TimeProvider.System);
    }

    public async Task<RuntimeStackExportDiscoveryResponse> ListItemsAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _sessions.State.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var items = new List<RuntimeStackExportItemDescriptor>();
        var errors = new List<string>();
        foreach (var contribution in _sessions.State.GetExtensionContributions(lease, SunderStackExtensionPoints.StackContributors))
        {
            try
            {
                var discovered = await contribution.Contribution.ListExportItemsAsync(new StackExportDiscoveryContext(contribution.PackageId), linked.Token);
                items.AddRange(discovered.Select(item => RuntimeStackContractMapper.ToExportItem(contribution.PackageId, contribution.Contribution.ContributorId, item)));
            }
            catch (Exception) when (!linked.IsCancellationRequested)
            {
                errors.Add($"Stack contributor '{contribution.Contribution.ContributorId}' export discovery failed.");
            }
        }
        return new RuntimeStackExportDiscoveryResponse(items, [], errors);
    }

    public async Task<RuntimeStackExportResponse> ExportAsync(RuntimeStackExportRequest request, CancellationToken cancellationToken = default)
    {
        using var lease = _sessions.State.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        if (string.IsNullOrWhiteSpace(request.StackId)) return Failed("Stack id is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) return Failed("Stack name is required.");
        var packageRequirements = BuildSelectedPackageRequirements(lease, request.SelectedPackages ?? []).ToList();
        if (request.SelectedItems.Count == 0 && packageRequirements.Count == 0) return Failed("Select at least one package or setup item to export.");

        var contributors = GetContributors(lease);
        var fragments = new List<StackOwnedFragment>();
        var previews = new Dictionary<string, SunderStackFragmentPreview>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var errors = new List<string>();
        foreach (var group in request.SelectedItems.GroupBy(item => Key(item.OwnerPackageId, item.ContributorId), StringComparer.OrdinalIgnoreCase))
        {
            var selected = group.First();
            if (string.IsNullOrWhiteSpace(selected.OwnerPackageId) || !contributors.TryGetValue(group.Key, out var registration))
            {
                errors.Add($"No active Stack contributor '{selected.ContributorId}' from package '{selected.OwnerPackageId}' is available for export.");
                continue;
            }
            try
            {
                var selections = group.GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                    .Select(itemGroup => new StackExportItemSelection(
                        itemGroup.Key,
                        itemGroup.SelectMany(item => item.Details ?? []).Select(detail => new StackExportDetailSelection(
                            detail.DetailId,
                            detail.IsSelected,
                            detail.ValueOverride,
                            Enum.TryParse<StackValueSensitivity>(detail.SensitivityOverride, true, out var sensitivity) ? sensitivity : null)).ToArray()))
                    .ToArray();
                var discovered = await registration.Contributor.ListExportItemsAsync(new StackExportDiscoveryContext(registration.PackageId), linked.Token);
                var contribution = await registration.Contributor.ExportAsync(new StackExportRequest(selections.Select(item => item.ItemId).ToArray(), selections), linked.Token);
                packageRequirements.Add(BuildPackageRequirement(lease, registration.PackageId));
                foreach (var fragment in contribution.Fragments)
                {
                    fragments.Add(new StackOwnedFragment(registration.PackageId, fragment));
                    var sourceItemId = string.IsNullOrWhiteSpace(fragment.SourceItemId) && selections.Length == 1 && contribution.Fragments.Count == 1
                        ? selections[0].ItemId
                        : fragment.SourceItemId;
                    if (!string.IsNullOrWhiteSpace(sourceItemId))
                    {
                        var item = discovered.FirstOrDefault(value => string.Equals(value.ItemId, sourceItemId, StringComparison.OrdinalIgnoreCase));
                        if (item is not null) previews[fragment.FragmentId] = BuildPreview(sourceItemId, item, selections.First(value => string.Equals(value.ItemId, sourceItemId, StringComparison.OrdinalIgnoreCase)));
                    }
                }
                packageRequirements.AddRange(contribution.PackageRequirements);
                warnings.AddRange(contribution.Warnings);
            }
            catch (Exception) when (!linked.IsCancellationRequested)
            {
                errors.Add($"Stack contributor '{registration.Contributor.ContributorId}' export failed.");
            }
        }
        if (errors.Count > 0) return new RuntimeStackExportResponse(false, null, warnings, errors);
        if (fragments.Count == 0 && packageRequirements.Count == 0) return new RuntimeStackExportResponse(false, null, warnings, ["Selected setup items did not produce Stack content."]);

        return await _archiveBuilder.BuildAsync(request, packageRequirements, fragments, previews, lease.Generation, warnings, linked.Token);
    }

    private Dictionary<string, ContributorRegistration> GetContributors(PackageSessionLease lease)
        => _sessions.State.GetExtensionContributions(lease, SunderStackExtensionPoints.StackContributors)
            .Where(value => !string.IsNullOrWhiteSpace(value.PackageId) && !string.IsNullOrWhiteSpace(value.Contribution.ContributorId))
            .GroupBy(value => Key(value.PackageId, value.Contribution.ContributorId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new ContributorRegistration(group.First().PackageId, group.First().Contribution), StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<StackPackageRequirement> BuildSelectedPackageRequirements(
        PackageSessionLease lease,
        IReadOnlyList<string> packageIds)
        => packageIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(packageId => new StackPackageRequirement(packageId, CreatedWithVersion: _sessions.State.GetLoadedPackage(lease, packageId)?.Descriptor.Version)).ToArray();

    private StackPackageRequirement BuildPackageRequirement(PackageSessionLease lease, string packageId)
        => new(packageId, CreatedWithVersion: _sessions.State.GetLoadedPackage(lease, packageId)?.Descriptor.Version, MinimumVersion: "1.0.0");

    private static SunderStackFragmentPreview BuildPreview(string sourceItemId, StackExportItemDescriptor item, StackExportItemSelection selection)
    {
        var selected = (selection.Details ?? []).ToDictionary(value => value.DetailId, StringComparer.OrdinalIgnoreCase);
        var details = new List<SunderStackFragmentDisplayDetail>();
        foreach (var detail in item.Details ?? [])
        {
            var id = string.IsNullOrWhiteSpace(detail.DetailId) ? RuntimeStackContractMapper.BuildDetailId(detail.Label) : detail.DetailId;
            if (selection.Details is not null && (!selected.TryGetValue(id!, out var choice) || !choice.IsSelected)) continue;
            selected.TryGetValue(id!, out var selectedDetail);
            var sensitivity = selectedDetail?.SensitivityOverride ?? detail.Sensitivity;
            var asksOnImport = sensitivity == StackValueSensitivity.Secret;
            var value = asksOnImport ? "Importer will provide this value." : selectedDetail?.ValueOverride ?? detail.Value;
            if (!string.IsNullOrWhiteSpace(detail.Label) && !string.IsNullOrWhiteSpace(value)) details.Add(new SunderStackFragmentDisplayDetail { Label = detail.Label, Value = value, Behavior = asksOnImport ? "Ask on import" : "Include value" });
        }
        return new SunderStackFragmentPreview { SourceItemId = sourceItemId, Kind = item.Kind, DisplayDetails = details };
    }

    private static RuntimeStackExportResponse Failed(string message) => new(false, null, [], [message]);
    private static string Key(string? packageId, string contributorId) => string.IsNullOrWhiteSpace(packageId) ? string.Empty : packageId.Trim() + "\u001f" + contributorId.Trim();

    private sealed record ContributorRegistration(string PackageId, IPackageStackContributor Contributor);
}
