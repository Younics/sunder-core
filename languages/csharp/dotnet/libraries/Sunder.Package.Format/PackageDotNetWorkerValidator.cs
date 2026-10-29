using System.Text;
using System.Text.Json;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Format;

internal static class PackageDotNetWorkerValidator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Validate(
        SunderPackageManifest manifest,
        IReadOnlyDictionary<string, string> physicalFiles,
        ICollection<string> errors,
        IReadOnlySet<SunderPackageTargetKey>? acquiredTargets = null)
    {
        var paths = physicalFiles.Keys
            .Select(path => ArchiveRelativePath.Parse(
                path,
                SunderPackageFormat.MaxArchivePathLength,
                SunderPackageFormat.MaxArchivePathDepth))
            .ToArray();
        foreach (var target in (manifest.Targets ?? []).OfType<SunderPackageTargetManifest>())
        {
            if (!IsDotNetWorker(target)
                || !SunderPackageTargetKey.TryCreate(target.Role, target.Rid, out var key)
                || acquiredTargets is not null && !acquiredTargets.Contains(key))
            {
                continue;
            }

            try
            {
                ValidateTarget(target, SunderPackageTargetResolver.CreateProjectionPlan(key, paths), physicalFiles, errors);
            }
            catch (InvalidDataException exception)
            {
                errors.Add($"Package .NET Worker target '{key}' has an invalid payload union: {exception.Message}");
            }
        }
    }

    private static void ValidateTarget(
        SunderPackageTargetManifest target,
        SunderPackageProjectionPlan plan,
        IReadOnlyDictionary<string, string> physicalFiles,
        ICollection<string> errors)
    {
        if (!ArchiveRelativePath.TryParse(
                target.EntryPoint,
                SunderPackageFormat.MaxLogicalPathLength,
                SunderPackageFormat.MaxLogicalPathDepth,
                out var entryPoint,
                out _)
            || !entryPoint.ToString().EndsWith(".dll", StringComparison.Ordinal))
        {
            return;
        }

        var entryPointText = entryPoint.ToString();
        var moduleBase = entryPointText[..^".dll".Length];
        var runtimeConfigPath = ArchiveRelativePath.Parse(moduleBase + ".runtimeconfig.json");
        var depsPath = ArchiveRelativePath.Parse(moduleBase + ".deps.json");
        var runtimeConfigFile = ResolveRequiredFile(target, plan, runtimeConfigPath, physicalFiles, errors);
        var depsFile = ResolveRequiredFile(target, plan, depsPath, physicalFiles, errors);

        if (runtimeConfigFile is not null)
        {
            ValidateRuntimeConfig(target, runtimeConfigFile, errors);
        }
        var dependencyInfo = depsFile is null
            ? DependencyInfo.Empty
            : ValidateDependencies(target, depsFile, errors);

        var logicalPaths = plan.Files
            .Select(static file => file.LogicalPath.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var logicalFileNames = logicalPaths
            .Select(DotNetWorkerPackagePolicy.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var requiredAsset in dependencyInfo.RequiredAssetNames)
        {
            if (!logicalFileNames.Contains(requiredAsset))
            {
                errors.Add(
                    $"Package .NET Worker target '{target.Role}/{target.Rid}' is missing dependency asset '{requiredAsset}' declared by '{depsPath}'.");
            }
        }

        foreach (var logicalPath in logicalPaths.Where(static path => path.StartsWith("lib/", StringComparison.Ordinal)))
        {
            if (DotNetWorkerPackagePolicy.IsSymbolFile(logicalPath))
            {
                errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' contains symbol file '{logicalPath}'.");
                continue;
            }
            if (IsXmlDocumentation(logicalPath, logicalPaths))
            {
                errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' contains XML documentation file '{logicalPath}'.");
                continue;
            }
            if (DotNetWorkerPackagePolicy.IsAppHostForEntryPoint(logicalPath, entryPointText))
            {
                errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' contains apphost '{logicalPath}'.");
                continue;
            }
            if (DotNetWorkerPackagePolicy.IsForbiddenRuntimeFile(logicalPath))
            {
                errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' contains forbidden .NET runtime file '{logicalPath}'.");
                continue;
            }
            if (DotNetWorkerPackagePolicy.IsBuildTimePath(logicalPath))
            {
                errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' contains build-time/toolchain file '{logicalPath}'.");
                continue;
            }
            if (logicalPath.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)
                && !logicalPath.Equals(runtimeConfigPath.ToString(), StringComparison.Ordinal))
            {
                errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' contains non-canonical runtimeconfig '{logicalPath}'.");
                continue;
            }
            if (logicalPath.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                && !logicalPath.Equals(depsPath.ToString(), StringComparison.Ordinal))
            {
                errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' contains non-canonical dependency manifest '{logicalPath}'.");
                continue;
            }
            if (DotNetWorkerPackagePolicy.IsPotentialSharedFrameworkAssembly(logicalPath)
                && !dependencyInfo.ManagedAssetNames.Contains(DotNetWorkerPackagePolicy.FileName(logicalPath)))
            {
                errors.Add(
                    $"Package .NET Worker target '{target.Role}/{target.Rid}' contains shared-framework assembly '{logicalPath}' that is not package-owned in '{depsPath}'.");
            }
        }
    }

    private static string? ResolveRequiredFile(
        SunderPackageTargetManifest target,
        SunderPackageProjectionPlan plan,
        ArchiveRelativePath logicalPath,
        IReadOnlyDictionary<string, string> physicalFiles,
        ICollection<string> errors)
    {
        if (!plan.TryResolveLogicalPath(logicalPath, out var physicalPath)
            || !physicalFiles.TryGetValue(physicalPath.ToString(), out var filePath))
        {
            errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' is missing required '{logicalPath}'.");
            return null;
        }
        return filePath;
    }

    private static void ValidateRuntimeConfig(
        SunderPackageTargetManifest target,
        string filePath,
        ICollection<string> errors)
    {
        using var document = ReadJson(filePath, "runtimeconfig", target, errors);
        if (document is null) return;
        if (!TryGetObject(document.RootElement, "runtimeOptions", out var runtimeOptions, errors, target, "runtimeconfig"))
        {
            return;
        }
        if (!TryGetString(runtimeOptions, "tfm", out var tfm)
            || !string.Equals(tfm, target.TargetFramework, StringComparison.Ordinal))
        {
            errors.Add(
                $"Package .NET Worker target '{target.Role}/{target.Rid}' runtimeconfig tfm must match targetFramework '{target.TargetFramework}'.");
        }
        if (runtimeOptions.TryGetProperty("includedFrameworks", out _))
        {
            errors.Add(
                $"Package .NET Worker target '{target.Role}/{target.Rid}' runtimeconfig is self-contained; includedFrameworks is not allowed.");
        }

        var frameworks = new List<JsonElement>();
        if (runtimeOptions.TryGetProperty("framework", out var framework))
        {
            if (framework.ValueKind == JsonValueKind.Object) frameworks.Add(framework);
            else errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' runtimeconfig framework must be an object.");
        }
        if (runtimeOptions.TryGetProperty("frameworks", out var frameworkArray))
        {
            if (frameworkArray.ValueKind == JsonValueKind.Array)
            {
                frameworks.AddRange(frameworkArray.EnumerateArray());
            }
            else
            {
                errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' runtimeconfig frameworks must be an array.");
            }
        }
        if (frameworks.Count != 1 || frameworks[0].ValueKind != JsonValueKind.Object)
        {
            errors.Add(
                $"Package .NET Worker target '{target.Role}/{target.Rid}' runtimeconfig must require exactly one '{SunderPackageFormat.DotNetFrameworkName}' framework.");
            return;
        }

        var requiredFramework = frameworks[0];
        if (!TryGetString(requiredFramework, "name", out var frameworkName)
            || !string.Equals(frameworkName, SunderPackageFormat.DotNetFrameworkName, StringComparison.Ordinal))
        {
            errors.Add(
                $"Package .NET Worker target '{target.Role}/{target.Rid}' runtimeconfig framework name must be '{SunderPackageFormat.DotNetFrameworkName}'.");
        }
        if (!TryGetString(requiredFramework, "version", out var frameworkVersion)
            || !SemanticVersion.TryParse(frameworkVersion, out _)
            || target.ExecutionRuntime?.VersionRange is not { } versionRange
            || !PackageVersionRange.IsSatisfiedBy(frameworkVersion, versionRange))
        {
            errors.Add(
                $"Package .NET Worker target '{target.Role}/{target.Rid}' runtimeconfig framework version must satisfy executionRuntime.versionRange '{target.ExecutionRuntime?.VersionRange}'.");
        }
    }

    private static DependencyInfo ValidateDependencies(
        SunderPackageTargetManifest target,
        string filePath,
        ICollection<string> errors)
    {
        using var document = ReadJson(filePath, "dependency manifest", target, errors);
        if (document is null) return DependencyInfo.Empty;
        if (!TryGetObject(document.RootElement, "runtimeTarget", out var runtimeTarget, errors, target, "dependency manifest")
            || !TryGetString(runtimeTarget, "name", out var runtimeTargetName))
        {
            errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' dependency manifest is missing runtimeTarget.name.");
            return DependencyInfo.Empty;
        }

        var frameworkVersion = target.TargetFramework is { Length: > 3 } ? target.TargetFramework[3..] : string.Empty;
        var expectedRuntimeTarget = $".NETCoreApp,Version=v{frameworkVersion}/{target.Rid}";
        if (!string.Equals(runtimeTargetName, expectedRuntimeTarget, StringComparison.Ordinal))
        {
            errors.Add(
                $"Package .NET Worker target '{target.Role}/{target.Rid}' dependency manifest runtimeTarget.name must be '{expectedRuntimeTarget}'.");
        }
        if (!TryGetObject(document.RootElement, "targets", out var targets, errors, target, "dependency manifest")
            || !targets.TryGetProperty(runtimeTargetName, out var selectedTarget)
            || selectedTarget.ValueKind != JsonValueKind.Object)
        {
            errors.Add(
                $"Package .NET Worker target '{target.Role}/{target.Rid}' dependency manifest is missing target graph '{runtimeTargetName}'.");
            return DependencyInfo.Empty;
        }

        if (document.RootElement.TryGetProperty("libraries", out var libraries)
            && libraries.ValueKind == JsonValueKind.Object)
        {
            foreach (var library in libraries.EnumerateObject())
            {
                ValidateDependencyIdentity(target, library.Name, errors);
            }
        }

        var managedAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requiredAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in selectedTarget.EnumerateObject())
        {
            var forbidden = ValidateDependencyIdentity(target, library.Name, errors);
            if (library.Value.ValueKind != JsonValueKind.Object) continue;
            if (library.Value.TryGetProperty("dependencies", out var dependencies)
                && dependencies.ValueKind == JsonValueKind.Object)
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    ValidateDependencyIdentity(target, dependency.Name, errors);
                }
            }
            if (forbidden) continue;

            AddAssets(library.Value, "runtime", target.Rid!, managedAssets, requiredAssets);
            AddAssets(library.Value, "native", target.Rid!, managedAssets, requiredAssets);
            AddAssets(library.Value, "resources", target.Rid!, managedAssets, requiredAssets);
            AddAssets(library.Value, "runtimeTargets", target.Rid!, managedAssets, requiredAssets);
        }
        return new DependencyInfo(managedAssets, requiredAssets);
    }

    private static bool ValidateDependencyIdentity(
        SunderPackageTargetManifest target,
        string identity,
        ICollection<string> errors)
    {
        if (DotNetWorkerPackagePolicy.IsRuntimePackPackageId(identity))
        {
            errors.Add(
                $"Package .NET Worker target '{target.Role}/{target.Rid}' dependency manifest contains forbidden runtime-pack identity '{identity}'.");
            return true;
        }
        if (DotNetWorkerPackagePolicy.IsToolchainPackageId(identity))
        {
            errors.Add(
                $"Package .NET Worker target '{target.Role}/{target.Rid}' dependency manifest contains forbidden SDK/toolchain identity '{identity}'.");
            return true;
        }
        return false;
    }

    private static void AddAssets(
        JsonElement library,
        string propertyName,
        string rid,
        ISet<string> managedAssets,
        ISet<string> requiredAssets)
    {
        if (!library.TryGetProperty(propertyName, out var assets) || assets.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (var asset in assets.EnumerateObject())
        {
            if (propertyName == "runtimeTargets"
                && asset.Value.ValueKind == JsonValueKind.Object
                && asset.Value.TryGetProperty("rid", out var assetRid)
                && assetRid.ValueKind == JsonValueKind.String
                && !string.Equals(assetRid.GetString(), rid, StringComparison.Ordinal))
            {
                continue;
            }
            var fileName = DotNetWorkerPackagePolicy.FileName(asset.Name);
            requiredAssets.Add(fileName);
            if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                managedAssets.Add(fileName);
            }
        }
    }

    private static JsonDocument? ReadJson(
        string filePath,
        string label,
        SunderPackageTargetManifest target,
        ICollection<string> errors)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length > SunderArchiveExtractionOptions.Default.MaxMetadataJsonBytes)
            {
                throw new InvalidDataException(
                    $"file exceeds the {SunderArchiveExtractionOptions.Default.MaxMetadataJsonBytes}-byte metadata limit");
            }
            return StrictJsonSerializer.ParseDocument(StrictUtf8.GetString(bytes));
        }
        catch (Exception exception) when (exception is JsonException
                                               or InvalidDataException
                                               or IOException
                                               or UnauthorizedAccessException
                                               or DecoderFallbackException)
        {
            errors.Add(
                $"Package .NET Worker target '{target.Role}/{target.Rid}' {label} is invalid: {exception.Message}");
            return null;
        }
    }

    private static bool TryGetObject(
        JsonElement parent,
        string propertyName,
        out JsonElement value,
        ICollection<string> errors,
        SunderPackageTargetManifest target,
        string label)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        errors.Add($"Package .NET Worker target '{target.Role}/{target.Rid}' {label} is missing object '{propertyName}'.");
        value = default;
        return false;
    }

    private static bool TryGetString(JsonElement parent, string propertyName, out string value)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool IsXmlDocumentation(string logicalPath, IReadOnlySet<string> logicalPaths)
        => logicalPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
           && logicalPaths.Contains(logicalPath[..^".xml".Length] + ".dll");

    private static bool IsDotNetWorker(SunderPackageTargetManifest target)
        => string.Equals(target.Role, SunderPackageFormat.RuntimeHostRole, StringComparison.Ordinal)
           && string.Equals(target.Kind, SunderPackageFormat.WorkerTargetKind, StringComparison.Ordinal)
            && (DotNetWorkerProfile.TryGetRuntimeRange(target.TargetFramework, out _)
               || string.Equals(target.ExecutionRuntime?.Id, SunderPackageFormat.DotNetExecutionRuntimeId, StringComparison.Ordinal)
               || string.Equals(target.ExecutionRuntime?.ModuleAbi, SunderPackageFormat.DotNetWorkerModuleAbi, StringComparison.Ordinal));

    private sealed record DependencyInfo(
        IReadOnlySet<string> ManagedAssetNames,
        IReadOnlySet<string> RequiredAssetNames)
    {
        internal static DependencyInfo Empty { get; } = new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}
