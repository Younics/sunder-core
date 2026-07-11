using System.Security.Cryptography;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeStackExportService(
    RuntimeSessionOwner sessions,
    RuntimeContentTransferStore transfers,
    RuntimePackagePaths paths)
{
    public async Task<RuntimeStackExportDiscoveryResponse> ListItemsAsync(CancellationToken cancellationToken = default)
    {
        var items = new List<RuntimeStackExportItemDescriptor>();
        var errors = new List<string>();
        foreach (var contribution in sessions.State.GetExtensionContributions(SunderStackExtensionPoints.StackContributors))
        {
            try
            {
                var discovered = await contribution.Contribution.ListExportItemsAsync(new StackExportDiscoveryContext(contribution.PackageId), cancellationToken);
                items.AddRange(discovered.Select(item => RuntimeStackContractMapper.ToExportItem(contribution.PackageId, contribution.Contribution.ContributorId, item)));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"Stack contributor '{contribution.Contribution.ContributorId}' export discovery failed.");
            }
        }
        return new RuntimeStackExportDiscoveryResponse(items, [], errors);
    }

    public async Task<RuntimeStackExportResponse> ExportAsync(RuntimeStackExportRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.StackId)) return Failed("Stack id is required.");
        if (string.IsNullOrWhiteSpace(request.Name)) return Failed("Stack name is required.");
        var packageRequirements = BuildSelectedPackageRequirements(request.SelectedPackages ?? []).ToList();
        if (request.SelectedItems.Count == 0 && packageRequirements.Count == 0) return Failed("Select at least one package or setup item to export.");

        var contributors = GetContributors();
        var fragments = new List<OwnedFragment>();
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
                var discovered = await registration.Contributor.ListExportItemsAsync(new StackExportDiscoveryContext(registration.PackageId), cancellationToken);
                var contribution = await registration.Contributor.ExportAsync(new StackExportRequest(selections.Select(item => item.ItemId).ToArray(), selections), cancellationToken);
                packageRequirements.Add(BuildPackageRequirement(registration.PackageId));
                foreach (var fragment in contribution.Fragments)
                {
                    fragments.Add(new OwnedFragment(registration.PackageId, fragment));
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
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"Stack contributor '{registration.Contributor.ContributorId}' export failed.");
            }
        }
        if (errors.Count > 0) return new RuntimeStackExportResponse(false, null, warnings, errors);
        if (fragments.Count == 0 && packageRequirements.Count == 0) return new RuntimeStackExportResponse(false, null, warnings, ["Selected setup items did not produce Stack content."]);

        var stagingPath = Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "V1", "export", Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(stagingPath, "output.sunderstack");
        string? unownedDownload = null;
        var mediaLeases = new List<RuntimeUploadLease>();
        try
        {
            var payloadFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var owned in fragments)
            {
                var payloadPath = Path.Combine(stagingPath, "fragments", owned.Fragment.FragmentId + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(payloadPath)!);
                await File.WriteAllTextAsync(payloadPath, owned.Fragment.JsonPayload, cancellationToken);
                payloadFiles[$"payload/fragments/{owned.Fragment.FragmentId}.json"] = payloadPath;
                foreach (var file in owned.Fragment.Files ?? []) payloadFiles[$"payload/files/{owned.Fragment.FragmentId}/{file.RelativePath.Replace('\\', '/')}"] = file.SourcePath;
            }
            var media = BuildMedia(request.Media ?? [], payloadFiles, mediaLeases, errors);
            if (errors.Count > 0) return new RuntimeStackExportResponse(false, null, warnings, errors);
            var now = DateTimeOffset.UtcNow;
            var manifest = new SunderStackManifest
            {
                SchemaVersion = SunderStackFormat.CurrentSchemaVersion,
                MinReaderVersion = SunderStackFormat.CurrentReaderVersion,
                StackId = request.StackId,
                Name = request.Name,
                Summary = request.Summary,
                ReadmeMarkdown = string.IsNullOrWhiteSpace(request.ReadmeMarkdown) ? null : request.ReadmeMarkdown,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                Packages = RuntimeStackContractMapper.ToPackageRequirements(packageRequirements),
                Fragments = fragments.Select(owned => ToManifest(owned, previews.GetValueOrDefault(owned.Fragment.FragmentId))).ToArray(),
                Media = media.Count == 0 ? null : media,
            };
            await SunderStackArchiveWriter.WriteAsync(manifest, outputPath, payloadFiles, cancellationToken);
            var validation = await SunderStackArchiveInspector.ExtractAndValidateAsync(outputPath, Path.Combine(stagingPath, "validate"), cancellationToken);
            warnings.AddRange(validation.Warnings);
            if (!validation.Success) return new RuntimeStackExportResponse(false, null, warnings, validation.Errors);
            var downloadPath = Path.Combine(paths.TransferRootPath, Guid.NewGuid().ToString("N") + ".download");
            unownedDownload = downloadPath;
            Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);
            File.Move(outputPath, downloadPath);
            await using var stream = new FileStream(downloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            var download = transfers.RegisterDownload(downloadPath, hash, stream.Length, request.StackId + ".sunderstack", "application/vnd.sunder.stack", sessions.Generation);
            unownedDownload = null;
            return new RuntimeStackExportResponse(true, download, warnings, []);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new RuntimeStackExportResponse(false, null, warnings, ["Failed to write Stack archive."]);
        }
        finally
        {
            if (unownedDownload is not null) TryDeleteFile(unownedDownload);
            foreach (var lease in mediaLeases) transfers.ReleaseUpload(lease);
            TryDeleteDirectory(stagingPath);
        }
    }

    private Dictionary<string, ContributorRegistration> GetContributors()
        => sessions.State.GetExtensionContributions(SunderStackExtensionPoints.StackContributors)
            .Where(value => !string.IsNullOrWhiteSpace(value.PackageId) && !string.IsNullOrWhiteSpace(value.Contribution.ContributorId))
            .GroupBy(value => Key(value.PackageId, value.Contribution.ContributorId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new ContributorRegistration(group.First().PackageId, group.First().Contribution), StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<StackPackageRequirement> BuildSelectedPackageRequirements(IReadOnlyList<string> packageIds)
        => packageIds.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(packageId => new StackPackageRequirement(packageId, CreatedWithVersion: sessions.State.GetLoadedPackage(packageId)?.Descriptor.Version)).ToArray();

    private StackPackageRequirement BuildPackageRequirement(string packageId)
        => new(packageId, CreatedWithVersion: sessions.State.GetLoadedPackage(packageId)?.Descriptor.Version, MinimumVersion: "1.0.0");

    private IReadOnlyList<SunderStackMediaManifest> BuildMedia(
        IReadOnlyList<RuntimeStackMediaInput> media,
        IDictionary<string, string> payloadFiles,
        ICollection<RuntimeUploadLease> leases,
        ICollection<string> errors)
    {
        var manifests = new List<SunderStackMediaManifest>();
        var pathsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in media)
        {
            var archivePath = item.ContentPath.Replace('\\', '/');
            var upload = transfers.AcquireUpload(item.UploadId, RuntimeUploadKind.StackMedia, sessions.Generation, consume: true);
            if (upload is null)
            {
                errors.Add("A Stack media upload was not found or is stale.");
                continue;
            }
            leases.Add(upload);
            if (string.IsNullOrWhiteSpace(archivePath)
                || !archivePath.StartsWith(SunderStackFormat.MediaPayloadRoot, StringComparison.OrdinalIgnoreCase)
                || archivePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == "..")
                || !pathsSeen.Add(archivePath))
            {
                errors.Add($"Stack media content path must be unique and under {SunderStackFormat.MediaPayloadRoot}.");
                continue;
            }
            payloadFiles[archivePath] = upload.FilePath;
            manifests.Add(new SunderStackMediaManifest
            {
                Path = archivePath,
                FileName = string.IsNullOrWhiteSpace(item.FileName) ? upload.FileName : Path.GetFileName(item.FileName),
                ContentType = string.IsNullOrWhiteSpace(item.ContentType) ? "application/octet-stream" : item.ContentType,
                Size = upload.Length,
                AltText = string.IsNullOrWhiteSpace(item.AltText) ? null : item.AltText.Trim(),
                SortOrder = item.SortOrder,
            });
        }
        return manifests;
    }

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

    private static SunderStackFragmentManifest ToManifest(OwnedFragment owned, SunderStackFragmentPreview? preview)
        => new()
        {
            FragmentId = owned.Fragment.FragmentId,
            OwnerPackageId = owned.OwnerPackageId,
            ContributorId = owned.Fragment.ContributorId,
            SchemaId = owned.Fragment.SchemaId,
            SchemaVersion = owned.Fragment.SchemaVersion,
            DisplayName = owned.Fragment.DisplayName,
            Description = owned.Fragment.Description,
            DefaultSelected = owned.Fragment.DefaultSelected,
            PayloadPath = $"payload/fragments/{owned.Fragment.FragmentId}.json",
            RequiredInputs = (owned.Fragment.RequiredInputs ?? []).Select(input => new SunderStackRequiredInputManifest { InputId = input.InputId, Label = input.Label, Description = input.Description, DefaultValue = input.DefaultValue, Required = input.Required }).ToArray(),
            Preview = preview,
        };

    private static RuntimeStackExportResponse Failed(string message) => new(false, null, [], [message]);
    private static string Key(string? packageId, string contributorId) => string.IsNullOrWhiteSpace(packageId) ? string.Empty : packageId.Trim() + "\u001f" + contributorId.Trim();
    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    private sealed record ContributorRegistration(string PackageId, IPackageStackContributor Contributor);
    private sealed record OwnedFragment(string OwnerPackageId, StackFragmentExport Fragment);
}
