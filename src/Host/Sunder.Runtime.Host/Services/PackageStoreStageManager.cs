using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageStoreStageManager(
    InstalledPackageStore store,
    SunderPackageArchiveInstaller archiveInstaller)
{
    private readonly Dictionary<string, PendingStoreStage> _stages = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    internal async Task<PackageStoreStagePreparation> PrepareAsync(
        IReadOnlyList<PackageStoreMutation> mutations,
        long catalogGeneration,
        CancellationToken cancellationToken)
    {
        if (mutations.Count == 0)
        {
            return new PackageStoreStagePreparation(
                null,
                PackageOperationResults.Failure("At least one package store mutation is required."));
        }

        var current = (await store.ListAsync(cancellationToken)).ToArray();
        var desired = current.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var prospective = current.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var prepared = new Dictionary<string, PreparedPackageArchiveMutation>(StringComparer.OrdinalIgnoreCase);
        var impacted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();

        try
        {
            foreach (var mutation in mutations.Where(IsArchiveMutation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var selected = string.IsNullOrWhiteSpace(mutation.PackageId)
                    ? null
                    : desired.GetValueOrDefault(mutation.PackageId);
                var isUpgrade = mutation.Kind == PackageStoreMutationKind.Upgrade;
                var preparation = await archiveInstaller.PrepareAsync(
                    mutation.ArchiveFilePath ?? string.Empty,
                    selected?.IsEnabled ?? true,
                    cancellationToken,
                    mutation.Provenance);
                if (!preparation.Success || preparation.Mutation is null)
                {
                    return FailResult(preparation.Failure
                        ?? PackageOperationResults.Failure("Package archive staging failed."));
                }

                var archive = preparation.Mutation;
                if (isUpgrade
                    && !string.Equals(archive.InstalledRecord.PackageId, mutation.PackageId, StringComparison.OrdinalIgnoreCase))
                {
                    SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
                    return Fail($"Package archive '{archive.InstalledRecord.PackageId}' does not match selected package '{mutation.PackageId}'.");
                }

                if (isUpgrade && selected is null)
                {
                    SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
                    return Fail($"Package '{mutation.PackageId}' is not installed.");
                }

                if (!isUpgrade && desired.ContainsKey(archive.InstalledRecord.PackageId))
                {
                    SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
                    return Fail($"Package '{archive.InstalledRecord.PackageId}' is already installed.");
                }

                if (!prepared.TryAdd(archive.InstalledRecord.PackageId, archive))
                {
                    SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
                    return Fail($"Package '{archive.InstalledRecord.PackageId}' is mutated by more than one archive in the same transaction.");
                }

                if (isUpgrade)
                {
                    var versionError = PackageStorePolicy.ValidateReplacementVersion(
                        selected!,
                        archive.InstalledRecord,
                        mutation.AllowDowngrade,
                        mutation.Reinstall);
                    if (versionError is not null)
                    {
                        return Fail(versionError);
                    }

                    archive = archive with
                    {
                        InstalledRecord = archive.InstalledRecord with { IsEnabled = selected!.IsEnabled },
                    };
                    prepared[archive.InstalledRecord.PackageId] = archive;
                    messages.Add($"Updated package '{archive.InstalledRecord.Name}' from {selected.Version} to {archive.InstalledRecord.Version}.");
                }
                else
                {
                    messages.Add($"Installed package '{archive.InstalledRecord.Name}' {archive.InstalledRecord.Version}.");
                }

                desired[archive.InstalledRecord.PackageId] = archive.InstalledRecord;
                prospective[archive.InstalledRecord.PackageId] = archive.InstalledRecord;
                impacted.Add(archive.InstalledRecord.PackageId);
            }

            foreach (var mutation in mutations.Where(mutation => !IsArchiveMutation(mutation)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(mutation.PackageId))
                {
                    return Fail("Package id is required.");
                }

                switch (mutation.Kind)
                {
                    case PackageStoreMutationKind.Enable:
                    case PackageStoreMutationKind.Disable:
                        if (!desired.TryGetValue(mutation.PackageId, out var package))
                        {
                            return Fail($"Package '{mutation.PackageId}' is not installed.");
                        }

                        var enabled = mutation.Kind == PackageStoreMutationKind.Enable;
                        if (package.IsEnabled == enabled)
                        {
                            messages.Add($"Package '{package.Name}' is already {(enabled ? "enabled" : "disabled")}.");
                            break;
                        }

                        desired[package.PackageId] = package with { IsEnabled = enabled };
                        prospective[package.PackageId] = prospective[package.PackageId] with { IsEnabled = enabled };
                        impacted.Add(package.PackageId);
                        messages.Add($"{(enabled ? "Enabled" : "Disabled")} package '{package.Name}'.");
                        break;
                    case PackageStoreMutationKind.Uninstall:
                        if (!desired.TryGetValue(mutation.PackageId, out package))
                        {
                            return Fail($"Package '{mutation.PackageId}' is not installed.");
                        }

                        var uninstallPlan = PackageStorePolicy.CreateUninstallPlan(package.PackageId, desired.Values);
                        if (uninstallPlan.RequiresCascadeConsent && !mutation.AllowCascade)
                        {
                            return Fail(
                                $"Uninstalling package '{package.PackageId}' would also remove: {string.Join(", ", uninstallPlan.CascadingRemovals.Select(removal => removal.PackageId))}. Explicit cascade consent is required.");
                        }
                        if ((uninstallPlan.RequiresCascadeConsent || mutation.ConfirmationToken is not null)
                            && !string.Equals(
                                mutation.ConfirmationToken,
                                uninstallPlan.ConfirmationToken,
                                StringComparison.Ordinal))
                        {
                            return Fail(
                                $"The uninstall plan for package '{package.PackageId}' is missing or stale. Request a new uninstall plan before committing.");
                        }

                        var removalIds = uninstallPlan.ExpectedRemovalPackageIds;
                        foreach (var packageId in removalIds)
                        {
                            desired.Remove(packageId);
                            prospective.Remove(packageId);
                            impacted.Add(packageId);
                        }

                        messages.Add(removalIds.Count == 1
                            ? $"Uninstalled package '{package.Name}'."
                            : $"Uninstalled package '{package.Name}' and {removalIds.Count - 1} dependent package(s).");
                        break;
                    default:
                        return Fail($"Unsupported package store mutation kind '{mutation.Kind}'.");
                }
            }

            var desiredPackages = desired.Values.OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase).ToArray();
            var validationErrors = PackageStorePolicy.ValidateCatalog(store, desiredPackages);
            if (validationErrors.Count > 0)
            {
                return FailResult(PackageOperationResults.Failure(validationErrors[0], validationErrors));
            }

            foreach (var replacedPackageId in prepared.Keys.Where(packageId => !desired.ContainsKey(packageId)))
            {
                SunderPackageArchiveInstaller.TryDeleteDirectory(prepared[replacedPackageId].StagingPath);
            }

            PackageStorePolicy.AddImpactedDependents(impacted, desiredPackages);
            var removedPackageIds = current
                .Where(package => !desired.ContainsKey(package.PackageId))
                .Select(package => package.PackageId)
                .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var result = PackageOperationResults.Success(
                PackageStorePolicy.BuildMessage(messages, impacted.Count),
                impactedPackageIds: impacted.OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase).ToArray(),
                removedPackageIds: removedPackageIds);
            var stageId = Guid.NewGuid().ToString("N");
            var stage = new PendingStoreStage(
                stageId,
                current,
                desiredPackages,
                prospective.Values.OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase).ToArray(),
                prepared.Values.Where(archive => desired.ContainsKey(archive.InstalledRecord.PackageId)).ToArray(),
                result,
                catalogGeneration);
            lock (_sync)
            {
                _stages.Add(stageId, stage);
            }

            return new PackageStoreStagePreparation(
                new PackageStorePreparedStage(
                    stageId,
                    stage.ProspectivePackages,
                    stage.PreparedArchives.ToDictionary(
                        static archive => archive.InstalledRecord.PackageId,
                        static archive => archive.StagingPath,
                        StringComparer.OrdinalIgnoreCase),
                    result,
                    catalogGeneration),
                null);
        }
        catch
        {
            Cleanup(prepared.Values);
            throw;
        }

        PackageStoreStagePreparation Fail(string message) => FailResult(PackageOperationResults.Failure(message));

        PackageStoreStagePreparation FailResult(PackageOperationResult failure)
        {
            Cleanup(prepared.Values);
            return new PackageStoreStagePreparation(null, failure);
        }
    }

    internal bool TryTake(string stageId, out PendingStoreStage stage)
    {
        lock (_sync)
        {
            return _stages.Remove(stageId, out stage!);
        }
    }

    internal bool Discard(string stageId)
    {
        if (!TryTake(stageId, out var stage))
        {
            return false;
        }

        Cleanup(stage.PreparedArchives);
        return true;
    }

    internal void DiscardAll()
    {
        PendingStoreStage[] stages;
        lock (_sync)
        {
            stages = _stages.Values.ToArray();
            _stages.Clear();
        }

        foreach (var stage in stages)
        {
            Cleanup(stage.PreparedArchives);
        }
    }

    internal static void Cleanup(IEnumerable<PreparedPackageArchiveMutation> archives)
    {
        foreach (var archive in archives)
        {
            SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
        }
    }

    private static bool IsArchiveMutation(PackageStoreMutation mutation) =>
        mutation.Kind is PackageStoreMutationKind.Install or PackageStoreMutationKind.Upgrade;
}
