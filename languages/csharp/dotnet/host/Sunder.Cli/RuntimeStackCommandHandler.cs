using System.Text;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class RuntimeStackCommandHandler(
    ICliRuntimeStackClient runtime,
    ArchiveValidationService archives,
    CliOutput output,
    ICliProgress progress)
{
    public async Task<int> ExecuteAsync(ImportStackCommand command, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(command.File);
        var validation = await archives.ValidateStackAsync(fullPath, token).ConfigureAwait(false);
        if (!validation.Success || validation.Manifest is null)
            return CliRenderers.StackValidation(output, validation);

        var manifest = validation.Manifest;
        var selectedFragments = (manifest.Fragments ?? [])
            .Where(fragment => fragment.DefaultSelected != false && !string.IsNullOrWhiteSpace(fragment.FragmentId))
            .Select(fragment => fragment.FragmentId!)
            .ToArray();
        progress.Report($"Uploading and previewing Stack '{manifest.StackId}'...");
        var upload = await runtime.UploadStackAsync(fullPath, token).ConfigureAwait(false);
        RuntimeStackImportPreviewResponse? preview = null;
        try
        {
            preview = await runtime.PreviewStackImportAsync(
                new RuntimeStackImportPreviewRequest(
                    upload.UploadId,
                    selectedFragments,
                    new Dictionary<string, string>(),
                    new Dictionary<string, string>()),
                token).ConfigureAwait(false);

            var selectedActions = preview.Actions.Where(action => action.DefaultSelected).ToArray();
            var blockers = BuildImportBlockers(manifest, preview);
            output.Data(new
            {
                dryRun = true,
                stackId = manifest.StackId,
                name = manifest.Name,
                selectedFragmentIds = selectedFragments,
                packageRequirements = (manifest.Packages ?? []).Select(package => new
                {
                    packageId = package.PackageId,
                    installTag = package.InstallTag,
                    minimumVersion = package.MinimumVersion,
                    required = package.Required,
                }).ToArray(),
                actions = preview.Actions.Select(action => new
                {
                    actionId = action.ActionId,
                    ownerPackageId = action.OwnerPackageId,
                    contributorId = action.ContributorId,
                    displayName = action.DisplayName,
                    kind = action.Kind,
                    selected = action.DefaultSelected,
                    description = action.Description,
                }).ToArray(),
                inputs = preview.RequiredInputs.Select(input => new
                {
                    inputId = input.InputId,
                    ownerPackageId = input.OwnerPackageId,
                    contributorId = input.ContributorId,
                    label = input.Label,
                    sensitivity = input.Sensitivity.ToString().ToLowerInvariant(),
                    required = input.Required,
                    hasDefault = input.DefaultValue is not null,
                    description = input.Description,
                }).ToArray(),
                conflicts = preview.Conflicts.Select(conflict => new
                {
                    conflictId = conflict.ConflictId,
                    ownerPackageId = conflict.OwnerPackageId,
                    contributorId = conflict.ContributorId,
                    message = conflict.Message,
                    severity = conflict.Severity,
                    fragmentId = conflict.FragmentId,
                }).ToArray(),
                selectedActionCount = selectedActions.Length,
                blockers,
                warnings = preview.Warnings,
            });
            output.Line($"Stack import preview: {manifest.Name} ({manifest.StackId})");
            output.Line($"Default fragments: {selectedFragments.Length}");
            output.Line($"Default actions: {selectedActions.Length}/{preview.Actions.Count}");
            output.Line($"Required inputs: {preview.RequiredInputs.Count}");
            foreach (var action in preview.Actions)
                output.Line($"  {(action.DefaultSelected ? "apply" : "skip ")} {action.DisplayName} ({action.Kind})");
            foreach (var input in preview.RequiredInputs)
                output.Line($"  input {input.InputId}: {input.Label} ({input.Sensitivity.ToString().ToLowerInvariant()}{(input.Required ? ", required" : string.Empty)})");
            foreach (var warning in preview.Warnings) output.Warning(warning);
            foreach (var conflict in preview.Conflicts) output.Warning(conflict.Message);
            foreach (var blocker in blockers) output.Info($"Apply blocker: {blocker}");
            return CliExitCodes.Success;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(preview?.PlanId))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await runtime.DiscardStackImportPlanAsync(preview.PlanId, cleanup.Token).ConfigureAwait(false);
                }
                catch
                {
                    // Runtime plans are also expiry-bounded; cleanup must not replace the preview result.
                }
            }
        }
    }

    public async Task<int> ExecuteAsync(ExportStackCommand command, CancellationToken token)
    {
        var destination = ResolveOutput(command.Output, command.StackId);
        if (File.Exists(destination) && !command.Force)
            throw new CliConflictException($"Destination file '{destination}' already exists. Use '--force' to replace it.");

        progress.Report("Discovering Runtime Stack export items...");
        var discovery = await runtime.ListStackExportItemsAsync(token).ConfigureAwait(false);
        foreach (var warning in discovery.Warnings) output.Warning(warning);
        if (discovery.Errors.Count > 0)
        {
            foreach (var error in discovery.Errors) output.Error(error, "runtime.stack.export.discovery_failed");
            return CliExitCodes.Failure;
        }

        var selected = discovery.Items
            .Where(item => command.IncludeAll || item.DefaultSelected)
            .Select(item => CreateSelection(item, command.IncludeAll))
            .Where(selection => selection is not null)
            .Select(selection => selection!)
            .ToArray();
        if (selected.Length == 0)
        {
            output.Error(
                command.IncludeAll
                    ? "The Runtime did not discover any exportable Stack content."
                    : "No Runtime Stack items are selected by default. Review in Sunder App or use '--all'.",
                "runtime.stack.export.empty");
            return CliExitCodes.Conflict;
        }

        progress.Report($"Exporting {selected.Length} Stack item(s)...");
        var result = await runtime.ExportStackAsync(new RuntimeStackExportRequest(
            command.StackId,
            string.IsNullOrWhiteSpace(command.Name) ? command.StackId : command.Name.Trim(),
            string.IsNullOrWhiteSpace(command.Summary) ? null : command.Summary.Trim(),
            selected,
            SelectedPackages: selected.Select(item => item.OwnerPackageId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()), token).ConfigureAwait(false);
        foreach (var warning in result.Warnings) output.Warning(warning);
        if (!result.Success || result.Download is null)
        {
            foreach (var error in result.Errors.DefaultIfEmpty("Runtime did not provide exported Stack content."))
                output.Error(error, "runtime.stack.export.failed");
            return CliExitCodes.Failure;
        }

        await runtime.DownloadContentAsync(result.Download, destination, token).ConfigureAwait(false);
        output.Data(new
        {
            stackId = command.StackId,
            output = destination,
            selectedItemCount = selected.Length,
            contentHash = result.Download.ContentHash,
            size = result.Download.Length,
            warnings = result.Warnings,
        });
        output.Success($"Exported Stack '{command.StackId}' to {destination}.");
        return CliExitCodes.Success;
    }

    private static IReadOnlyList<string> BuildImportBlockers(
        SunderStackManifest manifest,
        RuntimeStackImportPreviewResponse preview)
    {
        var blockers = new List<string>
        {
            "Noninteractive apply is deferred until package, fragment, action, and input selections can be supplied explicitly.",
        };
        if ((manifest.Packages?.Count ?? 0) > 0)
            blockers.Add("Package requirement installation and version selection are not part of the Runtime Stack import endpoint.");
        if ((manifest.Fragments ?? []).Any(fragment => fragment.DefaultSelected == false))
            blockers.Add("The archive contains fragments excluded by default and the CLI does not yet accept fragment selections.");
        if (preview.Actions.Any(action => !action.DefaultSelected))
            blockers.Add("The preview contains actions excluded by default and the CLI does not yet accept action selections.");
        if (preview.RequiredInputs.Count > 0)
            blockers.Add("The preview requires input values and the CLI does not yet provide a scoped safe input-source syntax.");
        return blockers;
    }

    private static RuntimeStackExportSelection? CreateSelection(RuntimeStackExportItemDescriptor item, bool includeAll)
    {
        var details = item.Details?.Where(detail => includeAll || detail.DefaultSelected)
            .Select(detail => new RuntimeStackExportDetailSelection(
                ResolveDetailId(detail),
                IsSelected: true,
                ValueOverride: null,
                SensitivityOverride: null))
            .ToArray();
        if (item.Details is { Count: > 0 } && details?.Length == 0) return null;
        return new RuntimeStackExportSelection(item.OwnerPackageId, item.ContributorId, item.ItemId, details);
    }

    private static string ResolveDetailId(RuntimeStackExportItemDetail detail)
    {
        if (!string.IsNullOrWhiteSpace(detail.DetailId)) return detail.DetailId;
        var result = new StringBuilder(detail.Label.Length);
        var pendingSeparator = false;
        foreach (var character in detail.Label.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                result.Append(character);
                pendingSeparator = false;
            }
            else if (result.Length > 0 && !pendingSeparator)
            {
                result.Append('-');
                pendingSeparator = true;
            }
        }
        var value = result.ToString().Trim('-');
        return value.Length == 0 ? "detail" : value;
    }

    private static string ResolveOutput(string? output, string stackId)
    {
        if (string.IsNullOrWhiteSpace(output)) return Path.GetFullPath(SunderStackFormat.BuildStackFileName(stackId));
        if (Path.EndsInDirectorySeparator(output) || Directory.Exists(Path.GetFullPath(output)))
            throw new CliUsageException("Option '--output' must name the exact destination file, not a directory.");
        return Path.GetFullPath(output);
    }
}
