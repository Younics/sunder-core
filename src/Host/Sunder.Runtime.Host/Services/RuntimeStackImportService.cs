using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;
using static Sunder.Runtime.Host.Services.RuntimeStackImportSupport;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeStackImportService : IDisposable
{
    private readonly RuntimeSessionOwner _sessions;
    private readonly RuntimeContentTransferStore _transfers;
    private readonly StackImportPlanStore _plans;
    private readonly StackImportArchiveReader _archiveReader = new();

    public RuntimeStackImportService(
        RuntimeSessionOwner sessions,
        RuntimeContentTransferStore transfers,
        TimeProvider? timeProvider = null,
        RuntimeStackPolicyOptions? policy = null)
    {
        _sessions = sessions;
        _transfers = transfers;
        _plans = new StackImportPlanStore(transfers, timeProvider ?? TimeProvider.System, policy ?? new RuntimeStackPolicyOptions());
    }

    public async Task<RuntimeStackImportPreviewResponse> PreviewAsync(
        RuntimeStackImportPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        SweepExpired();
        using var lease = _sessions.State.AcquireLease();
        using var linked = lease.CreateLinkedCancellation(cancellationToken);
        var upload = _transfers.AcquireUpload(request.UploadId, RuntimeUploadKind.Stack, lease.Generation, consume: true);
        if (upload is null)
        {
            return PreviewFailure("Stack upload was not found or is stale.");
        }

        var stagingPath = CreateStagingPath();
        var retainedByPlan = false;
        try
        {
            var loaded = await _archiveReader.LoadAsync(upload.FilePath, request.SelectedFragmentIds, stagingPath, linked.Token);
            if (!loaded.Success)
            {
                return PreviewFailure(loaded.Warnings, loaded.Errors);
            }

            var errors = new List<string>();
            var contributors = RuntimeStackContributorCatalog.GetImporters(_sessions, lease, errors);
            var inputValues = Copy(request.InputValues);
            var idRemaps = Copy(request.IdRemaps);
            var actions = new List<RuntimeStackImportActionDescriptor>();
            var inputs = new List<RuntimeStackRequiredInputDescriptor>();
            var conflicts = new List<RuntimeStackImportConflictDescriptor>();
            var warnings = loaded.Warnings.ToList();
            RuntimeStackContributorCatalog.ValidateScopedValues(inputValues, RuntimeStackScopedKey.InputKind, contributors, "input", errors);
            RuntimeStackContributorCatalog.ValidateScopedValues(idRemaps, RuntimeStackScopedKey.RemapKind, contributors, "remap", errors);
            var bindings = new List<StackContributorBinding>();
            foreach (var group in loaded.Fragments.GroupBy(
                         fragment => RuntimeStackContributorCatalog.Key(fragment.OwnerPackageId, fragment.ContributorId),
                         StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                if (!contributors.TryGetValue(group.Key, out var registration))
                {
                    errors.Add($"No active Stack contributor '{first.ContributorId}' from package '{first.OwnerPackageId}' is available.");
                    continue;
                }

                var fragments = Array.AsReadOnly(group.Select(fragment =>
                    RuntimeStackContractMapper.OwnImportFragment(
                        registration.PackageId,
                        registration.Importer.ContributorId,
                        fragment)).ToArray());
                try
                {
                    var preview = await registration.Importer.PreviewImportAsync(
                        new StackImportPreviewRequest(
                            fragments,
                            RuntimeStackContractMapper.ToContributorValues(
                                inputValues,
                                RuntimeStackScopedKey.InputKind,
                                registration.PackageId,
                                registration.Importer.ContributorId),
                            RuntimeStackContractMapper.ToContributorValues(
                                idRemaps,
                                RuntimeStackScopedKey.RemapKind,
                                registration.PackageId,
                                registration.Importer.ContributorId)),
                        linked.Token);
                    if (!RuntimeStackContributorCatalog.ValidatePreviewIds(preview, registration, errors))
                    {
                        continue;
                    }
                    var contributorActions = preview.Actions
                        .Select(value => RuntimeStackContractMapper.ToAction(registration.PackageId, registration.Importer.ContributorId, value))
                        .ToArray();
                    actions.AddRange(contributorActions);
                    inputs.AddRange(preview.RequiredInputs.Select(value => RuntimeStackContractMapper.ToRequiredInput(registration.PackageId, registration.Importer.ContributorId, value)));
                    conflicts.AddRange(preview.Conflicts.Select(value => RuntimeStackContractMapper.ToConflict(registration.PackageId, registration.Importer.ContributorId, value)));
                    warnings.AddRange(preview.Warnings);
                    bindings.Add(new StackContributorBinding(registration, fragments, contributorActions));
                }
                catch (Exception) when (!linked.IsCancellationRequested)
                {
                    errors.Add($"Stack importer '{registration.Importer.ContributorId}' preview failed.");
                }
            }

            var blocking = conflicts.Any(conflict => string.Equals(
                conflict.Severity,
                StackImportConflictSeverity.Error.ToString(),
                StringComparison.OrdinalIgnoreCase));
            if (errors.Count > 0 || blocking)
            {
                return new RuntimeStackImportPreviewResponse(false, null, null, actions, inputs, conflicts, warnings, errors);
            }

            var planId = CreatePlanId();
            var plan = new StackImportPlan(
                planId,
                upload,
                upload.ContentHash,
                stagingPath,
                lease.Generation,
                loaded.SelectedFragmentIds,
                inputValues,
                idRemaps,
                bindings,
                actions.ToArray(),
                conflicts.ToArray(),
                _plans.ExpiresAtUtc);
            if (!_plans.TryAdd(plan))
            {
                throw new InvalidOperationException("Failed to allocate a Stack import plan.");
            }

            retainedByPlan = true;
            return new RuntimeStackImportPreviewResponse(true, planId, plan.ExpiresAtUtc, actions, inputs, conflicts, warnings, []);
        }
        finally
        {
            if (!retainedByPlan)
            {
                TryDeleteDirectory(stagingPath);
                _transfers.ReleaseUpload(upload);
            }
        }
    }

    public async Task<RuntimeStackImportResponse> ImportAsync(
        RuntimeStackImportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.PlanId) || !_plans.TryTake(request.PlanId, out var plan))
        {
            return ImportFailure("Stack import plan was not found or was already consumed.");
        }

        try
        {
            if (_plans.IsExpired(plan))
            {
                return ImportFailure("Stack import plan has expired.", plan.IdRemaps);
            }

            using var lease = _sessions.State.AcquireLease();
            using var linked = lease.CreateLinkedCancellation(cancellationToken);
            if (lease.Generation != plan.Generation)
            {
                return ImportFailure("Stack import plan is stale because the Runtime generation changed.", plan.IdRemaps);
            }

            if (!string.Equals(await ComputeHashAsync(plan.Upload.FilePath, linked.Token), plan.ArchiveContentHash, StringComparison.OrdinalIgnoreCase))
            {
                return ImportFailure("Stack import plan is stale because its archive changed.", plan.IdRemaps);
            }

            var fragmentError = ValidateSelectedFragments(request.SelectedFragmentIds, plan.SelectedFragmentIds);
            if (fragmentError is not null)
            {
                return ImportFailure(fragmentError, plan.IdRemaps);
            }

            var knownActionIds = plan.Actions.Select(action => action.ActionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknownActionIds = request.SelectedActionIds
                .Where(id => string.IsNullOrWhiteSpace(id) || !knownActionIds.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (unknownActionIds.Length > 0)
            {
                return ImportFailure($"Stack import selected unknown action id(s): {string.Join(", ", unknownActionIds)}.", plan.IdRemaps);
            }

            var currentErrors = new List<string>();
            var currentContributors = RuntimeStackContributorCatalog.GetImporters(_sessions, lease, currentErrors);
            if (currentErrors.Count > 0)
            {
                return ImportFailure(currentErrors[0], plan.IdRemaps);
            }
            foreach (var binding in plan.Contributors)
            {
                var key = RuntimeStackContributorCatalog.Key(binding.Registration.PackageId, binding.Registration.Importer.ContributorId);
                if (!currentContributors.TryGetValue(key, out var current)
                    || !ReferenceEquals(current.Importer, binding.Registration.Importer))
                {
                    return ImportFailure("Stack import plan is stale because a contributor changed.", plan.IdRemaps);
                }
            }

            var imported = new List<RuntimeStackImportedItemDescriptor>();
            var contributorResults = new List<RuntimeStackImportContributorResultDescriptor>();
            var remaps = new Dictionary<string, string>(plan.IdRemaps, StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            var errors = new List<string>();
            foreach (var binding in plan.Contributors)
            {
                var registration = binding.Registration;
                var fragmentIds = binding.Fragments.Select(fragment => fragment.FragmentId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var selectedActionIds = request.SelectedActionIds
                    .Where(selected => binding.Actions.Any(action => string.Equals(action.ActionId, selected, StringComparison.OrdinalIgnoreCase)))
                    .Select(selected => binding.Actions.First(action => string.Equals(action.ActionId, selected, StringComparison.OrdinalIgnoreCase)).LocalActionId)
                    .ToArray();
                try
                {
                    var contributorId = registration.Importer.ContributorId;
                    var result = await registration.Importer.ImportAsync(
                        new StackImportRequest(
                            binding.Fragments,
                            RuntimeStackContractMapper.ToContributorValues(
                                plan.InputValues,
                                RuntimeStackScopedKey.InputKind,
                                registration.PackageId,
                                contributorId),
                            RuntimeStackContractMapper.ToContributorValues(
                                remaps,
                                RuntimeStackScopedKey.RemapKind,
                                registration.PackageId,
                                contributorId),
                            selectedActionIds),
                        linked.Token);
                    var mapped = result.ImportedItems
                        .Select(item => RuntimeStackContractMapper.ToImportedItem(registration.PackageId, contributorId, item))
                        .ToArray();
                    imported.AddRange(mapped);
                    var scopedResultRemaps = RuntimeStackContractMapper.ToHostRemaps(result.IdRemaps, registration.PackageId, contributorId);
                    foreach (var pair in scopedResultRemaps)
                    {
                        remaps[pair.Key] = pair.Value;
                    }

                    var contributorOutcome = result.Outcome switch
                    {
                        StackImportOutcome.Completed => RuntimeStackImportOutcome.Completed,
                        StackImportOutcome.Partial => RuntimeStackImportOutcome.Partial,
                        _ => RuntimeStackImportOutcome.Failed,
                    };
                    var contributorWarnings = result.Warnings.ToList();
                    var contributorErrors = result.Errors.ToList();
                    if (contributorOutcome == RuntimeStackImportOutcome.Failed && mapped.Length > 0)
                    {
                        contributorOutcome = RuntimeStackImportOutcome.Partial;
                        contributorErrors.Add($"Stack importer '{contributorId}' reported Failed with committed items; treating the result as Partial.");
                    }
                    else if (contributorErrors.Count == 0 && contributorOutcome != RuntimeStackImportOutcome.Completed)
                    {
                        contributorErrors.Add($"Stack importer '{contributorId}' reported failure.");
                    }

                    if (mapped.Length > 0)
                    {
                        var appliedContext = new StackImportAppliedContext(
                            registration.PackageId,
                            contributorId,
                            fragmentIds,
                            result.ImportedItems);
                        foreach (var handler in RuntimeStackContributorCatalog.GetImportAppliedHandlers(
                                     _sessions,
                                     lease,
                                     registration.PackageId,
                                     contributorId))
                        {
                            try
                            {
                                await handler.OnStackImportAppliedAsync(appliedContext, linked.Token);
                            }
                            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                            {
                                contributorWarnings.Add($"Package '{registration.PackageId}' cancelled its imported Stack data refresh.");
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                contributorWarnings.Add($"Package '{registration.PackageId}' did not refresh imported Stack data: {ex.Message}");
                            }
                        }
                    }

                    var resultRemaps = new Dictionary<string, string>(scopedResultRemaps, StringComparer.OrdinalIgnoreCase);
                    contributorResults.Add(new RuntimeStackImportContributorResultDescriptor(
                        registration.PackageId,
                        contributorId,
                        fragmentIds,
                        contributorOutcome,
                        mapped,
                        resultRemaps,
                        contributorWarnings,
                        contributorErrors));
                    warnings.AddRange(contributorWarnings);
                    errors.AddRange(contributorErrors);
                }
                catch (Exception) when (!linked.IsCancellationRequested)
                {
                    var message = $"Stack importer '{registration.Importer.ContributorId}' import failed.";
                    contributorResults.Add(new RuntimeStackImportContributorResultDescriptor(
                        registration.PackageId,
                        registration.Importer.ContributorId,
                        fragmentIds,
                        RuntimeStackImportOutcome.Failed,
                        [],
                        new Dictionary<string, string>(),
                        [],
                        [message]));
                    errors.Add(message);
                }
            }

            var outcome = GetOutcome(contributorResults);
            return new RuntimeStackImportResponse(outcome, imported, remaps, contributorResults, warnings, errors);
        }
        finally
        {
            _plans.Release(plan);
        }
    }

    public bool DiscardPlan(string planId)
    {
        return _plans.Discard(planId);
    }

    internal void SweepExpired()
    {
        _plans.SweepExpired();
    }

    public void Dispose()
    {
        _plans.Dispose();
    }

}
