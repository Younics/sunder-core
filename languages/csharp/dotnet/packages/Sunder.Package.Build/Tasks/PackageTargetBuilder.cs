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
            item.SetMetadata("ViewsJson", JsonSerializer.Serialize(target.Views ?? []));
            return (ITaskItem)item;
        }).ToArray();

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

        return roles.SelectMany(role => rids.Select(rid => Create(
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
            if (string.Equals(kind, SunderPackageFormat.ProcessTargetKind, StringComparison.Ordinal))
            {
                log.LogError(
                    $"SunderPackageTarget '{item.ItemSpec}' uses legacy target kind '{SunderPackageFormat.ProcessTargetKind}'. Fresh C# package targets must use '{SunderPackageFormat.WorkerTargetKind}'; '{SunderPackageFormat.ProcessTargetKind}' remains supported only for existing legacy package inputs.");
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
            runtimeIdentifier,
            outputType,
            selfContained,
            useAppHost,
            publishSingleFile,
            log);
        return log.HasLoggedErrors ? null : targets;
    }

    private static void ValidateWorkerConfiguration(
        IReadOnlyList<SunderPackageTargetManifest> targets,
        string? runtimeIdentifier,
        string? outputType,
        bool selfContained,
        bool useAppHost,
        bool publishSingleFile,
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
        if (!selfContained)
        {
            log.LogError("A C# worker target requires SelfContained 'true'.");
        }
        if (!useAppHost)
        {
            log.LogError("A C# worker target requires UseAppHost 'true'.");
        }
        if (publishSingleFile)
        {
            log.LogError("A C# worker target does not support PublishSingleFile; package the complete self-contained publish closure.");
        }
    }

    private static string DefaultEntryPoint(string kind, string? rid, string managedEntryPoint)
    {
        if (kind != SunderPackageFormat.WorkerTargetKind) return managedEntryPoint;
        var executable = Path.GetFileNameWithoutExtension(managedEntryPoint);
        return $"lib/{executable}{(rid?.StartsWith("win-", StringComparison.Ordinal) == true ? ".exe" : string.Empty)}";
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
        => new()
        {
            Role = role,
            Rid = rid,
            Kind = kind,
            EntryPoint = entryPoint,
            TargetFramework = targetFramework,
            SdkVersion = sdkVersion,
            RequiredHostCapabilities = capabilities
                .Order(StringComparer.Ordinal)
                .Cast<string?>()
                .ToArray(),
            Views = views.Cast<SunderPackageWebViewManifest?>().ToArray(),
        };

    private static string? Metadata(ITaskItem item, string name)
    {
        var value = item.GetMetadata(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string DefaultKind(string role)
        => role == SunderPackageFormat.AppHostRole
            ? SunderPackageFormat.AvaloniaTargetKind
            : SunderPackageFormat.DotnetTargetKind;

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
