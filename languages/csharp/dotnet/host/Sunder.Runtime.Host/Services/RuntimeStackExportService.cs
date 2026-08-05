using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeStackExportService
{
    private readonly RuntimeSessionOwner _sessions;
    private readonly Sunder.Sdk.Rpc.ISunderRpcClient _rpcClient;
    private readonly StackExportArchiveBuilder _archiveBuilder;

    public RuntimeStackExportService(
        RuntimeSessionOwner sessions,
        RuntimeContentTransferStore transfers,
        RuntimePackagePaths paths,
        RuntimeRpcBroker rpcBroker,
        TimeProvider? timeProvider = null,
        RuntimeStackPolicyOptions? policy = null)
    {
        _sessions = sessions;
        _rpcClient = rpcBroker.CreateStackHostClient();
        _archiveBuilder = new StackExportArchiveBuilder(
            transfers,
            paths,
            timeProvider ?? TimeProvider.System,
            policy ?? new RuntimeStackPolicyOptions());
    }

    public async Task<RuntimeStackExportDiscoveryResponse> ListItemsAsync(CancellationToken cancellationToken = default)
    {
        using var lease = _sessions.State.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        await using var callScope = await _rpcClient.CreateCallScopeAsync(
            cancellationToken: linked.Token).ConfigureAwait(false);
        var items = new List<RuntimeStackExportItemDescriptor>();
        var errors = new List<string>();
        var exporters = await RuntimeStackContributorCatalog.GetExportersAsync(
            _rpcClient,
            errors,
            linked.Token).ConfigureAwait(false);
        foreach (var registration in exporters.Values)
        {
            try
            {
                var client = SunderStackContributorRpc.CreateClient(callScope, registration.Provider.Endpoint);
                var discovered = await client.ListExportItemsAsync(
                    new StackExportDiscoveryContext(registration.PackageId),
                    linked.Token).ConfigureAwait(false);
                if (!RuntimeStackContributorCatalog.ValidateExportItemIds(discovered, registration.ContributorId, errors))
                {
                    continue;
                }
                items.AddRange(discovered.Select(item => RuntimeStackContractMapper.ToExportItem(
                    registration.PackageId,
                    registration.ContributorId,
                    item)));
            }
            catch (Exception) when (!linked.IsCancellationRequested)
            {
                errors.Add($"Stack exporter '{registration.ContributorId}' discovery failed.");
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
        await using var callScope = await _rpcClient.CreateCallScopeAsync(
            cancellationToken: linked.Token).ConfigureAwait(false);

        var fragments = new List<StackOwnedFragment>();
        var previews = new Dictionary<string, SunderStackFragmentPreview>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var errors = new List<string>();
        var contributors = await RuntimeStackContributorCatalog.GetExportersAsync(
            _rpcClient,
            errors,
            linked.Token).ConfigureAwait(false);
        foreach (var group in request.SelectedItems.GroupBy(
                     item => RuntimeStackContributorCatalog.Key(item.OwnerPackageId, item.ContributorId),
                     StringComparer.OrdinalIgnoreCase))
        {
            var selected = group.First();
            if (string.IsNullOrWhiteSpace(selected.OwnerPackageId) || !contributors.TryGetValue(group.Key, out var registration))
            {
                errors.Add($"No active Stack contributor '{selected.ContributorId}' from package '{selected.OwnerPackageId}' is available for export.");
                continue;
            }
            try
            {
                var scopedClient = SunderStackContributorRpc.CreateClient(
                    callScope,
                    registration.Provider.Endpoint);
                var selections = group.GroupBy(item => item.ItemId, StringComparer.OrdinalIgnoreCase)
                    .Select(itemGroup => new StackExportItemSelection(
                        itemGroup.Key,
                        itemGroup.SelectMany(item => item.Details ?? []).Select(detail => new StackExportDetailSelection(
                            detail.DetailId,
                            detail.IsSelected,
                            detail.ValueOverride,
                            Enum.TryParse<StackValueSensitivity>(detail.SensitivityOverride, true, out var sensitivity) ? sensitivity : null)).ToArray()))
                    .ToArray();
                var discovered = await scopedClient.ListExportItemsAsync(
                    new StackExportDiscoveryContext(registration.PackageId),
                    linked.Token).ConfigureAwait(false);
                if (!RuntimeStackContributorCatalog.ValidateExportItemIds(discovered, registration.ContributorId, errors))
                {
                    continue;
                }
                var contribution = await scopedClient.ExportAsync(
                    new StackExportRequest(selections),
                    linked.Token).ConfigureAwait(false);
                packageRequirements.Add(BuildPackageRequirement(lease, registration.PackageId));
                foreach (var fragment in contribution.Fragments)
                {
                    var owned = RuntimeStackContractMapper.OwnExportFragment(
                        registration.PackageId,
                        registration.ContributorId,
                        registration.Provider,
                        fragment);
                    var ownedFragment = owned.Fragment;
                    fragments.Add(owned);
                    var sourceItemId = string.IsNullOrWhiteSpace(fragment.SourceItemId) && selections.Length == 1 && contribution.Fragments.Count == 1
                        ? selections[0].ItemId
                        : fragment.SourceItemId;
                    if (!string.IsNullOrWhiteSpace(sourceItemId))
                    {
                        var item = discovered.FirstOrDefault(value => string.Equals(value.ItemId, sourceItemId, StringComparison.OrdinalIgnoreCase));
                        if (item is not null) previews[ownedFragment.FragmentId] = RuntimeStackContractMapper.ToPreview(sourceItemId, item, selections.First(value => string.Equals(value.ItemId, sourceItemId, StringComparison.OrdinalIgnoreCase)));
                    }
                }
                packageRequirements.AddRange(contribution.PackageRequirements);
                warnings.AddRange(contribution.Warnings);
            }
            catch (Exception) when (!linked.IsCancellationRequested)
            {
                errors.Add($"Stack exporter '{registration.ContributorId}' export failed.");
            }
        }
        if (errors.Count > 0) return new RuntimeStackExportResponse(false, null, warnings, errors);
        if (fragments.Count == 0 && packageRequirements.Count == 0) return new RuntimeStackExportResponse(false, null, warnings, ["Selected setup items did not produce Stack content."]);

        return await _archiveBuilder.BuildAsync(
            request,
            packageRequirements,
            fragments,
            previews,
            callScope,
            lease.Generation,
            warnings,
            linked.Token);
    }

    private IReadOnlyList<StackPackageRequirement> BuildSelectedPackageRequirements(
        PackageSessionLease lease,
        IReadOnlyList<string> packageIds)
        => packageIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(packageId => new StackPackageRequirement(packageId, CreatedWithVersion: _sessions.State.GetLoadedPackage(lease, packageId)?.Descriptor.Version)).ToArray();

    private StackPackageRequirement BuildPackageRequirement(PackageSessionLease lease, string packageId)
        => new(packageId, CreatedWithVersion: _sessions.State.GetLoadedPackage(lease, packageId)?.Descriptor.Version, MinimumVersion: "1.0.0");

    private static RuntimeStackExportResponse Failed(string message) => new(false, null, [], [message]);
}
