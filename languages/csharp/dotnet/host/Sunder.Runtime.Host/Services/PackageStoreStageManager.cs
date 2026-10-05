using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageStoreStageManager(
    InstalledPackageStore store,
    SunderPackageArchiveInstaller archiveInstaller,
    PackageLifecyclePolicyCache? packagePolicies = null)
{
    private readonly Dictionary<string, PendingStoreStage> _stages = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    internal bool HasStages { get { lock (_sync) return _stages.Count > 0; } }

    internal async Task<PackageStoreStagePreparation> PrepareAsync(
        IReadOnlyList<PackageStoreChange> mutations,
        long catalogGeneration,
        LocalExecutionRuntimeBindingSnapshot? localBindingAuthority,
        IReadOnlyList<ExecutionRuntimeLifecyclePolicyEntryV1> registryRuntimePolicyUpdates,
        ExecutionRuntimeLifecyclePolicySnapshot? registryRuntimePolicyAuthority,
        CancellationToken cancellationToken)
    {
        if (mutations.Count == 0 && registryRuntimePolicyUpdates.Count == 0)
        {
            return new PackageStoreStagePreparation(
                null,
                PackageOperationResults.Failure("At least one package store mutation is required."));
        }

        var current = (await store.ListAsync(cancellationToken)).ToArray();
        var desired = current.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var prospective = current.ToDictionary(package => package.PackageId, StringComparer.OrdinalIgnoreCase);
        var prepared = new Dictionary<string, PreparedPackageArchiveMutation>(StringComparer.OrdinalIgnoreCase);
        var ownedArchives = new List<PreparedPackageArchiveMutation>();
        var mutatedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var impacted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        var packageMutations = mutations.OfType<PackageStoreMutation>().ToArray();
        var runtimeRebinds = mutations.OfType<PackageExecutionRuntimeRebindMutation>().ToArray();
        var runtimePolicyBindingUpdates = mutations.OfType<PackageExecutionRuntimePolicyUpdateMutation>().ToArray();
        var packagePolicyUpdates = mutations.OfType<PackageLifecyclePolicyUpdateMutation>().ToArray();
        var localPolicyIdentities = new Dictionary<string, ExecutionRuntimeIdentity>(StringComparer.Ordinal);
        if (packageMutations.Length + runtimeRebinds.Length + runtimePolicyBindingUpdates.Length + packagePolicyUpdates.Length != mutations.Count)
        {
            return new PackageStoreStagePreparation(
                null,
                PackageOperationResults.Failure("The package store change type is unsupported."));
        }

        try
        {
            foreach (var mutation in packageMutations.Where(IsArchiveMutation))
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
                    mutation.Provenance,
                    mutation.ConsentManifestSha256,
                    mutation.RegistryProjectionTargets);
                if (!preparation.Success || preparation.Mutation is null)
                {
                    return FailResult(preparation.Failure
                        ?? PackageOperationResults.Failure("Package archive staging failed."));
                }

                var archive = preparation.Mutation;
                ownedArchives.Add(archive);
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

                if (!mutatedPackageIds.Add(archive.InstalledRecord.PackageId))
                {
                    SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
                    return Fail($"Package '{archive.InstalledRecord.PackageId}' is mutated more than once in the same transaction.");
                }

                var exactBindings = mutation.ExecutionRuntimeBindings;
                if (exactBindings is null && localBindingAuthority is not null)
                {
                    exactBindings = await localBindingAuthority.ResolveInstalledAsync(
                            archive.Manifest,
                            cancellationToken,
                            InstalledPackageProjection.Keys(archive.InstalledRecord))
                        .ConfigureAwait(false);
                    foreach (var binding in exactBindings)
                    {
                        var identity = new ExecutionRuntimeIdentity(
                            binding.RuntimeId,
                            binding.RuntimeVersion,
                            binding.RuntimePackagingRevision,
                            binding.Rid,
                            binding.ModuleAbi);
                        localPolicyIdentities.TryAdd(identity.SingleFlightKey, identity);
                    }
                }
                if (exactBindings is not null)
                {
                    archive = archive with
                    {
                        InstalledRecord = archive.InstalledRecord with
                        {
                            ExecutionRuntimeBindings = exactBindings,
                        },
                    };
                    try
                    {
                        _ = RegistryPackagePlanResolver.ProjectRuntimeBindings(
                            archive.InstalledRecord,
                            archive.Manifest);
                    }
                    catch (InvalidDataException exception)
                    {
                        SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
                        return Fail(exception.Message);
                    }
                }

                var consentError = PackageSensitiveCapabilityPolicy.AuthorizeCandidate(
                    archive,
                    selected,
                    mutation.CapabilityConsent,
                    out var capabilityGrants);
                if (consentError is not null)
                {
                    SunderPackageArchiveInstaller.TryDeleteDirectory(archive.StagingPath);
                    return Fail(consentError);
                }
                archive = archive with
                {
                    InstalledRecord = archive.InstalledRecord with
                    {
                        CapabilityGrants = capabilityGrants,
                        Compatibility = !PackageCompatibilityAuthority.NeedsEvidence(archive.Manifest) ? null
                            : mutation.ResolutionPolicy is { } resolution ? new InstalledPackageCompatibilityRecord(
                                InstalledPackagePolicyRecord.Subject(archive.InstalledRecord), RegistryActivePolicyClient.Origin(mutation.Provenance!.RegistryOrigin!), resolution)
                            : selected?.Compatibility is { } retained && retained.Package == InstalledPackagePolicyRecord.Subject(archive.InstalledRecord) ? retained : null,
                    },
                };

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
                        InstalledRecord = archive.InstalledRecord with
                        {
                            IsEnabled = selected!.IsEnabled,
                        },
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

            foreach (var mutation in packageMutations.Where(mutation => !IsArchiveMutation(mutation)))
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

            var bindingUpdates = runtimeRebinds
                .Select(static mutation => (
                    mutation.PackageId,
                    mutation.ExpectedPackage,
                    mutation.DesiredBindings,
                    MetadataOnly: false))
                .Concat(runtimePolicyBindingUpdates.Select(static mutation => (
                    mutation.PackageId,
                    mutation.ExpectedPackage,
                    mutation.DesiredBindings,
                    MetadataOnly: true)));
            foreach (var update in bindingUpdates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(update.PackageId)
                    || update.ExpectedPackage is null
                    || update.DesiredBindings is null)
                {
                    return Fail("A Registry execution-runtime rebind is incomplete.");
                }
                if (!desired.TryGetValue(update.PackageId, out var package))
                {
                    return Fail($"Package '{update.PackageId}' is not installed.");
                }
                if (!mutatedPackageIds.Add(package.PackageId))
                {
                    return Fail($"Package '{package.PackageId}' is mutated more than once in the same transaction.");
                }
                if (!PackageStorePolicy.CatalogBindingsEqual([package], [update.ExpectedPackage]))
                {
                    return Fail($"Registry execution-runtime rebind for package '{package.PackageId}' is stale.");
                }
                if (package.ExecutionRuntimeBindings.SequenceEqual(update.DesiredBindings))
                {
                    return Fail($"Registry execution-runtime rebind for package '{package.PackageId}' does not change its exact binding.");
                }
                if (update.MetadataOnly
                    && !IsLifecyclePolicyOnlyUpdate(
                        package.ExecutionRuntimeBindings,
                        update.DesiredBindings))
                {
                    return Fail(
                        $"Registry lifecycle policy update for package '{package.PackageId}' changed immutable execution-runtime binding evidence.");
                }

                var validation = await InstalledPackageProjection.ValidateAsync(package, cancellationToken);
                if (!validation.Success || validation.Manifest is null || validation.ContentIndex is null
                    || !PackageSessionPreparer.InventoryMatches(validation.ContentIndex, package.ContentInventory)
                    || !string.Equals(
                        PackageSessionPreparer.ComputeContentIdentity(package.InstallPath),
                        package.ContentIdentity,
                        StringComparison.Ordinal))
                {
                    return Fail(
                        $"Installed package '{package.PackageId}' cannot be rebound because its strict package content is invalid.");
                }

                var rebound = package with { ExecutionRuntimeBindings = update.DesiredBindings };
                try
                {
                    _ = RegistryPackagePlanResolver.ProjectRuntimeBindings(rebound, validation.Manifest);
                }
                catch (InvalidDataException exception)
                {
                    return Fail(exception.Message);
                }

                desired[package.PackageId] = rebound;
                prospective[package.PackageId] = rebound;
                if (update.MetadataOnly)
                {
                    messages.Add($"Updated execution-runtime lifecycle evidence for package '{package.Name}'.");
                }
                else
                {
                    impacted.Add(package.PackageId);
                    messages.Add($"Rebound package '{package.Name}' to its resolved execution runtime.");
                }
            }

            foreach (var update in packagePolicyUpdates)
            {
                if (!desired.TryGetValue(update.PackageId, out var package) || !mutatedPackageIds.Add(update.PackageId)
                    || !PackageStorePolicy.CatalogsEqual([package], [update.ExpectedPackage]))
                    return Fail($"Package policy update for '{update.PackageId}' is stale or duplicated.");
                update.DesiredPolicy?.Validate(package);
                desired[update.PackageId] = prospective[update.PackageId] = update.DesiredPolicy?.ApplyTo(package) ?? package with { Policy = null };
                messages.Add($"Updated package policy evidence for '{package.Name}'.");
            }

            IReadOnlyList<InstalledPackagePolicyRecord> policyAuthority = [];
            if (packagePolicies is not null && (packageMutations.Length > 0 || runtimeRebinds.Length > 0 || runtimePolicyBindingUpdates.Length > 0))
            {
                var captured = await packagePolicies.CaptureInstalledAsync(desired.Values.ToArray(), cancellationToken);
                var required = prepared.Keys.Concat(runtimeRebinds.Select(value => value.PackageId))
                    .Concat(packageMutations.Where(value => value.Kind == PackageStoreMutationKind.Enable).Select(value => value.PackageId!))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var package in captured)
                {
                    if (required.Contains(package.PackageId) && package.Policy is { IsBlocked: true } denied) return Fail(denied.ErrorCode!);
                    desired[package.PackageId] = prospective[package.PackageId] = package;
                    if (prepared.TryGetValue(package.PackageId, out var archive))
                    {
                        if (package.Policy is { State: "Allowed", RegistryOrigin: { } origin } policy)
                        {
                            var authoritative = policy.Snapshots.Single(value => value.Origin == origin).Packages[RegistryActivePolicyClient.PackageKey(policy.Package)];
                            if (!PackagePolicyAdmission.Platforms(archive.Manifest).SequenceEqual(authoritative.Decision.PlatformDependencies))
                                return Fail("Package platform provenance differs from its authoritative policy.");
                        }
                        prepared[package.PackageId] = archive with { InstalledRecord = package };
                    }
                }
                if (packageMutations.Length > 0 || runtimeRebinds.Length > 0)
                    policyAuthority = captured.Where(package => package.Policy is { State: "Allowed" } && (package.IsEnabled || required.Contains(package.PackageId)))
                        .Select(package => package.Policy!).ToArray();
            }
            var graphSources = new List<RuntimePackageSource>();
            if (desired.Values.Any(package => package.Compatibility is not null)
                || prepared.Values.Any(archive => PackageCompatibilityAuthority.NeedsEvidence(archive.Manifest)))
            {
                foreach (var package in desired.Values)
                {
                    var source = prepared.TryGetValue(package.PackageId, out var archive)
                        ? PackagePolicyAdmission.WithProvenance(new(package.PackageId, PackageSourceKind.Installed, archive.StagingPath,
                            ContentIdentity: package.ContentIdentity, Manifest: archive.Manifest), package)
                        : await PackageCompatibilityAuthority.ReadSourceAsync(package, cancellationToken);
                    graphSources.Add(source);
                }
                PackageCompatibilityAuthority.ValidateGraph(graphSources);
                foreach (var source in graphSources)
                {
                    var package = desired[source.PackageId] with { DependsOn = PackageCompatibilityAuthority.EffectiveDependencies(source)
                        .Select(value => new InstalledPackageDependencyRecord(value.PackageId, value.VersionRange)).ToArray() };
                    desired[source.PackageId] = prospective[source.PackageId] = package;
                    if (prepared.TryGetValue(source.PackageId, out var archive)) prepared[source.PackageId] = archive with { InstalledRecord = package };
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
            var activationPolicyIdentities = prospective.Values
                .Where(static package => package.IsEnabled)
                .SelectMany(static package => package.ExecutionRuntimeBindings)
                .Select(static binding => new ExecutionRuntimeIdentity(
                    binding.RuntimeId,
                    binding.RuntimeVersion,
                    binding.RuntimePackagingRevision,
                    binding.Rid,
                    binding.ModuleAbi))
                .DistinctBy(static identity => identity.SingleFlightKey, StringComparer.Ordinal)
                .OrderBy(static identity => identity.SingleFlightKey, StringComparer.Ordinal)
                .ToArray();
            var stageId = Guid.NewGuid().ToString("N");
            var stage = new PendingStoreStage(
                stageId,
                current,
                desiredPackages,
                prospective.Values.OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase).ToArray(),
                prepared.Values.Where(archive => desired.ContainsKey(archive.InstalledRecord.PackageId)).ToArray(),
                result,
                catalogGeneration,
                Array.AsReadOnly(registryRuntimePolicyUpdates.ToArray()),
                registryRuntimePolicyAuthority,
                localBindingAuthority,
                localBindingAuthority is null
                    || (localPolicyIdentities.Count == 0 && activationPolicyIdentities.Length == 0)
                    ? null
                    : new LocalExecutionRuntimePolicyAuthority(
                        localBindingAuthority.PolicyGeneration,
                        Array.AsReadOnly(localPolicyIdentities.Values
                            .OrderBy(static identity => identity.SingleFlightKey, StringComparer.Ordinal)
                            .ToArray()),
                        Array.AsReadOnly(activationPolicyIdentities)),
                policyAuthority);
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
                    catalogGeneration,
                    stage.LocalRuntimePolicyAuthority,
                    stage.RegistryRuntimePolicyAuthority),
                null);
        }
        catch
        {
            Cleanup(ownedArchives);
            throw;
        }

        PackageStoreStagePreparation Fail(string message) => FailResult(PackageOperationResults.Failure(message));

        PackageStoreStagePreparation FailResult(PackageOperationResult failure)
        {
            Cleanup(ownedArchives);
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

    internal bool TryGet(string stageId, out PendingStoreStage stage)
    {
        lock (_sync)
        {
            return _stages.TryGetValue(stageId, out stage!);
        }
    }

    internal async Task<bool> DiscardAsync(string stageId)
    {
        if (!TryTake(stageId, out var stage))
        {
            return false;
        }

        Cleanup(stage.PreparedArchives);
        if (stage.LocalBindingAuthority is not null)
        {
            await stage.LocalBindingAuthority.DisposeAsync().ConfigureAwait(false);
        }
        return true;
    }

    internal async Task<IReadOnlyList<string>> DiscardAllAsync()
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
            if (stage.LocalBindingAuthority is not null)
            {
                await stage.LocalBindingAuthority.DisposeAsync().ConfigureAwait(false);
            }
        }
        return Array.AsReadOnly(stages.Select(static stage => stage.StageId).ToArray());
    }

    internal IReadOnlyList<InstalledPackageRecord> CaptureGarbageCollectionAuthority()
    {
        lock (_sync)
        {
            return Array.AsReadOnly(_stages.Values
                .SelectMany(static stage => stage.PreviousPackages.Concat(stage.DesiredPackages))
                .ToArray());
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

    private static bool IsLifecyclePolicyOnlyUpdate(
        IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> current,
        IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> desired)
    {
        if (current.Count != desired.Count)
        {
            return false;
        }
        var currentByTarget = current.ToDictionary(
            static binding => $"{binding.Role}\0{binding.Rid}",
            StringComparer.Ordinal);
        return desired.All(binding =>
            currentByTarget.TryGetValue($"{binding.Role}\0{binding.Rid}", out var existing)
            && string.Equals(existing.RuntimeId, binding.RuntimeId, StringComparison.Ordinal)
            && string.Equals(existing.RuntimeVersion, binding.RuntimeVersion, StringComparison.Ordinal)
            && existing.RuntimePackagingRevision == binding.RuntimePackagingRevision
            && string.Equals(existing.ModuleAbi, binding.ModuleAbi, StringComparison.Ordinal)
            && string.Equals(existing.RuntimeManifestSha256, binding.RuntimeManifestSha256, StringComparison.Ordinal)
            && string.Equals(existing.RuntimeArtifactSha256, binding.RuntimeArtifactSha256, StringComparison.Ordinal)
            && existing.RuntimeArtifactSize == binding.RuntimeArtifactSize
            && string.Equals(existing.RegistryOrigin, binding.RegistryOrigin, StringComparison.Ordinal)
            && string.Equals(existing.AcquisitionSource, binding.AcquisitionSource, StringComparison.Ordinal)
            && string.Equals(existing.SigningKeyId, binding.SigningKeyId, StringComparison.Ordinal)
            && existing.TrustStoreGeneration == binding.TrustStoreGeneration);
    }
}
