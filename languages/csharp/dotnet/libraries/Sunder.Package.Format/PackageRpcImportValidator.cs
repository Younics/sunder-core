using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

internal static class PackageRpcImportValidator
{
    private const int MaxEntries = 128;

    public static void Validate(SunderPackageManifest manifest, ICollection<string> errors)
    {
        if (manifest is null)
        {
            errors.Add("Package manifest is null.");
            return;
        }
        var snapshots = new Dictionary<string, AuthoritySnapshot>(StringComparer.Ordinal);
        var bundles = new Dictionary<string, SunderPackageContractBundleManifest>(StringComparer.Ordinal);
        var bundleSnapshots = new Dictionary<string, AuthoritySnapshot>(StringComparer.Ordinal);
        var importedVersions = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var bundle in Entries(manifest.ContractBundles, "contractBundles", errors, optional: true))
        {
            var key = Key(bundle.ContractId, bundle.Version);
            bundles.TryAdd(key, bundle);
            if (bundle.Source is not ("owned" or "imported"))
            {
                errors.Add($"Contract bundle '{bundle.ContractId}' must declare source as 'owned' or 'imported'.");
            }
            if (bundle.Source == "owned")
            {
                if (bundle.AuthorityKind != "package"
                    || !PackageId.TryParse(bundle.AuthorityId, out _)
                    || bundle.AuthorityId != manifest.Id)
                {
                    errors.Add($"Owned contract bundle '{bundle.ContractId}' must declare package authority equal to the manifest id.");
                }
                if (bundle.SelectedAuthorityRelease is not null || bundle.SelectedAuthorityContentIdentity is not null)
                {
                    errors.Add($"Owned contract bundle '{bundle.ContractId}' forbids selected-authority evidence.");
                }
                continue;
            }

            if (bundle.Source == "imported" && bundle.ContractId is not null
                && !importedVersions.TryAdd(bundle.ContractId, bundle.Version)
                && importedVersions[bundle.ContractId] != bundle.Version)
            {
                errors.Add($"Imported contract id '{bundle.ContractId}' must materialize only one exact version across all imports.");
            }
            var snapshot = new AuthoritySnapshot(bundle.AuthorityKind, bundle.AuthorityId,
                bundle.SelectedAuthorityRelease, bundle.SelectedAuthorityContentIdentity);
            ValidateAuthority(snapshot, $"Contract bundle '{bundle.ContractId}'");
            bundleSnapshots.TryAdd(key, snapshot);
            if (bundle.Exposure != "public")
            {
                errors.Add($"Imported contract bundle '{bundle.ContractId}' must have public exposure.");
            }
        }

        var uses = new Dictionary<string, SunderPackageContractUseManifest>(StringComparer.Ordinal);
        foreach (var use in Entries(manifest.UsesContracts, "usesContracts", errors, optional: true))
        {
            uses.TryAdd(Key(use.Role, use.ContractId, use.ContractVersion), use);
        }
        var providers = new Dictionary<string, SunderPackageProviderManifest>(StringComparer.Ordinal);
        foreach (var provider in Entries(manifest.Provides, "provides", errors, optional: true))
        {
            providers.TryAdd(Key(provider.Role, provider.ProviderId, provider.ContractId, provider.ContractVersion), provider);
        }
        var roles = (manifest.Targets ?? []).Take(SunderPackageFormat.MaxTargets)
            .Where(static target => target is not null && SunderPackageTargetKey.TryCreate(target.Role, target.Rid, out _))
            .Select(static target => target!.Role!).ToHashSet(StringComparer.Ordinal);

        var artifacts = new Dictionary<string, SunderPackageArtifactImportManifest>(StringComparer.Ordinal);
        var artifactAuthorities = new Dictionary<string, Dictionary<string, AuthoritySnapshot>>(StringComparer.Ordinal);
        foreach (var artifact in OrderedEntries(manifest.PackageArtifactImports, "packageArtifactImports",
                     static item => Key(item.PackageId), errors, optional: true))
        {
            ValidatePackageId(artifact.PackageId, "Package artifact import", errors);
            ValidateSelection(artifact.RequestedVersionRange, artifact.IncludePrerelease,
                artifact.SelectedPackageVersion, $"Package artifact import '{artifact.PackageId}'", errors);
            ValidateHash(artifact.SelectedPackageContentIdentity, "selectedPackageContentIdentity", errors);
            ValidateHash(artifact.PublicInventorySha256, "publicInventorySha256", errors);
            if (artifact.PublicContractCount is not (>= 1 and <= MaxEntries))
            {
                errors.Add($"Package artifact import '{artifact.PackageId}' publicContractCount must be between 1 and {MaxEntries}.");
            }
            ValidateAuthority(new AuthoritySnapshot("package", artifact.PackageId, artifact.SelectedPackageVersion,
                artifact.SelectedPackageContentIdentity), $"Package artifact import '{artifact.PackageId}'");

            var constraints = new Dictionary<string, AuthoritySnapshot>(StringComparer.Ordinal);
            foreach (var constraint in OrderedEntries(artifact.AuthorityConstraints, "authorityConstraints",
                         static item => Key(item.AuthorityKind, item.AuthorityId), errors))
            {
                var snapshot = new AuthoritySnapshot(constraint.AuthorityKind, constraint.AuthorityId,
                    constraint.SelectedAuthorityRelease, constraint.SelectedAuthorityContentIdentity);
                ValidateAuthority(snapshot, $"Package artifact import '{artifact.PackageId}' authority constraint");
                constraints.TryAdd(Key(snapshot.Kind, snapshot.Id), snapshot);
            }
            if (constraints.Count > artifact.PublicContractCount)
            {
                errors.Add($"Package artifact import '{artifact.PackageId}' has more authorityConstraints than publicContractCount.");
            }
            if (artifact.PackageId is not null && artifacts.TryAdd(artifact.PackageId, artifact))
            {
                artifactAuthorities.Add(artifact.PackageId, constraints);
            }
        }

        var coveredBundles = new HashSet<string>(StringComparer.Ordinal);
        var coveredUses = new HashSet<string>(StringComparer.Ordinal);
        var coveredProviders = new HashSet<string>(StringComparer.Ordinal);
        var artifactContracts = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        var apiContracts = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var dependencyPresence = new Dictionary<string, string>(StringComparer.Ordinal);
        var pendingDependencies = new Queue<string>();
        foreach (var api in OrderedEntries(manifest.PackageApiImports, "packageApiImports",
                     static item => Key(item.PackageId), errors, optional: true))
        {
            ValidatePackageId(api.PackageId, "Package API import", errors);
            if (api.Presence is not ("required" or "optional"))
            {
                errors.Add($"Package API import '{api.PackageId}' must declare presence as 'required' or 'optional'.");
            }
            AddDependency(api.PackageId, api.Presence);
            var refs = new HashSet<string>(StringComparer.Ordinal);
            var contractIds = new HashSet<string>(StringComparer.Ordinal);
            var authorityKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in OrderedEntries(api.BundleRefs, "packageApiImports.bundleRefs",
                         static item => Key(item.ContractId, item.ContractVersion), errors))
            {
                var key = Key(reference.ContractId, reference.ContractVersion);
                refs.Add(key);
                if (reference.ContractId is not null && !contractIds.Add(reference.ContractId))
                {
                    errors.Add($"Package API import '{api.PackageId}' must select only one version of each contract id.");
                }
                if (ValidateBundleReference(reference, "package", api.PackageId) is { } snapshot)
                {
                    authorityKeys.Add(Key(snapshot.Kind, snapshot.Id));
                }
            }
            if (api.PackageId is not null) apiContracts.TryAdd(api.PackageId, refs);

            if (api.PackageId is not null && artifacts.TryGetValue(api.PackageId, out var artifact))
            {
                var constraints = artifactAuthorities[api.PackageId];
                var packageAuthorities = constraints.Values.Where(static value => value.Kind == "package")
                    .Select(static value => Key(value.Kind, value.Id)).ToHashSet(StringComparer.Ordinal);
                if (packageAuthorities.Count == 0 || !packageAuthorities.SetEquals(authorityKeys))
                {
                    errors.Add($"Package API import '{api.PackageId}' bundleRefs must cover exactly its package authority constraints.");
                }
                // The manifest has no complete inventory. Check possible counts and graph closure here;
                // the Registry must independently authenticate the complete selected artifact inventory.
                var platformCount = constraints.Values.Count(static value => value.Kind == "platform");
                if (refs.Count > artifact.PublicContractCount - platformCount
                    || (platformCount == 0 && refs.Count != artifact.PublicContractCount))
                {
                    errors.Add($"Package API import '{api.PackageId}' bundleRefs disagree with publicContractCount.");
                }
            }

            var useRefs = new HashSet<string>(StringComparer.Ordinal);
            var useRoles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in OrderedEntries(api.UseRefs, "packageApiImports.useRefs",
                         static item => Key(item.Role, item.ContractId, item.ContractVersion), errors))
            {
                ValidateContractIdentity(reference.ContractId, reference.ContractVersion, "Contract use reference", errors);
                var key = Key(reference.Role, reference.ContractId, reference.ContractVersion);
                useRefs.Add(key);
                if (!SunderPackageFormat.IsHostRole(reference.Role) || !roles.Contains(reference.Role!))
                {
                    errors.Add($"Package API use reference '{reference.ContractId}' role '{reference.Role}' has no matching package target.");
                }
                else
                {
                    useRoles.Add(reference.Role!);
                }
                if (!refs.Contains(Key(reference.ContractId, reference.ContractVersion)) || !uses.ContainsKey(key))
                {
                    errors.Add($"Package API import '{api.PackageId}' useRefs require exact matching bundleRefs and local uses.");
                }
                coveredUses.Add(key);
            }
            // A group grants exactly its own roles crossed with its complete bundle set.
            // Owner/reexport groups can overlap without granting the same consumer roles.
            // Global reverse coverage below still rejects every ungranted local role use.
            foreach (var role in useRoles)
            {
                foreach (var key in refs)
                {
                    if (!useRefs.Contains(Key(role, key)))
                    {
                        errors.Add($"Package API import '{api.PackageId}' useRefs must cover every bundle for each consumer role.");
                    }
                }
            }
        }

        var importAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var providerTuples = new HashSet<string>(StringComparer.Ordinal);
        foreach (var import in OrderedEntries(manifest.ProviderImports, "providerImports",
                     static item => Key(item.Key), errors, optional: true))
        {
            ValidateLocalName(import.Key, "Provider import", errors);
            if (import.Key is not null && !importAliases.Add(import.Key))
            {
                errors.Add($"Provider and platform import key '{import.Key}' is duplicated under OrdinalIgnoreCase.");
            }
            ValidatePackageId(import.PackageId, "Provider import", errors);
            ValidateContractIdentity(import.ContractId, import.ContractVersion, "Provider import", errors);
            if (!providerTuples.Add(Key(import.PackageId, import.ContractId)))
            {
                errors.Add("Provider imports must have unique (packageId, contractId) tuples.");
            }
            if (import.Presence is not null and not ("required" or "optional"))
                errors.Add("Provider import presence must be required or optional when supplied.");
            AddDependency(import.PackageId, import.Presence ?? "required");
            ValidateBundleReference(import.BundleRef, "package", import.PackageId);
            ValidateProviderReferences(import.BundleRef, import.ContractId, import.ContractVersion, import.ProviderRefs);
        }

        var platformTuples = new HashSet<string>(StringComparer.Ordinal);
        foreach (var import in OrderedEntries(manifest.PlatformImports, "platformImports",
                     static item => Key(item.Key), errors, optional: true))
        {
            ValidateLocalName(import.Key, "Platform import", errors);
            if (import.Key is not null && !importAliases.Add(import.Key))
            {
                errors.Add($"Provider and platform import key '{import.Key}' is duplicated under OrdinalIgnoreCase.");
            }
            ValidateContractIdentity(import.ContractId, import.ContractVersion, "Platform import", errors);
            if (import.AuthorityKind != "platform" || import.AuthorityId != "sunder.core" || import.IncludePrerelease != false)
            {
                errors.Add("Platform imports require platform authority 'sunder.core' and includePrerelease=false.");
            }
            if (!platformTuples.Add(Key(import.AuthorityId, import.ContractId)))
            {
                errors.Add("Platform imports must have unique (authorityId, contractId) tuples.");
            }
            ValidateSelection(import.AuthorityReleaseRange, import.IncludePrerelease, import.SelectedAuthorityRelease,
                $"Platform import '{import.Key}'", errors);
            var snapshot = new AuthoritySnapshot(import.AuthorityKind, import.AuthorityId,
                import.SelectedAuthorityRelease, import.SelectedAuthorityContentIdentity);
            ValidateAuthority(snapshot, $"Platform import '{import.Key}'");
            if (ValidateBundleReference(import.BundleRef, "platform", null) is { } bundleSnapshot && bundleSnapshot != snapshot)
            {
                errors.Add($"Platform import '{import.Key}' does not match its bundle authority snapshot.");
            }
            ValidateProviderReferences(import.BundleRef, import.ContractId, import.ContractVersion, import.ProviderRefs);
        }

        // Provider artifacts also carry the public metadata closure needed to
        // decode service references. A local usesContracts entry is explicit
        // per-contract Runtime intent; providerImports alone never invents one.
        // Complete inventory/projection membership is authenticated by the
        // compiler/Registry, just as for the existing provider root bundle.
        var providerArtifacts = (manifest.ProviderImports ?? []).Take(MaxEntries).OfType<SunderPackageProviderImportManifest>()
            .Select(import => import.PackageId).OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var referenceBundles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, bundle) in bundles.Where(pair => pair.Value.Source == "imported" && pair.Value.AuthorityKind == "package" && pair.Value.Exposure == "public"))
        {
            if (!bundleSnapshots.TryGetValue(key, out var snapshot)) continue;
            foreach (var id in providerArtifacts.Where(id => artifactAuthorities.TryGetValue(id, out var constraints)
                         && constraints.TryGetValue(Key(snapshot.Kind, snapshot.Id), out var selected) && selected == snapshot))
            {
                // Existing root/group references retain their own inventory
                // membership. Do not charge another reexport's different public
                // inventory for every contract with the same ultimate authority.
                if (!coveredBundles.Contains(key))
                    ValidateBundleReference(new() { ContractId = bundle.ContractId, ContractVersion = bundle.Version }, "package",
                        snapshot.Id is not null && artifacts.ContainsKey(snapshot.Id) ? snapshot.Id : id);
                referenceBundles.Add(key);
            }
        }
        foreach (var (key, bundle) in bundles)
        {
            if (bundle.Source == "imported" && !coveredBundles.Contains(key))
            {
                errors.Add($"Imported contract bundle '{bundle.ContractId}' is orphaned: no import group references it.");
            }
        }
        foreach (var (key, use) in uses)
        {
            if (!bundles.TryGetValue(Key(use.ContractId, use.ContractVersion), out var bundle)) continue;
            if (bundle.AuthorityKind == "platform")
            {
                errors.Add($"Platform contract '{use.ContractId}' cannot have consumer uses.");
            }
            else if (bundle.Source == "imported" && !coveredUses.Contains(key)
                && !(use.Role == "runtime" && referenceBundles.Contains(Key(use.ContractId, use.ContractVersion))))
            {
                errors.Add($"Imported contract use '{use.ContractId}' is orphaned: provider imports do not grant consumer uses.");
            }
        }
        foreach (var (key, provider) in providers)
        {
            if (bundles.TryGetValue(Key(provider.ContractId, provider.ContractVersion), out var bundle)
                && bundle.Source == "imported" && !coveredProviders.Contains(key))
            {
                errors.Add($"Imported contract provider '{provider.ProviderId}' is orphaned: no provider import references it.");
            }
        }

        // Each selected artifact is queued at most twice: optional, then a possible required upgrade.
        while (pendingDependencies.TryDequeue(out var packageId))
        {
            foreach (var constraint in artifactAuthorities[packageId].Values)
            {
                if (constraint.Kind != "package" || constraint.Id == packageId) continue;
                if (constraint.Id is null || !artifacts.ContainsKey(constraint.Id))
                {
                    errors.Add($"Package authority '{constraint.Id}' has an unreviewed authority range: an explicit selected artifact with a reviewed requestedVersionRange is required.");
                    continue;
                }
                if (!artifactAuthorities[constraint.Id].TryGetValue(Key("package", constraint.Id), out var owner)
                    || owner != constraint)
                {
                    errors.Add($"Explicitly selected package authority '{constraint.Id}' must retain its own exact authority constraint.");
                }
                AddDependency(constraint.Id, dependencyPresence[packageId]);
            }
        }
        var dependencies = new Dictionary<string, SunderPackageDependencyManifest>(StringComparer.Ordinal);
        foreach (var dependency in (manifest.DependsOn ?? []).Take(SunderPackageFormat.MaxDependencies))
        {
            if (dependency?.PackageId is not null) dependencies.TryAdd(dependency.PackageId, dependency);
        }
        foreach (var (packageId, artifact) in artifacts)
        {
            if (artifactContracts.TryGetValue(packageId, out var referencedContracts))
            {
                var constraints = artifactAuthorities[packageId];
                var unrepresentedAuthorities = constraints.Keys.Count(key => !referencedContracts.Keys.Any(contractId =>
                    bundleSnapshots.TryGetValue(Key(contractId, referencedContracts[contractId]), out var snapshot)
                    && Key(snapshot.Kind, snapshot.Id) == key));
                if (referencedContracts.Count + unrepresentedAuthorities > artifact.PublicContractCount)
                {
                    errors.Add($"Package artifact import '{packageId}' references more contracts and authorities than publicContractCount permits.");
                }
                if (apiContracts.TryGetValue(packageId, out var apiRefs)
                    && referencedContracts.Any(pair => !apiRefs.Contains(Key(pair.Key, pair.Value))))
                {
                    errors.Add($"Package API import '{packageId}' bundleRefs omit an exact contract required by its import graph.");
                }
            }
            if (!dependencyPresence.TryGetValue(packageId, out var presence))
            {
                errors.Add($"Package artifact import '{packageId}' is orphaned: no import intent or reviewed authority dependency references it.");
                continue;
            }
            if (!dependencies.TryGetValue(packageId, out var dependency)
                || dependency.VersionRange != artifact.RequestedVersionRange || dependency.Presence != presence)
            {
                errors.Add($"RPC dependency '{packageId}' must use the selected artifact's reviewed requestedVersionRange and coalesced presence '{presence}', not a selected release/content install pin.");
            }
        }

        void ValidateAuthority(AuthoritySnapshot snapshot, string label)
        {
            if (snapshot.Kind is not ("package" or "platform"))
            {
                errors.Add($"{label} must declare authorityKind as 'package' or 'platform'.");
            }
            ValidatePackageId(snapshot.Id, label + " authority", errors);
            if (!SemanticVersion.TryParse(snapshot.Release, out _))
            {
                errors.Add($"{label} must declare a strict SemVer selectedAuthorityRelease.");
            }
            ValidateHash(snapshot.ContentIdentity, label + " selectedAuthorityContentIdentity", errors);
            if (snapshot.Kind == "platform")
            {
                if (snapshot.Id != "sunder.core")
                {
                    errors.Add($"{label} requires platform authority 'sunder.core'.");
                }
            }
            else
            {
                if (snapshot.Kind == "package" && snapshot.Id == manifest.Id)
                {
                    errors.Add($"{label} cannot import the package being built as an authority or dependency.");
                }
            }
            var key = Key(snapshot.Kind, snapshot.Id);
            if (!snapshots.TryAdd(key, snapshot) && snapshots[key] != snapshot)
            {
                errors.Add($"{label} has conflicting selected authority snapshots for '{snapshot.Kind}:{snapshot.Id}'.");
            }
        }

        AuthoritySnapshot? ValidateBundleReference(SunderPackageContractReferenceManifest? reference, string kind, string? packageId)
        {
            if (reference is null)
            {
                errors.Add("Import bundleRef is mandatory and cannot be null.");
                return null;
            }
            ValidateContractIdentity(reference.ContractId, reference.ContractVersion, "Import bundle reference", errors);
            var key = Key(reference.ContractId, reference.ContractVersion);
            if (!bundles.TryGetValue(key, out var bundle) || bundle.Source != "imported"
                || bundle.Exposure != "public" || bundle.AuthorityKind != kind
                || !bundleSnapshots.TryGetValue(key, out var snapshot))
            {
                errors.Add($"Import bundle reference '{reference.ContractId}' requires an exact public imported {kind}-authority bundle.");
                return null;
            }
            coveredBundles.Add(key);
            if (kind == "package" && packageId is not null && reference.ContractId is not null)
            {
                RequireArtifactContract(packageId, reference.ContractId, reference.ContractVersion);
                if (snapshot.Id is not null && snapshot.Id != packageId
                    && artifacts.TryGetValue(snapshot.Id, out var owner)
                    && owner.SelectedPackageVersion == snapshot.Release
                    && owner.SelectedPackageContentIdentity == snapshot.ContentIdentity)
                {
                    // Carry the exact requirement, not a reconstructed inventory or an install pin.
                    RequireArtifactContract(snapshot.Id, reference.ContractId, reference.ContractVersion);
                }
            }
            if (kind == "package"
                && (packageId is null || !artifactAuthorities.TryGetValue(packageId, out var constraints)
                    || !constraints.TryGetValue(Key(snapshot.Kind, snapshot.Id), out var constraint) || constraint != snapshot))
            {
                errors.Add($"Import bundle reference '{reference.ContractId}' is not backed by the selected artifact's exact authorityConstraints.");
            }
            return snapshot;
        }

        void RequireArtifactContract(string packageId, string contractId, string? version)
        {
            if (!artifactContracts.TryGetValue(packageId, out var selectedContracts))
            {
                selectedContracts = new Dictionary<string, string?>(StringComparer.Ordinal);
                artifactContracts.Add(packageId, selectedContracts);
            }
            if (!selectedContracts.TryAdd(contractId, version) && selectedContracts[contractId] != version)
            {
                errors.Add($"Package artifact import '{packageId}' must select only one version of each contract id across all import groups.");
            }
        }

        void ValidateProviderReferences(SunderPackageContractReferenceManifest? bundleRef, string? contractId, string? version,
            IReadOnlyList<SunderPackageProviderReferenceManifest?>? references)
        {
            if (bundleRef is null || bundleRef.ContractId != contractId || bundleRef.ContractVersion != version)
            {
                errors.Add("Provider import contract identity must exactly match its bundleRef.");
            }
            foreach (var reference in OrderedEntries(references, "providerRefs",
                         static item => Key(item.Role, item.ProviderId, item.ContractId, item.ContractVersion), errors))
            {
                ValidatePackageId(reference.ProviderId, "Provider reference", errors);
                ValidateContractIdentity(reference.ContractId, reference.ContractVersion, "Provider reference", errors);
                if (reference.Role != "runtime" || !roles.Contains(reference.Role))
                {
                    errors.Add("Import providerRefs require runtime role and a matching package target.");
                }
                var key = Key(reference.Role, reference.ProviderId, reference.ContractId, reference.ContractVersion);
                if (reference.ContractId != contractId || reference.ContractVersion != version || !providers.ContainsKey(key))
                {
                    errors.Add("Import providerRefs require exact matching import contract identity and local providers.");
                }
                // Same-contract owner and reexport groups share references; coverage is not exclusive.
                coveredProviders.Add(key);
            }
        }

        void AddDependency(string? packageId, string? presence)
        {
            if (packageId is null || !artifacts.ContainsKey(packageId))
            {
                errors.Add($"RPC import '{packageId}' requires an explicit selected packageArtifactImports entry.");
                return;
            }
            if (presence is not ("required" or "optional")) return;
            if (!dependencyPresence.TryGetValue(packageId, out var previous) || (previous == "optional" && presence == "required"))
            {
                dependencyPresence[packageId] = presence;
                pendingDependencies.Enqueue(packageId);
            }
        }
    }

    private static IEnumerable<T> Entries<T>(IReadOnlyList<T?>? entries, string label, ICollection<string> errors, bool optional = false)
        where T : class
    {
        if (!optional && (entries is null || entries.Count == 0))
        {
            errors.Add($"{label} must declare a nonempty array.");
        }
        if (entries is { Count: > MaxEntries }) errors.Add($"{label} exceeds the limit of {MaxEntries} entries.");
        foreach (var entry in (entries ?? []).Take(MaxEntries))
        {
            if (entry is null) errors.Add($"{label} entry is null.");
            else yield return entry;
        }
    }

    private static IEnumerable<T> OrderedEntries<T>(IReadOnlyList<T?>? entries, string label, Func<T, string> key,
        ICollection<string> errors, bool optional = false) where T : class
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? previous = null;
        foreach (var entry in Entries(entries, label, errors, optional))
        {
            var current = key(entry);
            if (!seen.Add(current)) errors.Add($"{label} contains a duplicate identity/reference tuple.");
            if (previous is not null && string.CompareOrdinal(previous, current) > 0)
            {
                errors.Add($"{label} must be sorted in ordinal tuple order.");
            }
            previous = current;
            yield return entry;
        }
    }

    private static void ValidateSelection(string? rangeText, bool? includePrerelease, string? selected, string label, ICollection<string> errors)
    {
        var validRange = PackageVersionRange.TryParse(rangeText, out var range)
                         && string.Equals(range.ToString(), rangeText, StringComparison.Ordinal);
        if (!validRange) errors.Add($"{label} must declare a supported canonical version range.");
        if (includePrerelease is null) errors.Add($"{label} must declare includePrerelease.");
        if (!SemanticVersion.TryParse(selected, out var version))
        {
            errors.Add($"{label} must declare a strict SemVer selected release.");
        }
        else if ((validRange && !range.IsSatisfiedBy(version)) || (version.Prerelease is not null && includePrerelease != true))
        {
            errors.Add($"{label} selected release must satisfy its reviewed range and prerelease policy.");
        }
    }

    private static void ValidateContractIdentity(string? id, string? version, string label, ICollection<string> errors)
    {
        ValidatePackageId(id, label, errors);
        if (!SemanticVersion.TryParse(version, out _)) errors.Add($"{label} must declare a strict SemVer contractVersion.");
    }

    private static void ValidatePackageId(string? id, string label, ICollection<string> errors)
    {
        if (!PackageId.TryParse(id, out _)) errors.Add($"{label} id '{id}' must use lowercase dot-separated ASCII identifiers.");
    }

    private static void ValidateHash(string? hash, string label, ICollection<string> errors)
    {
        if (!PackageContentSignatureValidator.IsLowercaseSha256(hash)) errors.Add($"{label} must declare a lowercase SHA-256 identity.");
    }

    private static void ValidateLocalName(string? name, string label, ICollection<string> errors)
    {
        if (name is not { Length: >= 1 and <= 128 } || name[0] is not (>= 'a' and <= 'z')
            || !name.All(char.IsAsciiLetterOrDigit))
        {
            errors.Add($"{label} key must be an ASCII local name beginning with a lowercase letter, at most 128 characters.");
        }
    }

    private static string Key(params string?[] parts) => string.Join('\0', parts);

    private sealed record AuthoritySnapshot(string? Kind, string? Id, string? Release, string? ContentIdentity);
}
