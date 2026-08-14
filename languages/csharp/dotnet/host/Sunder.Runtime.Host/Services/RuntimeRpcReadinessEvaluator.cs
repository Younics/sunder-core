using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeRpcReadinessEvaluator(RuntimeRpcPermissionStore permissions)
{
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
            .Where(provider => IsCompatible(caller.PackageId, use, provider))
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
            return true;
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
               && registration.Contract.DescriptorVersion == SunderRpcProtocol.DescriptorVersionV2
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
