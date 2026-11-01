using System.Text;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed partial class RuntimeStackCommandHandler(
    ICliRuntimeStackClient runtime,
    ICliRuntimePackageClient packages,
    ICliRuntimeExecutionRuntimeClient executionRuntimes,
    ArchiveValidationService archives,
    CliOutput output,
    ICliProgress progress,
    CliOptions options,
    IPackageSecretValueReader inputReader)
{
    public async Task<int> ExecuteAsync(ImportStackCommand command, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(command.File);
        var validation = await archives.ValidateStackAsync(fullPath, token).ConfigureAwait(false);
        if (!validation.Success || validation.Manifest is null)
            return CliRenderers.StackValidation(output, validation);

        var manifest = validation.Manifest;
        var availableFragments = (manifest.Fragments ?? []).Select(fragment => fragment.FragmentId!).ToArray();
        var selectedFragments = command.NoFragments ? [] : command.FragmentIds is { Count: > 0 }
            ? SelectKnown(command.FragmentIds, availableFragments, "fragment")
            : (manifest.Fragments ?? []).Where(fragment => fragment.DefaultSelected != false).Select(fragment => fragment.FragmentId!).ToArray();
        var inputValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        RuntimeStackImportPreviewResponse? preview = null;
        try
        {
            inputValues = await ReadInputsAsync(command, token).ConfigureAwait(false);
            output.ProtectValues(inputValues.Values);
            var preparation = await PreparePackageRequirementsAsync(manifest, command, token).ConfigureAwait(false);
            if (preparation.ExitCode != CliExitCodes.Success) return preparation.ExitCode;
            progress.Report($"Uploading and previewing Stack '{manifest.StackId}'...");
            var upload = await runtime.UploadStackAsync(fullPath, token).ConfigureAwait(false);
            preview = await runtime.PreviewStackImportAsync(
                new RuntimeStackImportPreviewRequest(
                    upload.UploadId,
                    selectedFragments,
                    inputValues,
                    new Dictionary<string, string>()) { UseDefaultFragments = false },
                token).ConfigureAwait(false);

            var defaults = preview.RequiredInputs.Where(input => input.Sensitivity == RuntimeStackInputSensitivity.Public
                && input.DefaultValue is not null && !inputValues.ContainsKey(input.InputId)).ToArray();
            if (defaults.Length > 0)
            {
                if (!string.IsNullOrWhiteSpace(preview.PlanId))
                    await runtime.DiscardStackImportPlanAsync(preview.PlanId, token).ConfigureAwait(false);
                foreach (var input in defaults) inputValues.Add(input.InputId, input.DefaultValue!);
                output.ProtectValues(inputValues.Values);
                var reboundUpload = await runtime.UploadStackAsync(fullPath, token).ConfigureAwait(false);
                preview = await runtime.PreviewStackImportAsync(new RuntimeStackImportPreviewRequest(
                    reboundUpload.UploadId, selectedFragments, inputValues, new Dictionary<string, string>())
                { UseDefaultFragments = false }, token).ConfigureAwait(false);
            }

            var selectedActionIds = command.NoActions ? [] : command.ActionIds is { Count: > 0 }
                ? SelectKnown(command.ActionIds, preview.Actions.Select(action => action.ActionId).ToArray(), "action")
                : preview.Actions.Where(action => action.DefaultSelected).Select(action => action.ActionId).ToArray();
            var selectedActions = preview.Actions.Where(action => selectedActionIds.Contains(action.ActionId, StringComparer.OrdinalIgnoreCase)).ToArray();
            var blockers = preview.Errors.Concat(preview.Conflicts.Where(conflict => conflict.Severity == "Error").Select(conflict => conflict.Message)).ToList();
            foreach (var input in preview.RequiredInputs.Where(input => input.Required && selectedActions.Any(action =>
                         action.OwnerPackageId == input.OwnerPackageId && action.ContributorId == input.ContributorId)))
                if (!inputValues.TryGetValue(input.InputId, out var value) || string.IsNullOrWhiteSpace(value))
                    blockers.Add($"Provide required input '{input.InputId}' through '--input-env' or '--inputs-stdin'.");
            output.Line($"Stack import preview: {manifest.Name} ({manifest.StackId})");
            output.Line($"Selected fragments: {selectedFragments.Length}");
            output.Line($"Selected actions: {selectedActions.Length}/{preview.Actions.Count}");
            output.Line($"Required inputs: {preview.RequiredInputs.Count}");
            foreach (var action in preview.Actions)
                output.Line($"  {(selectedActionIds.Contains(action.ActionId, StringComparer.OrdinalIgnoreCase) ? "apply" : "skip ")} {action.DisplayName} ({action.Kind})");
            foreach (var input in preview.RequiredInputs)
                output.Line($"  input {input.InputId}: {input.Label} ({input.Sensitivity.ToString().ToLowerInvariant()}{(input.Required ? ", required" : string.Empty)})");
            foreach (var warning in preview.Warnings) output.Warning(warning);
            foreach (var conflict in preview.Conflicts) output.Warning(conflict.Message);
            foreach (var blocker in blockers) output.Info($"Apply blocker: {blocker}");
            foreach (var error in preview.Errors) output.Error(error, "runtime.stack.preview.failed");
            RuntimeStackImportResponse? applied = null;
            if (!command.DryRun && preview.Success && blockers.Count == 0 && selectedActions.Length > 0)
            {
                if (string.IsNullOrWhiteSpace(preview.PlanId))
                    blockers.Add("Runtime did not return an import plan.");
                else
                    applied = await runtime.ImportStackAsync(new(preview.PlanId, selectedFragments, selectedActionIds), token).ConfigureAwait(false);
            }
            output.Data(new
            {
                dryRun = command.DryRun,
                packagePreflight = preparation.Preflight,
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
                    selected = selectedActionIds.Contains(action.ActionId, StringComparer.OrdinalIgnoreCase),
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
                outcome = applied?.Outcome.ToString(),
                importedItems = applied?.ImportedItems,
                contributorResults = applied?.ContributorResults.Select(result => new
                {
                    result.OwnerPackageId, result.ContributorId, result.FragmentIds,
                    outcome = result.Outcome.ToString(), result.ImportedItems, result.Warnings, result.Errors,
                }),
                errors = applied?.Errors ?? preview.Errors,
            });
            if (applied is not null)
            {
                foreach (var warning in applied.Warnings) output.Warning(warning);
                foreach (var error in applied.Errors) output.Error(error, "runtime.stack.import.failed");
                output.Line($"Stack import: {applied.Outcome} ({applied.ImportedItems.Count} imported items)");
                return applied.Outcome == RuntimeStackImportOutcome.Completed ? CliExitCodes.Success : CliExitCodes.Failure;
            }
            return !preview.Success || !command.DryRun && blockers.Count > 0 ? CliExitCodes.Conflict : CliExitCodes.Success;
        }
        finally
        {
            inputValues.Clear();
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
        progress.Report("Discovering Runtime Stack export items...");
        var discovery = await runtime.ListStackExportItemsAsync(token).ConfigureAwait(false);
        foreach (var warning in discovery.Warnings) output.Warning(warning);
        if (discovery.Errors.Count > 0)
        {
            foreach (var error in discovery.Errors) output.Error(error, "runtime.stack.export.discovery_failed");
            return CliExitCodes.Failure;
        }

        var requestedItems = command.ItemIds is { Count: > 0 }
            ? SelectKnown(command.ItemIds, discovery.Items.Select(item => $"{item.OwnerPackageId}/{item.ContributorId}/{item.ItemId}").ToArray(), "export item").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var selected = discovery.Items
            .Where(item => requestedItems is not null ? requestedItems.Contains($"{item.OwnerPackageId}/{item.ContributorId}/{item.ItemId}") : command.IncludeAll || item.DefaultSelected)
            .Select(item => CreateSelection(item, command.IncludeAll))
            .Where(selection => selection is not null)
            .Select(selection => selection!)
            .ToArray();
        if (command.DryRun)
        {
            output.Data(new
            {
                dryRun = true,
                items = discovery.Items.Select(item => new
                {
                    selectionId = $"{item.OwnerPackageId}/{item.ContributorId}/{item.ItemId}",
                    item.OwnerPackageId, item.ContributorId, item.ItemId, item.DisplayName, item.Kind, item.DefaultSelected,
                    selected = selected.Any(selection => selection.OwnerPackageId == item.OwnerPackageId && selection.ContributorId == item.ContributorId && selection.ItemId == item.ItemId),
                    details = item.Details?.Select(detail => new { detail.DetailId, detail.Label, detail.DefaultSelected, detail.IsEditable, detail.SupportsAskOnImport, detail.Sensitivity }),
                }),
            });
            foreach (var item in discovery.Items) output.Line($"{item.OwnerPackageId}/{item.ContributorId}/{item.ItemId}: {item.DisplayName}");
            return CliExitCodes.Success;
        }
        if (selected.Length == 0)
        {
            output.Error(
                command.IncludeAll
                    ? "The Runtime did not discover any exportable Stack content."
                    : "No Runtime Stack items are selected by default. Review in Sunder App or use '--all'.",
                "runtime.stack.export.empty");
            return CliExitCodes.Conflict;
        }

        var destination = ResolveOutput(command.Output, command.StackId);
        if (File.Exists(destination) && !command.Force)
            throw new CliConflictException($"Destination file '{destination}' already exists. Use '--force' to replace it.");

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

    private static string[] SelectKnown(IReadOnlyList<string> selected, IReadOnlyList<string> available, string kind)
    {
        var known = available.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Any(id => !known.Contains(id))) throw new CliUsageException($"Unknown Stack {kind} selection. Inspect the archive or refresh the preview first.");
        if (selected.Any(id => available.Count(value => string.Equals(value, id, StringComparison.OrdinalIgnoreCase)) != 1))
            throw new CliUsageException($"Ambiguous Stack {kind} selection. Use the Runtime API with separate owner, contributor, and item ids.");
        return selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
