using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeStackImportService(RuntimeSessionOwner sessions, RuntimeContentTransferStore transfers)
{
    public async Task<RuntimeStackImportPreviewResponse> PreviewAsync(
        RuntimeStackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var upload = transfers.AcquireUpload(request.UploadId, RuntimeUploadKind.Stack, sessions.Generation, consume: false);
        if (upload is null)
        {
            return new RuntimeStackImportPreviewResponse(false, [], [], [], [], ["Stack upload was not found or is stale."]);
        }
        var stagingPath = CreateStagingPath();
        try
        {
            var loaded = await LoadFragmentsAsync(upload.FilePath, request.SelectedFragmentIds, stagingPath, cancellationToken);
            if (!loaded.Success) return new RuntimeStackImportPreviewResponse(false, [], [], [], loaded.Warnings, loaded.Errors);
            var contributors = GetContributors();
            var actions = new List<RuntimeStackImportActionDescriptor>();
            var inputs = new List<RuntimeStackRequiredInputDescriptor>();
            var conflicts = new List<RuntimeStackImportConflictDescriptor>();
            var warnings = loaded.Warnings.ToList();
            var errors = new List<string>();
            foreach (var group in loaded.Fragments.GroupBy(fragment => Key(fragment.OwnerPackageId, fragment.ContributorId), StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                if (!contributors.TryGetValue(group.Key, out var registration))
                {
                    errors.Add($"No active Stack contributor '{first.ContributorId}' from package '{first.OwnerPackageId}' is available.");
                    continue;
                }
                try
                {
                    var preview = await registration.Contributor.PreviewImportAsync(new StackImportPreviewRequest(group.ToArray(), request.InputValues, request.IdRemaps), cancellationToken);
                    actions.AddRange(preview.Actions.Select(value => RuntimeStackContractMapper.ToAction(registration.PackageId, registration.Contributor.ContributorId, value)));
                    inputs.AddRange(preview.RequiredInputs.Select(value => RuntimeStackContractMapper.ToRequiredInput(registration.PackageId, registration.Contributor.ContributorId, value)));
                    conflicts.AddRange(preview.Conflicts.Select(value => RuntimeStackContractMapper.ToConflict(registration.PackageId, registration.Contributor.ContributorId, value)));
                    warnings.AddRange(preview.Warnings);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    errors.Add($"Stack contributor '{registration.Contributor.ContributorId}' preview failed.");
                }
            }
            var blocking = conflicts.Any(conflict => string.Equals(conflict.Severity, StackImportConflictSeverity.Error.ToString(), StringComparison.OrdinalIgnoreCase));
            return new RuntimeStackImportPreviewResponse(errors.Count == 0 && !blocking, actions, inputs, conflicts, warnings, errors);
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    public async Task<RuntimeStackImportResponse> ImportAsync(RuntimeStackImportRequest request, CancellationToken cancellationToken = default)
    {
        var upload = transfers.AcquireUpload(request.UploadId, RuntimeUploadKind.Stack, sessions.Generation, consume: true);
        if (upload is null) return new RuntimeStackImportResponse(false, [], request.IdRemaps, [], ["Stack upload was not found or is stale."]);
        var stagingPath = CreateStagingPath();
        try
        {
            var loaded = await LoadFragmentsAsync(upload.FilePath, request.SelectedFragmentIds, stagingPath, cancellationToken);
            if (!loaded.Success) return new RuntimeStackImportResponse(false, [], request.IdRemaps, loaded.Warnings, loaded.Errors);
            var contributors = GetContributors();
            var imported = new List<RuntimeStackImportedItemDescriptor>();
            var applied = new List<RuntimeStackImportAppliedContributionDescriptor>();
            var remaps = new Dictionary<string, string>(request.IdRemaps, StringComparer.OrdinalIgnoreCase);
            var warnings = loaded.Warnings.ToList();
            var errors = new List<string>();
            foreach (var group in loaded.Fragments.GroupBy(fragment => Key(fragment.OwnerPackageId, fragment.ContributorId), StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                if (!contributors.TryGetValue(group.Key, out var registration))
                {
                    errors.Add($"No active Stack contributor '{first.ContributorId}' from package '{first.OwnerPackageId}' is available.");
                    continue;
                }
                try
                {
                    var fragments = group.ToArray();
                    var result = await registration.Contributor.ImportAsync(new StackImportRequest(fragments, request.InputValues, remaps, request.SelectedActionIds), cancellationToken);
                    var mapped = result.ImportedItems.Select(item => RuntimeStackContractMapper.ToImportedItem(registration.PackageId, registration.Contributor.ContributorId, item)).ToArray();
                    imported.AddRange(mapped);
                    if (mapped.Length > 0) applied.Add(new RuntimeStackImportAppliedContributionDescriptor(registration.PackageId, registration.Contributor.ContributorId, fragments.Select(fragment => fragment.FragmentId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), mapped));
                    foreach (var pair in result.IdRemaps) remaps[pair.Key] = pair.Value;
                    warnings.AddRange(result.Warnings);
                    errors.AddRange(result.Errors);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    errors.Add($"Stack contributor '{registration.Contributor.ContributorId}' import failed.");
                }
            }
            return new RuntimeStackImportResponse(errors.Count == 0, imported, remaps, warnings, errors) { AppliedContributions = applied };
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
            transfers.ReleaseUpload(upload);
        }
    }

    private Dictionary<string, ContributorRegistration> GetContributors()
        => sessions.State.GetExtensionContributions(SunderStackExtensionPoints.StackContributors)
            .Where(value => !string.IsNullOrWhiteSpace(value.PackageId) && !string.IsNullOrWhiteSpace(value.Contribution.ContributorId))
            .GroupBy(value => Key(value.PackageId, value.Contribution.ContributorId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new ContributorRegistration(group.First().PackageId, group.First().Contribution), StringComparer.OrdinalIgnoreCase);

    private static async Task<FragmentLoadResult> LoadFragmentsAsync(string stackPath, IReadOnlyList<string> selectedIds, string stagingPath, CancellationToken cancellationToken)
    {
        var validation = await SunderStackArchiveInspector.ExtractAndValidateAsync(stackPath, stagingPath, cancellationToken);
        if (!validation.Success || validation.Manifest is null) return new FragmentLoadResult(false, [], validation.Warnings, validation.Errors);
        var selected = selectedIds.Count == 0
            ? (validation.Manifest.Fragments ?? []).Where(fragment => fragment.DefaultSelected != false).Select(fragment => fragment.FragmentId!).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : selectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fragments = new List<StackFragmentImport>();
        foreach (var fragment in validation.Manifest.Fragments ?? [])
        {
            if (string.IsNullOrWhiteSpace(fragment.FragmentId) || !selected.Contains(fragment.FragmentId)) continue;
            var payloadPath = SunderArchive.ResolveFile(stagingPath, ArchiveRelativePath.Parse(fragment.PayloadPath!));
            fragments.Add(new StackFragmentImport(
                fragment.FragmentId,
                fragment.OwnerPackageId!,
                fragment.ContributorId!,
                fragment.SchemaId!,
                fragment.SchemaVersion!.Value,
                fragment.DisplayName!,
                await File.ReadAllTextAsync(payloadPath, cancellationToken),
                fragment.Description,
                ResolveFiles(stagingPath, fragment.FragmentId)));
        }
        return new FragmentLoadResult(true, fragments, validation.Warnings, []);
    }

    private static IReadOnlyList<StackImportPayloadFile> ResolveFiles(string stagingPath, string fragmentId)
    {
        var root = Path.Combine(stagingPath, "payload", "files", fragmentId);
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(path => new StackImportPayloadFile(Path.GetRelativePath(root, path).Replace('\\', '/'), path)).ToArray()
            : [];
    }

    private static string Key(string? packageId, string contributorId) => string.IsNullOrWhiteSpace(packageId) ? string.Empty : packageId.Trim() + "\u001f" + contributorId.Trim();
    private static string CreateStagingPath() => Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "V1", "runtime", Guid.NewGuid().ToString("N"));
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { } }

    private sealed record ContributorRegistration(string PackageId, IPackageStackContributor Contributor);
    private sealed record FragmentLoadResult(bool Success, IReadOnlyList<StackFragmentImport> Fragments, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors);
}
