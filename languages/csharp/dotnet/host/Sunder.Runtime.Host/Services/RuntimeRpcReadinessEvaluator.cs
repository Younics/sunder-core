using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeRpcReadinessEvaluator(RuntimeRpcPermissionStore permissions)
{
    internal static IReadOnlyList<string> AppOwnerOnlyContracts(RuntimePackageSource source)
        => (source.Manifest?.ContractBundles ?? []).Where(bundle => bundle?.Exposure == SunderPackageFormat.PackageContractExposure)
            .Select(bundle => bundle!.ContractId + "\0" + bundle.Version).Order(StringComparer.Ordinal).ToArray();

    internal static IReadOnlyList<RuntimeRequiredProviderPredicate> AppPredicates(RuntimePackageSource source)
        => (source.Manifest?.UsesContracts ?? []).OfType<SunderPackageContractUseManifest>().Where(use => use.Role == SunderPackageFormat.AppHostRole && use.Required == true
            && InstalledPackageProjection.IncludesRole(source, use.Role))
            .OrderBy(use => use.ContractId, StringComparer.Ordinal).ThenBy(use => use.ContractVersion, StringComparer.Ordinal)
            .Select(use => new RuntimeRequiredProviderPredicate(source.PackageId, use.Role!, new(use.ContractId!, use.ContractVersion!,
                source.Manifest!.ContractBundles!.Single(bundle => bundle!.ContractId == use.ContractId && bundle.Version == use.ContractVersion)!.DescriptorSha256!, use.AbiSha256!),
                (use.Actions ?? []).Select(value => value!).Order(StringComparer.Ordinal).ToArray())).ToArray();

    internal bool AppPredicatesSatisfied(ActivePackageSession session, string version, bool development, string? manifestHash,
        IReadOnlyList<RuntimeRequiredProviderPredicate> predicates, IReadOnlyList<string>? ownerOnlyContracts = null)
    {
        var providers = session.LoadedPackageMap.Values.Where(package => session.IsPackageEnabled(package.Descriptor.PackageId))
            .SelectMany(package => package.RpcProviders.Values.Select(registration => new ActiveProvider(package.Descriptor.PackageId, registration))).ToArray();
        return predicates.All(predicate => providers.Any(provider =>
        {
            var registration = provider.Registration;
            if (ownerOnlyContracts?.Contains(predicate.Contract.ContractId + "\0" + predicate.Contract.ContractVersion, StringComparer.Ordinal) == true
                && provider.PackageId != predicate.ConsumerPackageId) return false;
            if (registration.Manifest?.Role != SunderPackageFormat.RuntimeHostRole || registration.ContractId != predicate.Contract.ContractId
                || registration.ContractVersion != predicate.Contract.ContractVersion || registration.DescriptorSha256 != predicate.Contract.DescriptorSha256
                || registration.Contract.AbiSha256 != predicate.Contract.AbiSha256) return false;
            if (IsOwnerProvider(predicate.ConsumerPackageId, provider)) return true;
            return registration.Manifest.Audience == SunderPackageFormat.PublicProviderAudience && (development
                || manifestHash is not null && predicate.Actions.All(action => permissions.IsGranted(new(predicate.ConsumerPackageId, version, manifestHash,
                    SunderPackageFormat.AppHostRole, predicate.Contract.ContractId, predicate.Contract.ContractVersion, predicate.Contract.AbiSha256, action))));
        }));
    }

    internal IReadOnlyList<string> UnsatisfiedConsumers(ActivePackageSession session, string role)
    {
        if (session.RequiredProviderPlan is null) return [];
        var providers = session.LoadedPackageMap.Values.Where(package => session.IsPackageEnabled(package.Descriptor.PackageId))
            .SelectMany(package => package.RpcProviders.Values.Select(registration => new ActiveProvider(package.Descriptor.PackageId, registration))).ToArray();
        return session.GetSessionPackages().Where(package => package.IsEnabled && package.Readiness is PackageReadinessState.Ready or PackageReadinessState.Degraded
            && session.TryGetPackageSource(package.PackageId, out var source) && source is not null
            && package.RpcContractUses.Where(use => use.Required && use.Role == role).Any(use => !IsAvailable(package, source,
                session.TryGetLoadedPackage(package.PackageId, out var loaded) ? loaded : null, use, providers)))
            .Select(package => package.PackageId).Order(StringComparer.Ordinal).ToArray();
    }

    internal RuntimeRequiredProviderPlan Plan(ActivePackageSession session)
    {
        var providers = session.LoadedPackageMap.Values.Where(package => session.IsPackageEnabled(package.Descriptor.PackageId))
            .SelectMany(package => package.RpcProviders.Values.Select(registration => new ActiveProvider(package.Descriptor.PackageId, registration))).ToArray();
        var sources = session.GetActivePackageSources().Where(source => session.IsPackageEnabled(source.PackageId)).ToDictionary(source => source.PackageId, StringComparer.Ordinal);
        var requirements = new List<RequiredProviderPredicate>();
        foreach (var package in session.GetSessionPackages().Where(package => package.IsEnabled).OrderBy(package => package.PackageId, StringComparer.Ordinal))
        {
            if (!sources.TryGetValue(package.PackageId, out var source)) continue;
            session.TryGetLoadedPackage(package.PackageId, out var caller);
            foreach (var use in package.RpcContractUses.Where(use => use.Required && IsRoleActive(package.HostRoles, use.Role))
                         .OrderBy(use => use.Role, StringComparer.Ordinal).ThenBy(use => use.ContractId, StringComparer.Ordinal).ThenBy(use => use.ContractVersion, StringComparer.Ordinal))
            {
                var bundle = source.Manifest?.ContractBundles?.SingleOrDefault(value => value?.ContractId == use.ContractId && value.Version == use.ContractVersion);
                var hash = bundle?.DescriptorSha256 ?? caller?.RpcContracts.Values.SingleOrDefault(value => value.ContractId == use.ContractId && value.Version == use.ContractVersion)?.Sha256;
                var alternatives = providers.Where(provider => hash is not null && provider.Registration.DescriptorSha256 == hash
                        && IsAvailable(package, source, caller, use, [provider]))
                    .Select(provider => provider.PackageId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                requirements.Add(new(new(package.PackageId, use.Role, new(use.ContractId, use.ContractVersion, hash ?? "", use.AbiSha256), use.Actions.Order(StringComparer.Ordinal).ToArray()), alternatives));
            }
        }
        var dependencies = sources.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.PackageDependencies.Select(value => value.PackageId).Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var plan = new RuntimeRequiredProviderPlan(dependencies, requirements);
        plan.Validate();
        return plan;
    }

    public IReadOnlyList<string> Evaluate(ActivePackageSession session)
    {
        var providers = session.LoadedPackageMap.Values
            .Where(package => session.IsPackageEnabled(package.Descriptor.PackageId))
            .SelectMany(package => package.RpcProviders.Values.Select(registration =>
                new ActiveProvider(package.Descriptor.PackageId, registration)))
            .ToArray();
        var changedPackageIds = new List<string>();
        foreach (var package in session.GetSessionPackages())
        {
            if (!package.IsEnabled
                || package.Readiness is not (PackageReadinessState.Ready or PackageReadinessState.Degraded)
                || !session.TryGetPackageSource(package.PackageId, out var source)
                || source is null)
            {
                continue;
            }

            var requiredUses = package.RpcContractUses
                .Where(use => use.Required && IsRoleActive(package.HostRoles, use.Role))
                .ToArray();
            session.TryGetLoadedPackage(package.PackageId, out var callerActivation);
            var ready = requiredUses.All(use => IsAvailable(
                package,
                source,
                callerActivation,
                use,
                providers));
            var readiness = ready ? PackageReadinessState.Ready : PackageReadinessState.Degraded;
            if (session.SetRpcReadiness(package.PackageId, readiness))
            {
                changedPackageIds.Add(package.PackageId);
            }
        }
        return changedPackageIds;
    }

    private bool IsAvailable(
        SessionPackageDescriptor caller,
        RuntimePackageSource source,
        ActiveLoadedPackage? callerActivation,
        PackageRpcContractUseDescriptor use,
        IReadOnlyList<ActiveProvider> providers)
    {
        var compatibleProviders = providers
            .Where(provider => IsCompatible(caller.PackageId, use, provider)
                && (provider.PackageId == caller.PackageId || source.Manifest?.ContractBundles?.Any(bundle => bundle?.ContractId == use.ContractId
                    && bundle.Version == use.ContractVersion && bundle.Exposure == SunderPackageFormat.PublicContractExposure) == true)
                && (source.Manifest?.ContractBundles?.SingleOrDefault(value => value?.ContractId == use.ContractId && value.Version == use.ContractVersion)?.DescriptorSha256
                    ?? callerActivation?.RpcContracts.Values.SingleOrDefault(value => value.ContractId == use.ContractId && value.Version == use.ContractVersion)?.Sha256)
                    == provider.Registration.DescriptorSha256)
            .ToArray();
        if (compatibleProviders.Length == 0)
        {
            return false;
        }
        if (compatibleProviders.Any(provider => IsOwnerProvider(caller.PackageId, provider)))
        {
            return true;
        }
        if (!compatibleProviders.Any(static provider =>
                provider.Registration.Manifest?.Audience == SunderPackageFormat.PublicProviderAudience))
        {
            return false;
        }
        if (source.Kind == PackageSourceKind.Dev)
        {
            return PackageCompatibilityAuthority.DevelopmentUseEnabled(source, use.Role, use.ContractId, use.ContractVersion);
        }

        var manifestSha256 = GetManifestSha256(source, callerActivation, use.Role);
        if (manifestSha256 is null)
        {
            return false;
        }
        return use.Actions.All(action => permissions.IsGranted(new RuntimeRpcPermissionKey(
            caller.PackageId,
            caller.Version,
            manifestSha256,
            use.Role,
            use.ContractId,
            use.ContractVersion,
            use.AbiSha256,
            action)));
    }

    private static bool IsCompatible(
        string callerPackageId,
        PackageRpcContractUseDescriptor use,
        ActiveProvider provider)
    {
        var registration = provider.Registration;
        var declaration = registration.Manifest;
        return declaration is not null
               && declaration.Role == SunderPackageFormat.RuntimeHostRole
               && (declaration.Audience == SunderPackageFormat.PublicProviderAudience
                   || declaration.Audience == SunderPackageFormat.OwnerProviderAudience
                   && string.Equals(callerPackageId, provider.PackageId, StringComparison.Ordinal))
               && string.Equals(declaration.ContractId, use.ContractId, StringComparison.Ordinal)
               && string.Equals(declaration.ContractVersion, use.ContractVersion, StringComparison.Ordinal)
               && string.Equals(declaration.AbiSha256, use.AbiSha256, StringComparison.Ordinal)
               && string.Equals(registration.ContractId, use.ContractId, StringComparison.Ordinal)
               && string.Equals(registration.ContractVersion, use.ContractVersion, StringComparison.Ordinal)
               && string.Equals(registration.Contract.ContractId, use.ContractId, StringComparison.Ordinal)
               && string.Equals(registration.Contract.Version, use.ContractVersion, StringComparison.Ordinal)
               && registration.Contract.DescriptorVersion == SunderRpcProtocol.DescriptorVersion
               && string.Equals(registration.Contract.AbiSha256, use.AbiSha256, StringComparison.Ordinal)
               && string.Equals(registration.DescriptorSha256, declaration.DescriptorSha256, StringComparison.Ordinal)
               && string.Equals(registration.Contract.Sha256, declaration.DescriptorSha256, StringComparison.Ordinal);
    }

    private static bool IsOwnerProvider(string callerPackageId, ActiveProvider provider)
        => provider.Registration.Manifest?.Audience == SunderPackageFormat.OwnerProviderAudience
           && string.Equals(callerPackageId, provider.PackageId, StringComparison.Ordinal);

    private static bool IsRoleActive(PackageHostRoles roles, string role)
        => role switch
        {
            SunderPackageFormat.AppHostRole => roles.HasFlag(PackageHostRoles.App),
            SunderPackageFormat.RuntimeHostRole => roles.HasFlag(PackageHostRoles.Runtime),
            _ => false,
        };

    private static string? GetManifestSha256(
        RuntimePackageSource source,
        ActiveLoadedPackage? callerActivation,
        string role)
    {
        if (role == SunderPackageFormat.RuntimeHostRole
            && !string.IsNullOrWhiteSpace(callerActivation?.RpcManifestSha256))
        {
            return callerActivation.RpcManifestSha256;
        }
        if (source.ContentIndex is not null)
        {
            return RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex);
        }
        return string.IsNullOrWhiteSpace(callerActivation?.RpcManifestSha256)
            ? null
            : callerActivation.RpcManifestSha256;
    }

    private sealed record ActiveProvider(
        string PackageId,
        RuntimeRpcProviderRegistration Registration);
}
