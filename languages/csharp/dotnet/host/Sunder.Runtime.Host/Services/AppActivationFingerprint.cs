using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal static class AppActivationFingerprint
{
    internal static string Create(Guid runtimeId, string packageId, IReadOnlyList<RuntimePackageSource> sources,
        IReadOnlyList<SessionPackageDescriptor> packages, bool developmentWorkerContinuation = false,
        bool developmentAppContinuation = false)
    {
        if (developmentWorkerContinuation && developmentAppContinuation)
            throw new ArgumentException("A continuation comparison must select exactly one role.");
        var byId = sources.ToDictionary(source => source.PackageId, StringComparer.Ordinal);
        var states = packages.ToDictionary(package => package.PackageId, StringComparer.Ordinal);
        var closure = DependencyClosure(packageId, sources);
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
                Enabled = states.GetValueOrDefault(id)?.IsEnabled,
                Readiness = states.GetValueOrDefault(id)?.Readiness,
            };
        }).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { RuntimeId = runtimeId, PackageId = packageId, Closure = closure, Evidence = evidence })));

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

    internal static IReadOnlySet<string> DependencyClosure(string packageId, IReadOnlyList<RuntimePackageSource> sources)
    {
        var byId = sources.ToDictionary(source => source.PackageId, StringComparer.Ordinal);
        var closure = new SortedSet<string>(StringComparer.Ordinal);
        Visit(packageId);
        return closure;
        void Visit(string id)
        {
            if (!closure.Add(id) || !byId.TryGetValue(id, out var source)) return;
            foreach (var dependency in source.Manifest?.DependsOn ?? [])
                if (dependency?.PackageId is { } dependencyId) Visit(dependencyId);
            foreach (var import in source.Manifest?.PackageArtifactImports ?? [])
                if (import?.PackageId is { } importedId) Visit(importedId);
            // Optional/required provider presence and readiness can change without a
            // manifest edit. Retain only while the effective imported provider set agrees.
            var contracts = (source.Manifest?.UsesContracts ?? []).Where(use => use is not null)
                .Select(use => (use!.ContractId, use.ContractVersion)).ToHashSet();
            foreach (var provider in sources.Where(candidate => (candidate.Manifest?.Provides ?? [])
                         .Any(value => value is not null && contracts.Contains((value.ContractId, value.ContractVersion)))))
                Visit(provider.PackageId);
        }
    }
}
