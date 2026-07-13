using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;

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

            var contributors = GetContributors(lease);
            var inputValues = Copy(request.InputValues);
            var idRemaps = Copy(request.IdRemaps);
            var actions = new List<RuntimeStackImportActionDescriptor>();
            var inputs = new List<RuntimeStackRequiredInputDescriptor>();
            var conflicts = new List<RuntimeStackImportConflictDescriptor>();
            var warnings = loaded.Warnings.ToList();
            var errors = new List<string>();
            var bindings = new List<StackContributorBinding>();
            foreach (var group in loaded.Fragments.GroupBy(
                         fragment => Key(fragment.OwnerPackageId, fragment.ContributorId),
                         StringComparer.OrdinalIgnoreCase))
            {
                var first = group.First();
                if (!contributors.TryGetValue(group.Key, out var registration))
                {
                    errors.Add($"No active Stack contributor '{first.ContributorId}' from package '{first.OwnerPackageId}' is available.");
                    continue;
                }

                var fragments = Array.AsReadOnly(group.ToArray());
                try
                {
                    var preview = await registration.Contributor.PreviewImportAsync(
                        new StackImportPreviewRequest(fragments, inputValues, idRemaps),
                        linked.Token);
                    var contributorActions = preview.Actions
                        .Select(value => RuntimeStackContractMapper.ToAction(registration.PackageId, registration.Contributor.ContributorId, value))
                        .ToArray();
                    actions.AddRange(contributorActions);
                    inputs.AddRange(preview.RequiredInputs.Select(value => RuntimeStackContractMapper.ToRequiredInput(registration.PackageId, registration.Contributor.ContributorId, value)));
                    conflicts.AddRange(preview.Conflicts.Select(value => RuntimeStackContractMapper.ToConflict(registration.PackageId, registration.Contributor.ContributorId, value)));
                    warnings.AddRange(preview.Warnings);
                    bindings.Add(new StackContributorBinding(registration, fragments, contributorActions));
                }
                catch (Exception) when (!linked.IsCancellationRequested)
                {
                    errors.Add($"Stack contributor '{registration.Contributor.ContributorId}' preview failed.");
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

            var currentContributors = GetContributors(lease);
            foreach (var binding in plan.Contributors)
            {
                var key = Key(binding.Registration.PackageId, binding.Registration.Contributor.ContributorId);
                if (!currentContributors.TryGetValue(key, out var current)
                    || !ReferenceEquals(current.Contributor, binding.Registration.Contributor))
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
                    .ToArray();
                try
                {
                    var result = await registration.Contributor.ImportAsync(
                        new StackImportRequest(binding.Fragments, plan.InputValues, Copy(remaps), selectedActionIds),
                        linked.Token);
                    var mapped = result.ImportedItems
                        .Select(item => RuntimeStackContractMapper.ToImportedItem(registration.PackageId, registration.Contributor.ContributorId, item))
                        .ToArray();
                    imported.AddRange(mapped);
                    foreach (var pair in result.IdRemaps)
                    {
                        remaps[pair.Key] = pair.Value;
                    }

                    var contributorOutcome = result.Outcome switch
                    {
                        StackImportOutcome.Completed => RuntimeStackImportOutcome.Completed,
                        StackImportOutcome.Partial => RuntimeStackImportOutcome.Partial,
                        _ => RuntimeStackImportOutcome.Failed,
                    };
                    var contributorErrors = result.Errors.Count > 0 || contributorOutcome == RuntimeStackImportOutcome.Completed
                        ? result.Errors
                        : [$"Stack contributor '{registration.Contributor.ContributorId}' reported failure."];
                    var resultRemaps = new Dictionary<string, string>(result.IdRemaps, StringComparer.OrdinalIgnoreCase);
                    contributorResults.Add(new RuntimeStackImportContributorResultDescriptor(
                        registration.PackageId,
                        registration.Contributor.ContributorId,
                        fragmentIds,
                        contributorOutcome,
                        mapped,
                        resultRemaps,
                        result.Warnings,
                        contributorErrors));
                    warnings.AddRange(result.Warnings);
                    errors.AddRange(contributorErrors);
                }
                catch (Exception) when (!linked.IsCancellationRequested)
                {
                    var message = $"Stack contributor '{registration.Contributor.ContributorId}' import failed.";
                    contributorResults.Add(new RuntimeStackImportContributorResultDescriptor(
                        registration.PackageId,
                        registration.Contributor.ContributorId,
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

    private Dictionary<string, StackContributorRegistration> GetContributors(PackageSessionLease lease)
        => _sessions.State.GetExtensionContributions(lease, SunderStackExtensionPoints.StackContributors)
            .Where(value => !string.IsNullOrWhiteSpace(value.PackageId) && !string.IsNullOrWhiteSpace(value.Contribution.ContributorId))
            .GroupBy(value => Key(value.PackageId, value.Contribution.ContributorId), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new StackContributorRegistration(group.First().PackageId, group.First().Contribution), StringComparer.OrdinalIgnoreCase);

    private static string? ValidateSelectedFragments(IReadOnlyList<string> requested, IReadOnlyList<string> planned)
    {
        var plannedSet = planned.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = requested
            .Where(id => string.IsNullOrWhiteSpace(id) || !plannedSet.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length > 0)
        {
            return $"Stack import selected unknown fragment id(s): {string.Join(", ", unknown)}.";
        }

        var requestedSet = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return requestedSet.SetEquals(plannedSet)
            ? null
            : "Stack import fragment selection does not match the previewed plan.";
    }

    private static RuntimeStackImportOutcome GetOutcome(IReadOnlyList<RuntimeStackImportContributorResultDescriptor> results)
    {
        if (results.Count > 0 && results.All(result => result.Outcome == RuntimeStackImportOutcome.Completed))
        {
            return RuntimeStackImportOutcome.Completed;
        }

        return results.Any(result => result.Outcome is RuntimeStackImportOutcome.Completed or RuntimeStackImportOutcome.Partial)
            ? RuntimeStackImportOutcome.Partial
            : RuntimeStackImportOutcome.Failed;
    }

    private static RuntimeStackImportPreviewResponse PreviewFailure(string error)
        => PreviewFailure([], [error]);

    private static RuntimeStackImportPreviewResponse PreviewFailure(
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> errors)
        => new(false, null, null, [], [], [], warnings, errors);

    private static RuntimeStackImportResponse ImportFailure(
        string error,
        IReadOnlyDictionary<string, string>? idRemaps = null)
        => new(
            RuntimeStackImportOutcome.Failed,
            [],
            idRemaps ?? new Dictionary<string, string>(),
            [],
            [],
            [error]);

    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> source)
        => new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase));

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string Key(string? packageId, string contributorId)
        => string.IsNullOrWhiteSpace(packageId) ? string.Empty : packageId.Trim() + "\u001f" + contributorId.Trim();

    private static string CreatePlanId()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string CreateStagingPath()
        => Path.Combine(Path.GetTempPath(), "Sunder.Stacks", "V1", "runtime", Guid.NewGuid().ToString("N"));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

}
