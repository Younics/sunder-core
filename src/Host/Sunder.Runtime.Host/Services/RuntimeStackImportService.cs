using Sunder.Runtime.Contracts;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using static Sunder.Runtime.Host.Services.RuntimeStackImportSupport;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeStackImportService : IDisposable
{
    private readonly RuntimeSessionOwner _sessions;
    private readonly RuntimeContentTransferStore _transfers;
    private readonly ISunderRpcClient _rpcClient;
    private readonly StackImportPlanStore _plans;
    private readonly StackImportArchiveReader _archiveReader = new();
    private readonly TimeProvider _timeProvider;
    private readonly RuntimeTransportPolicyOptions _transportPolicy;

    public RuntimeStackImportService(
        RuntimeSessionOwner sessions,
        RuntimeContentTransferStore transfers,
        RuntimeRpcBroker rpcBroker,
        TimeProvider? timeProvider = null,
        RuntimeStackPolicyOptions? policy = null,
        RuntimeTransportPolicyOptions? transportPolicy = null)
    {
        _sessions = sessions;
        _transfers = transfers;
        _rpcClient = rpcBroker.CreateStackHostClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transportPolicy = transportPolicy ?? new RuntimeTransportPolicyOptions();
        _plans = new StackImportPlanStore(transfers, _timeProvider, policy ?? new RuntimeStackPolicyOptions());
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
            var contributors = await RuntimeStackContributorCatalog.GetImportersAsync(
                _rpcClient,
                errors,
                linked.Token).ConfigureAwait(false);
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
                        registration.ContributorId,
                        fragment)).ToArray());
                RegisteredStackImportFragments? registered = null;
                try
                {
                    registered = await RegisterImportFragmentsAsync(
                        fragments,
                        registration,
                        lease.Generation,
                        linked.Token).ConfigureAwait(false);
                    var preview = await registration.Client.PreviewImportAsync(
                        new StackRpcImportPreviewRequest(
                            registered.Fragments,
                            RuntimeStackContractMapper.ToContributorValues(
                                inputValues,
                                RuntimeStackScopedKey.InputKind,
                                registration.PackageId,
                                registration.ContributorId),
                            RuntimeStackContractMapper.ToContributorValues(
                                idRemaps,
                                RuntimeStackScopedKey.RemapKind,
                                registration.PackageId,
                                registration.ContributorId)),
                        linked.Token).ConfigureAwait(false);
                    if (!RuntimeStackContributorCatalog.ValidatePreviewIds(preview, registration.ContributorId, errors))
                    {
                        continue;
                    }
                    var contributorActions = preview.Actions
                        .Select(value => RuntimeStackContractMapper.ToAction(registration.PackageId, registration.ContributorId, value))
                        .ToArray();
                    actions.AddRange(contributorActions);
                    inputs.AddRange(preview.RequiredInputs.Select(value => RuntimeStackContractMapper.ToRequiredInput(registration.PackageId, registration.ContributorId, value)));
                    conflicts.AddRange(preview.Conflicts.Select(value => RuntimeStackContractMapper.ToConflict(registration.PackageId, registration.ContributorId, value)));
                    warnings.AddRange(preview.Warnings);
                    bindings.Add(new StackContributorBinding(registration, fragments, contributorActions));
                }
                catch (Exception) when (!linked.IsCancellationRequested)
                {
                    errors.Add($"Stack importer '{registration.ContributorId}' preview failed.");
                }
                finally
                {
                    if (registered is not null)
                    {
                        DiscardRegisteredContent(registered, registration, lease.Generation);
                    }
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

            foreach (var binding in plan.Contributors)
            {
                var current = await _rpcClient.GetProviderAsync(
                    binding.Registration.Provider.Endpoint,
                    linked.Token).ConfigureAwait(false);
                if (current != binding.Registration.Provider)
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
                RegisteredStackImportFragments? registered = null;
                try
                {
                    var contributorId = registration.ContributorId;
                    registered = await RegisterImportFragmentsAsync(
                        binding.Fragments,
                        registration,
                        lease.Generation,
                        linked.Token).ConfigureAwait(false);
                    var result = await registration.Client.ImportAsync(
                        new StackRpcImportRequest(
                            registered.Fragments,
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
                        linked.Token).ConfigureAwait(false);
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

                    if (mapped.Length > 0 && registration.Metadata.SupportsImportApplied)
                    {
                        var appliedContext = new StackImportAppliedContext(
                            registration.PackageId,
                            contributorId,
                            fragmentIds,
                            result.ImportedItems);
                        try
                        {
                            await registration.Client.ImportAppliedAsync(appliedContext, linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!linked.IsCancellationRequested)
                        {
                            contributorWarnings.Add($"Package '{registration.PackageId}' cancelled its imported Stack data refresh.");
                        }
                        catch (SunderRpcException) when (linked.IsCancellationRequested)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            throw;
                        }
                        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
                        {
                            contributorWarnings.Add($"Package '{registration.PackageId}' cancelled its imported Stack data refresh.");
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            contributorWarnings.Add($"Package '{registration.PackageId}' did not refresh imported Stack data.");
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
                    var message = $"Stack importer '{registration.ContributorId}' import failed.";
                    contributorResults.Add(new RuntimeStackImportContributorResultDescriptor(
                        registration.PackageId,
                        registration.ContributorId,
                        fragmentIds,
                        RuntimeStackImportOutcome.Failed,
                        [],
                        new Dictionary<string, string>(),
                        [],
                        [message]));
                    errors.Add(message);
                }
                finally
                {
                    if (registered is not null)
                    {
                        DiscardRegisteredContent(registered, registration, lease.Generation);
                    }
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

    private async Task<RegisteredStackImportFragments> RegisterImportFragmentsAsync(
        IReadOnlyList<StackFragmentImport> fragments,
        StackContributorRegistration registration,
        long generation,
        CancellationToken cancellationToken)
    {
        var references = new List<SunderRpcContentReference>();
        try
        {
            var rpcFragments = new List<StackRpcFragmentImport>(fragments.Count);
            foreach (var fragment in fragments)
            {
                var files = new List<StackRpcPayloadFile>();
                foreach (var file in fragment.Files ?? [])
                {
                    await using var source = await file.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                    var content = await _transfers.RegisterRpcContentAsync(
                        source,
                        file.Length,
                        "application/octet-stream",
                        Path.GetFileName(file.RelativePath),
                        RuntimeRpcHostCallerActivation.StackPrincipalId,
                        registration.PackageId,
                        generation,
                        _timeProvider.GetUtcNow() + _transportPolicy.ContentTransferLifetime,
                        SunderRpcContentRepeatability.Repeatable,
                        _transportPolicy.MaxRpcContentUses,
                        cancellationToken).ConfigureAwait(false);
                    references.Add(content);
                    files.Add(new StackRpcPayloadFile(file.RelativePath, content));
                }
                rpcFragments.Add(new StackRpcFragmentImport(
                    fragment.FragmentId,
                    registration.PackageId,
                    registration.ContributorId,
                    fragment.SchemaId,
                    fragment.SchemaVersion,
                    fragment.DisplayName,
                    fragment.JsonPayload,
                    fragment.Description,
                    files.Count == 0 ? null : files));
            }
            return new RegisteredStackImportFragments(rpcFragments, references);
        }
        catch
        {
            foreach (var reference in references)
            {
                _transfers.DiscardRpcContent(
                    reference,
                    RuntimeRpcHostCallerActivation.StackPrincipalId,
                    registration.PackageId,
                    generation);
            }
            throw;
        }
    }

    private void DiscardRegisteredContent(
        RegisteredStackImportFragments registered,
        StackContributorRegistration registration,
        long generation)
    {
        foreach (var reference in registered.References)
        {
            _transfers.DiscardRpcContent(
                reference,
                RuntimeRpcHostCallerActivation.StackPrincipalId,
                registration.PackageId,
                generation);
        }
    }

}

internal sealed record RegisteredStackImportFragments(
    IReadOnlyList<StackRpcFragmentImport> Fragments,
    IReadOnlyList<SunderRpcContentReference> References);
