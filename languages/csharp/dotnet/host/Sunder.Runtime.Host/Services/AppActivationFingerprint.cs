using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal static class AppActivationFingerprint
{
    internal static string Create(Guid runtimeId, string packageId, IReadOnlyList<RuntimePackageSource> sources,
        IReadOnlyList<SessionPackageDescriptor> packages, bool developmentWorkerContinuation = false,
        bool developmentAppContinuation = false, string role = SunderPackageFormat.AppHostRole)
    {
        if (developmentWorkerContinuation && developmentAppContinuation)
            throw new ArgumentException("A continuation comparison must select exactly one role.");
        var byId = sources.ToDictionary(source => source.PackageId, StringComparer.Ordinal);
        var states = packages.ToDictionary(package => package.PackageId, StringComparer.Ordinal);
        var closure = DependencyClosure(packageId, sources, role);
        var required = CreateClosure(packageId, sources, role, livenessOnly: true);
        var evidence = closure.Select(id =>
        {
            if (!byId.TryGetValue(id, out var source)) return null;
            var continuation = WorkerContentIdentity(source)
                ?? (developmentAppContinuation && id == packageId ? DevelopmentAppContentIdentity(source) : null);
            return new
            {
                PackageId = id, source.Kind, source.SourceFolder, source.HostRoles, source.Dependencies, source.SelectedTargetKey, source.SelectedTarget,
                ContentIdentity = continuation ?? source.ContentIdentity, source.RegistryPolicyOrigin,
                source.RegistryPolicyContentIdentity, source.InstalledPolicy, source.Compatibility, source.DevelopmentImports,
                source.DevelopmentOmittedPackages, source.AcquiredTargets, source.Manifest,
                ContentIndex = continuation is null ? source.ContentIndex : null,
                // Imported contract/source bytes are compatibility dependencies,
                // not a requirement that the contract owner's other role is live.
                // Required-provider liveness is independently fenced by Host policy.
                Enabled = required.Contains(id) ? states.GetValueOrDefault(id)?.IsEnabled : null,
                Readiness = required.Contains(id) ? states.GetValueOrDefault(id)?.Readiness : null,
            };
        }).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { RuntimeId = runtimeId, PackageId = packageId, Role = role, Closure = closure, Evidence = evidence })));

        // App-owned RPC implementations may be effective Worker dependencies.
        // Their payload changes are not qualified as UI-only continuation.
        string? WorkerContentIdentity(RuntimePackageSource source) => developmentWorkerContinuation
            && source.Kind == PackageSourceKind.Dev && RuntimeRpcPackageMetadata.IsSha256(source.DevelopmentWorkerContentIdentity)
            && !(source.Manifest?.Provides ?? []).Any(provider => provider?.Role == SunderPackageFormat.AppHostRole)
                ? source.DevelopmentWorkerContentIdentity : null;
    }

    private static string? DevelopmentAppContentIdentity(RuntimePackageSource source)
    {
        // The preparer issues the Worker comparison evidence only after strict
        // canonical-shadow validation. Require that exact validated index here,
        // rather than accepting an arbitrary/unqualified source's role hints.
        if (source.Kind != PackageSourceKind.Dev || source.ContentIndex is not { Files: not null } index
            || !RuntimeRpcPackageMetadata.IsSha256(source.ContentIdentity)
            || !RuntimeRpcPackageMetadata.IsSha256(source.DevelopmentWorkerContentIdentity)
            || source.DevelopmentWorkerContentIdentity != DevelopmentWorkerContentIdentity.Create(index)) return null;

        // Only this package's Worker payload may change. Keep the entire manifest,
        // shared/contracts/App bytes and the full effective dependency closure.
        // This digest is comparison evidence, never policy/permit/RPC authority.
        var files = index.Files
            .Where(entry => !SunderPackageFormat.TryClassifyPayloadPath(entry.Path!, out _, out var role, out _, out _)
                || role != SunderPackageFormat.RuntimeHostRole)
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Domain = "sunder-development-app-continuation-v1",
            index.SchemaVersion,
            Files = files,
        })));
    }

    internal static IReadOnlySet<string> DependencyClosure(string packageId, IReadOnlyList<RuntimePackageSource> sources,
        string role = SunderPackageFormat.AppHostRole)
        => CreateClosure(packageId, sources, role, livenessOnly: false);

    private static IReadOnlySet<string> CreateClosure(string packageId, IReadOnlyList<RuntimePackageSource> sources,
        string role, bool livenessOnly)
    {
        var byId = sources.ToDictionary(source => source.PackageId, StringComparer.Ordinal);
        var closure = new SortedSet<string>(StringComparer.Ordinal);
        Visit(packageId);
        return closure;
        void Visit(string id)
        {
            if (!closure.Add(id) || !byId.TryGetValue(id, out var source)) return;
            foreach (var dependency in source.Manifest?.DependsOn ?? [])
                if (dependency?.PackageId is { } dependencyId && (!livenessOnly || dependency.Presence != "optional")) Visit(dependencyId);
            if (!livenessOnly)
                foreach (var import in source.Manifest?.PackageArtifactImports ?? [])
                    if (import?.PackageId is { } importedId) Visit(importedId);
            // Optional catalogs are dynamic. Their presence or another role's
            // imports must not reconstruct this role's unchanged activation.
            var contracts = (source.Manifest?.UsesContracts ?? []).Where(use => use is not null
                    && use.Role == role && use.Required != false
                    && PackageCompatibilityAuthority.DevelopmentUseEnabled(source, role, use.ContractId!, use.ContractVersion!))
                .Select(use => (use!.ContractId, use.ContractVersion, use.AbiSha256)).ToHashSet();
            foreach (var provider in sources.Where(candidate => (candidate.Manifest?.Provides ?? [])
                         .Any(value => value is not null && contracts.Contains((value.ContractId, value.ContractVersion, value.AbiSha256))
                             && (value.Audience != "owner" || candidate.PackageId == source.PackageId))))
                Visit(provider.PackageId);
        }
    }
}
