using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Tests;

internal static class CanonicalPackageTestBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string WriteExplodedPackage(
        string root,
        string packageId,
        string version,
        string entryAssemblySource,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<InstalledPackageDependencyRecord>? dependencies = null,
        IReadOnlyList<string>? runtimeIdentifiers = null,
        string appTargetKind = SunderPackageFormat.AvaloniaTargetKind,
        string runtimeTargetKind = SunderPackageFormat.WorkerTargetKind,
        string? appEntryPoint = null,
        string? runtimeEntryPoint = null,
        IReadOnlyList<SunderPackageWebViewManifest?>? appViews = null,
        IReadOnlyList<SunderPackageContractBundleManifest?>? contractBundles = null,
        IReadOnlyList<SunderPackageContractUseManifest?>? usesContracts = null,
        IReadOnlyList<string>? runtimeRequiredHostCapabilities = null)
    {
        roles ??= [SunderPackageFormat.AppHostRole, SunderPackageFormat.RuntimeHostRole];
        runtimeIdentifiers ??= [RuntimeInformation.RuntimeIdentifier];
        Directory.CreateDirectory(Path.Combine(root, "manifest"));
        var libraryFolder = Path.Combine(root, "payload", "shared", "lib");
        Directory.CreateDirectory(libraryFolder);
        var entryAssemblyName = Path.GetFileName(entryAssemblySource);
        File.Copy(entryAssemblySource, Path.Combine(libraryFolder, entryAssemblyName), overwrite: true);

        if (roles.Contains(SunderPackageFormat.RuntimeHostRole, StringComparer.Ordinal))
        {
            foreach (var rid in runtimeIdentifiers)
            {
                var workerFolder = Path.Combine(root, "payload", SunderPackageFormat.RuntimeHostRole, rid, "lib");
                TestWorkerFixture.CopyPayload(workerFolder, rid);
            }
        }

        var targets = roles
            .SelectMany(role => runtimeIdentifiers.Select(rid => new SunderPackageTargetManifest
            {
                Role = role,
                Rid = rid,
                Kind = role == SunderPackageFormat.AppHostRole
                    ? appTargetKind
                    : runtimeTargetKind,
                EntryPoint = role == SunderPackageFormat.AppHostRole
                    ? appEntryPoint ?? $"lib/{entryAssemblyName}"
                    : runtimeEntryPoint ?? "lib/Sunder.Runtime.Host.TestWorker.dll",
                TargetFramework = "net10.0",
                SdkVersion = "1.1.0",
                RequiredHostCapabilities = role == SunderPackageFormat.RuntimeHostRole
                    ? CreateRuntimeRequiredHostCapabilities(
                        runtimeRequiredHostCapabilities,
                        runtimeTargetKind)
                    : ["sdk-baseline-1-1.v1", "core.v1"],
                ExecutionRuntime = role == SunderPackageFormat.RuntimeHostRole
                                   && runtimeTargetKind == SunderPackageFormat.WorkerTargetKind
                    ? new SunderPackageExecutionRuntimeManifest
                    {
                        Id = SunderPackageFormat.DotNetExecutionRuntimeId,
                        VersionRange = ">=10.0.0 <11.0.0",
                        ModuleAbi = SunderPackageFormat.DotNetWorkerModuleAbi,
                    }
                    : null,
                Views = role == SunderPackageFormat.AppHostRole ? appViews : null,
            }))
            .Cast<SunderPackageTargetManifest?>()
            .ToArray();
        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = packageId,
            Name = packageId,
            Version = version,
            DependsOn = (dependencies ?? [])
                .Select(dependency => new SunderPackageDependencyManifest
                {
                    PackageId = dependency.PackageId,
                    VersionRange = dependency.VersionRange,
                    Presence = "required",
                })
                .ToArray(),
            ContractBundles = contractBundles ?? [],
            PackageArtifactImports = [],
            PackageApiImports = [],
            ProviderImports = [],
            PlatformImports = [],
            UsesContracts = usesContracts ?? [],
            Targets = targets,
        };
        var manifestPath = Path.Combine(root, "manifest", "sunder-package.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        WriteContentIndex(root);
        return root;
    }

    public static InstalledPackageRecord CreateInstalledRecord(
        string root,
        string packageId,
        string version,
        bool isEnabled = true,
        IReadOnlyList<InstalledPackageDependencyRecord>? dependencies = null,
        IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord>? executionRuntimeBindings = null)
    {
        var validation = SunderPackageArchiveInspector.ValidateExtractedPackageAsync(root)
            .GetAwaiter()
            .GetResult();
        if (!validation.Success || validation.Manifest is null || validation.ContentIndex?.Files is null)
        {
            throw new InvalidDataException(string.Join(" | ", validation.Errors));
        }
        return new InstalledPackageRecord(
            packageId,
            validation.Manifest.Name!,
            validation.Manifest.Summary,
            version,
            validation.Manifest.Icon,
            root,
            Path.Combine(root, "manifest", "sunder-package.json"),
            ComputeContentIdentity(root),
            validation.ContentIndex.Files
                .Select(entry => new InstalledPackageContentRecord(entry!.Path!, entry.Sha256!, entry.Size))
                .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
                .ToArray(),
            dependencies ?? [],
            isEnabled,
            DateTimeOffset.UtcNow,
            InstalledPackageProvenanceRecord.Unknown,
            [],
            executionRuntimeBindings ?? CreateExecutionRuntimeBindings(validation.Manifest));
    }

    public static async Task<string> CreateArchiveAsync(string sourceRoot)
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sunderpkg");
        await Task.Run(() => ZipFile.CreateFromDirectory(sourceRoot, archivePath));
        return archivePath;
    }

    public static RuntimePackageTargetSource CreateTargetSource(
        string root,
        PackageSourceKind sourceKind,
        string role = SunderPackageFormat.AppHostRole,
        string? rid = null)
    {
        var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(
                           File.ReadAllText(Path.Combine(root, "manifest", "sunder-package.json")))
                       ?? throw new InvalidDataException("Test package manifest could not be read.");
        var contentIndex = JsonSerializer.Deserialize<SunderPackageContentIndex>(
                               File.ReadAllText(Path.Combine(root, "manifest", "content-index.json")))
                           ?? throw new InvalidDataException("Test package content index could not be read.");
        var key = new SunderPackageTargetKey(role, rid ?? RuntimeInformation.RuntimeIdentifier);
        if (!SunderPackageTargetResolver.TryResolveTarget(manifest, key, out var target))
        {
            throw new InvalidDataException($"Test package does not declare target '{key}'.");
        }

        var source = new RuntimePackageSource(
            manifest.Id!,
            sourceKind,
            root,
            root,
            role == SunderPackageFormat.AppHostRole ? PackageHostRoles.App : PackageHostRoles.Runtime,
            ContentIdentity: sourceKind == PackageSourceKind.Installed ? ComputeContentIdentity(root) : null,
            Manifest: manifest,
            ContentIndex: contentIndex,
            SelectedTargetKey: key,
            SelectedTarget: target);
        return new RuntimePackageTargetSource(source, key, target!);
    }

    public static void WriteContentIndex(string root)
    {
        var indexPath = Path.Combine(root, "manifest", "content-index.json");
        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !PathsEqual(path, indexPath))
            .Select(path =>
            {
                using var stream = File.OpenRead(path);
                return new SunderPackageContentIndexEntry(
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
                    stream.Length);
            })
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        File.WriteAllText(
            indexPath,
            JsonSerializer.Serialize(
                new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, entries),
                JsonOptions));
    }

    private static string ComputeContentIdentity(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in new[]
                 {
                     Path.Combine(root, "manifest", "sunder-package.json"),
                     Path.Combine(root, "manifest", "content-index.json"),
                 })
        {
            using var stream = File.OpenRead(path);
            hash.AppendData(SHA256.HashData(stream));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<InstalledPackageExecutionRuntimeBindingRecord> CreateExecutionRuntimeBindings(
        SunderPackageManifest manifest)
    {
        var policyObservedAtUtc = DateTimeOffset.Parse("2026-07-11T10:00:00Z");
        var resolvedAtUtc = DateTimeOffset.Parse("2026-07-11T10:01:00Z");
        return (manifest.Targets ?? [])
            .Where(target => target is
            {
                Role: SunderPackageFormat.RuntimeHostRole,
                Kind: SunderPackageFormat.WorkerTargetKind,
                ExecutionRuntime: not null,
            })
            .Select(target => new InstalledPackageExecutionRuntimeBindingRecord(
                target!.Role!,
                target.Rid!,
                target.ExecutionRuntime!.Id!,
                "10.0.1",
                1,
                target.ExecutionRuntime.ModuleAbi!,
                new string('a', 64),
                new string('b', 64),
                104857600,
                "https://registry.example/",
                RuntimeExecutionRuntimeAcquisitionSources.Registry,
                "sunder-runtime-release-2026-01",
                1,
                1,
                "supported",
                policyObservedAtUtc,
                resolvedAtUtc))
            .ToArray();
    }

    private static IReadOnlyList<string> CreateRuntimeRequiredHostCapabilities(
        IReadOnlyList<string>? requiredHostCapabilities,
        string runtimeTargetKind)
    {
        var capabilities = requiredHostCapabilities
                           ?? ["sdk-baseline-1-1.v1", "core.v1", "execution-runtime.v1", "rpc.v1", "worker-protocol.v1"];
        if (runtimeTargetKind != SunderPackageFormat.WorkerTargetKind
            || capabilities.Contains(SunderPackageFormat.ExecutionRuntimeV1Capability, StringComparer.Ordinal))
        {
            return capabilities;
        }

        return [.. capabilities, SunderPackageFormat.ExecutionRuntimeV1Capability];
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
