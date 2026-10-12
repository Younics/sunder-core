using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Package.Format;
using System.Text.Json;

namespace Sunder.Package.Build.Tasks;

internal static class PackageTargetBuilder
{
    public static IReadOnlyList<SunderPackageTargetManifest>? Build(
        IReadOnlyList<string> inferredRoles,
        IReadOnlyList<ITaskItem> configuredRuntimeIdentifiers,
        IReadOnlyList<ITaskItem> explicitTargets,
        IReadOnlyList<ITaskItem> configuredViews,
        string packageId,
        string entryAssembly,
        string targetFramework,
        string sdkVersion,
        IReadOnlyList<string> capabilities,
        string? runtimeIdentifier,
        string? outputType,
        bool selfContained,
        bool useAppHost,
        bool publishSingleFile,
        bool publishTrimmed,
        TaskLoggingHelper log)
    {
        var roles = inferredRoles
            .Where(SunderPackageFormat.IsHostRole)
            .ToHashSet(StringComparer.Ordinal);
        var entryPoint = "lib/" + PackageAssetDiscovery.NormalizePath(entryAssembly);
        var targets = explicitTargets.Count > 0
            ? BuildExplicit(
                explicitTargets,
                configuredViews,
                packageId,
                roles,
                entryPoint,
                targetFramework,
                sdkVersion,
                capabilities,
                runtimeIdentifier,
                outputType,
                selfContained,
                useAppHost,
                publishSingleFile,
                publishTrimmed,
                log)
            : BuildInferred(configuredRuntimeIdentifiers, configuredViews, packageId, roles, entryPoint, targetFramework, sdkVersion, capabilities, log);
        if (targets is null)
        {
            return null;
        }

        var seen = new HashSet<SunderPackageTargetKey>();
        foreach (var target in targets)
        {
            if (!SunderPackageTargetKey.TryCreate(target.Role, target.Rid, out var key))
            {
                log.LogError($"Sunder package target '{target.Role}/{target.Rid}' must use a supported exact role and RID.");
                continue;
            }
            if (!seen.Add(key))
            {
                log.LogError($"Sunder package target '{key}' is declared more than once.");
            }
            if (!SunderPackageFormat.IsTargetKind(target.Role, target.Kind))
            {
                log.LogError($"Sunder package target '{key}' kind '{target.Kind}' is invalid for role '{target.Role}'.");
            }
            if (string.Equals(target.Kind, SunderPackageFormat.WebTargetKind, StringComparison.Ordinal)
                && target.Views is not { Count: > 0 })
            {
                log.LogError($"Sunder package web target '{key}' must declare at least one SunderPackageView.");
            }
            if (!ArchiveRelativePath.TryParse(
                    target.EntryPoint,
                    SunderPackageFormat.MaxLogicalPathLength,
                    SunderPackageFormat.MaxLogicalPathDepth,
                    out _,
                    out var error))
            {
                log.LogError($"Sunder package target '{key}' entryPoint '{target.EntryPoint}' is unsafe: {error}.");
            }
        }

        if (log.HasLoggedErrors)
        {
            return null;
        }

        return targets
            .OrderBy(static target => RoleOrder(target.Role))
            .ThenBy(static target => RidOrder(target.Rid))
            .ToArray();
    }

    public static ITaskItem[] ToTaskItems(IEnumerable<SunderPackageTargetManifest> targets)
        => targets.Select(target =>
        {
            var item = new TaskItem($"{target.Role}/{target.Rid}");
            item.SetMetadata("Role", target.Role);
            item.SetMetadata("Rid", target.Rid);
            item.SetMetadata("Kind", target.Kind);
            item.SetMetadata("EntryPoint", target.EntryPoint);
            item.SetMetadata("TargetFramework", target.TargetFramework);
            item.SetMetadata("SdkVersion", target.SdkVersion);
            item.SetMetadata("RequiredHostCapabilities", string.Join(';', target.RequiredHostCapabilities ?? []));
            item.SetMetadata("ExecutionRuntimeId", target.ExecutionRuntime?.Id ?? string.Empty);
            item.SetMetadata("ExecutionRuntimeVersionRange", target.ExecutionRuntime?.VersionRange ?? string.Empty);
            item.SetMetadata("ExecutionRuntimeModuleAbi", target.ExecutionRuntime?.ModuleAbi ?? string.Empty);
            item.SetMetadata("ViewsJson", JsonSerializer.Serialize(target.Views ?? []));
            return (ITaskItem)item;
        }).ToArray();

    public static IReadOnlyList<SunderPackageTargetManifest>? StampRpcCapabilities(
        IReadOnlyList<SunderPackageTargetManifest> targets,
        IReadOnlySet<string> roles,
        TaskLoggingHelper log,
        IReadOnlySet<string>? extendedDeadlineRoles = null)
    {
        var result = new List<SunderPackageTargetManifest>(targets.Count);
        foreach (var target in targets)
        {
            var capabilities = (target.RequiredHostCapabilities ?? [])
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToHashSet(StringComparer.Ordinal);
            if (roles.Contains(target.Role!)
                || string.Equals(target.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal))
            {
                capabilities.Add(SunderPackageFormat.RpcV1Capability);
            }
            if (extendedDeadlineRoles?.Contains(target.Role!) == true)
                capabilities.Add(Sunder.Sdk.Compatibility.SunderSdkCapabilities.RpcExtendedDeadlineV1);
            if (capabilities.Count > SunderPackageFormat.MaxHostCapabilities)
            {
                log.LogError(
                    $"Sunder package target '{target.Role}/{target.Rid}' exceeds the requiredHostCapabilities limit after RPC capability stamping.");
                continue;
            }

            result.Add(new SunderPackageTargetManifest
            {
                Role = target.Role,
                Rid = target.Rid,
                Kind = target.Kind,
                EntryPoint = target.EntryPoint,
                TargetFramework = target.TargetFramework,
                SdkVersion = target.SdkVersion,
                RequiredHostCapabilities = capabilities.Order(StringComparer.Ordinal).Cast<string?>().ToArray(),
                ExecutionRuntime = target.ExecutionRuntime,
                Views = target.Views,
            });
        }
        return log.HasLoggedErrors ? null : result;
    }

    private static IReadOnlyList<SunderPackageTargetManifest>? BuildInferred(
        IReadOnlyList<ITaskItem> configuredRuntimeIdentifiers,
        IReadOnlyList<ITaskItem> configuredViews,
        string packageId,
        IReadOnlySet<string> roles,
        string entryPoint,
        string targetFramework,
        string sdkVersion,
        IReadOnlyList<string> capabilities,
        TaskLoggingHelper log)
    {
        var ridValues = configuredRuntimeIdentifiers.Count == 0
            ? SunderPackageFormat.SupportedRuntimeIdentifiers
            : configuredRuntimeIdentifiers
                .Select(static item => item.ItemSpec.Trim())
                .Where(static value => value.Length > 0)
                .ToArray();
        var rids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rid in ridValues)
        {
            if (!SunderPackageFormat.IsRuntimeIdentifier(rid))
            {
                log.LogError($"SunderPackageRuntimeIdentifiers contains unsupported RID '{rid}'.");
            }
            else if (!rids.Add(rid))
            {
                log.LogError($"SunderPackageRuntimeIdentifiers contains RID '{rid}' more than once.");
            }
        }
        if (log.HasLoggedErrors)
        {
            return null;
        }

        return roles.Where(static role => role == SunderPackageFormat.AppHostRole)
            .SelectMany(role => rids.Select(rid => Create(
                role,
                rid,
                DefaultKind(role),
                entryPoint,
                targetFramework,
                sdkVersion,
                capabilities,
                [])))
            .ToArray();
    }

    private static IReadOnlyList<SunderPackageTargetManifest>? BuildExplicit(
        IReadOnlyList<ITaskItem> configuredTargets,
        IReadOnlyList<ITaskItem> configuredViews,
        string packageId,
        IReadOnlySet<string> inferredRoles,
        string defaultEntryPoint,
        string targetFramework,
        string sdkVersion,
        IReadOnlyList<string> capabilities,
        string? runtimeIdentifier,
        string? outputType,
        bool selfContained,
        bool useAppHost,
        bool publishSingleFile,
        bool publishTrimmed,
        TaskLoggingHelper log)
    {
        var targets = new List<SunderPackageTargetManifest>(configuredTargets.Count);
        foreach (var item in configuredTargets)
        {
            var role = Metadata(item, "Role");
            var rid = Metadata(item, "Rid");
            if ((role is null || rid is null) && item.ItemSpec.Split('/') is [var itemRole, var itemRid])
            {
                role ??= itemRole;
                rid ??= itemRid;
            }
            var kind = role is null ? null : Metadata(item, "Kind") ?? DefaultKind(role);
            if (string.Equals(role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
                && !string.Equals(kind, SunderPackageFormat.WorkerTargetKind, StringComparison.Ordinal))
            {
                log.LogError(
                    $"SunderPackageTarget '{item.ItemSpec}' uses unsupported Runtime target kind '{kind}'. Manifest V{SunderPackageFormat.CurrentManifestVersion} C# Runtime targets must use '{SunderPackageFormat.WorkerTargetKind}'.");
                continue;
            }
            var isWorker = role == SunderPackageFormat.RuntimeHostRole
                           && kind == SunderPackageFormat.WorkerTargetKind;
            if (!SunderPackageFormat.IsHostRole(role)
                || !isWorker && !inferredRoles.Contains(role!))
            {
                log.LogError(
                    $"SunderPackageTarget '{item.ItemSpec}' role '{role}' is not implemented by the package entry assembly.");
                continue;
            }
            var configuredEntryPoint = Metadata(item, "EntryPoint");
            targets.Add(Create(
                role!,
                rid ?? string.Empty,
                kind!,
                configuredEntryPoint is null
                    ? DefaultEntryPoint(kind!, rid, defaultEntryPoint)
                    : PackageAssetDiscovery.NormalizePath(configuredEntryPoint),
                targetFramework,
                sdkVersion,
                capabilities,
                string.Equals(kind, SunderPackageFormat.WebTargetKind, StringComparison.Ordinal)
                    ? PackageWebViewBuilder.BuildForTarget(configuredViews, packageId, role!, rid ?? string.Empty, log)
                    : []));
        }
        ValidateWorkerConfiguration(
            targets,
            defaultEntryPoint,
            runtimeIdentifier,
            outputType,
            selfContained,
            useAppHost,
            publishSingleFile,
            publishTrimmed,
            log);
        return log.HasLoggedErrors ? null : targets;
    }

    private static void ValidateWorkerConfiguration(
        IReadOnlyList<SunderPackageTargetManifest> targets,
        string defaultEntryPoint,
        string? runtimeIdentifier,
        string? outputType,
        bool selfContained,
        bool useAppHost,
        bool publishSingleFile,
        bool publishTrimmed,
        TaskLoggingHelper log)
    {
        var workers = targets
            .Where(static target => target.Kind == SunderPackageFormat.WorkerTargetKind)
            .ToArray();
        if (workers.Length == 0) return;
        if (workers.Length != 1 || targets.Count != 1)
        {
            log.LogError("A C# worker build must declare exactly one explicit Runtime worker target; aggregate exact RID leaves to create a multi-target package.");
            return;
        }

        var worker = workers[0];
        var expectedEntryPoint = DefaultEntryPoint(
            SunderPackageFormat.WorkerTargetKind,
            worker.Rid,
            defaultEntryPoint);
        if (!string.Equals(worker.EntryPoint, expectedEntryPoint, StringComparison.Ordinal))
        {
            log.LogError(
                $"A C# worker target entry point must be the official managed module '{expectedEntryPoint}'; custom worker entry-point names are not supported.");
        }
        if (!SunderPackageFormat.IsRuntimeIdentifier(runtimeIdentifier)
            || !string.Equals(worker.Rid, runtimeIdentifier, StringComparison.Ordinal))
        {
            log.LogError(
                $"Runtime worker target RID '{worker.Rid}' must exactly match the build RuntimeIdentifier '{runtimeIdentifier}'.");
        }
        if (!string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase))
        {
            log.LogError("A C# worker target requires OutputType 'Exe'.");
        }
        if (!string.Equals(worker.TargetFramework, SunderPackageFormat.DotNetWorkerTargetFramework, StringComparison.Ordinal))
        {
            log.LogError($"A C# worker target requires TargetFramework '{SunderPackageFormat.DotNetWorkerTargetFramework}'.");
        }
        if (selfContained)
        {
            log.LogError("A C# worker target requires SelfContained 'false'.");
        }
        if (useAppHost)
        {
            log.LogError("A C# worker target requires UseAppHost 'false'.");
        }
        if (publishSingleFile)
        {
            log.LogError("A C# worker target requires PublishSingleFile 'false'.");
        }
        if (publishTrimmed)
        {
            log.LogError("A C# worker target requires PublishTrimmed 'false'.");
        }
        if (worker.RequiredHostCapabilities?.Contains(
                SunderPackageFormat.WorkerProtocolV1Capability,
                StringComparer.Ordinal) != true)
        {
            log.LogError(
                $"A C# worker target must use the Worker V1 API and infer Host capability '{SunderPackageFormat.WorkerProtocolV1Capability}'.");
        }
        foreach (var capability in (worker.RequiredHostCapabilities ?? [])
                     .Where(static capability => capability is not null
                                                  && capability.StartsWith("worker-protocol.", StringComparison.Ordinal)
                                                   && capability != SunderPackageFormat.WorkerProtocolV1Capability))
        {
            log.LogError(
                $"A C# worker target cannot use unsupported worker protocol capability '{capability}'.");
        }
    }

    private static string DefaultEntryPoint(string kind, string? rid, string managedEntryPoint)
    {
        if (kind != SunderPackageFormat.WorkerTargetKind) return managedEntryPoint;
        return managedEntryPoint;
    }

    private static SunderPackageTargetManifest Create(
        string role,
        string rid,
        string kind,
        string entryPoint,
        string targetFramework,
        string sdkVersion,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<SunderPackageWebViewManifest> views)
    {
        var requiredCapabilities = capabilities.ToHashSet(StringComparer.Ordinal);
        if (kind == SunderPackageFormat.WorkerTargetKind)
        {
            requiredCapabilities.Add(SunderPackageFormat.ExecutionRuntimeV1Capability);
        }

        return new SunderPackageTargetManifest
        {
            Role = role,
            Rid = rid,
            Kind = kind,
            EntryPoint = entryPoint,
            TargetFramework = targetFramework,
            SdkVersion = sdkVersion,
            RequiredHostCapabilities = requiredCapabilities
                .Order(StringComparer.Ordinal)
                .Cast<string?>()
                .ToArray(),
            ExecutionRuntime = kind == SunderPackageFormat.WorkerTargetKind
                ? new SunderPackageExecutionRuntimeManifest
                {
                    Id = SunderPackageFormat.DotNetExecutionRuntimeId,
                    VersionRange = SunderPackageFormat.DotNetExecutionRuntimeVersionRange,
                    ModuleAbi = SunderPackageFormat.DotNetWorkerModuleAbi,
                }
                : null,
            Views = views.Cast<SunderPackageWebViewManifest?>().ToArray(),
        };
    }

    private static string? Metadata(ITaskItem item, string name)
    {
        var value = item.GetMetadata(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string DefaultKind(string role)
        => role == SunderPackageFormat.AppHostRole
            ? SunderPackageFormat.AvaloniaTargetKind
            : SunderPackageFormat.WorkerTargetKind;

    private static int RoleOrder(string? role)
        => role == SunderPackageFormat.AppHostRole ? 0 : 1;

    private static int RidOrder(string? rid)
    {
        for (var index = 0; index < SunderPackageFormat.SupportedRuntimeIdentifiers.Count; index++)
        {
            if (rid == SunderPackageFormat.SupportedRuntimeIdentifiers[index]) return index;
        }
        return int.MaxValue;
    }
}
