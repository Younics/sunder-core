using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class InstalledPackageExecutionRuntimeBindingTests
{
    [Fact]
    public async Task SchemaOne_RoundTripsEveryImmutableBindingField()
    {
        var paths = CreatePaths();
        var binding = CreateBinding();
        var package = InstalledPackageStoreTests.CreatePackage(paths, "bound.package") with
        {
            ExecutionRuntimeBindings = [binding],
        };
        var store = new InstalledPackageStore(paths);

        await store.WriteAsync([package]);

        var persisted = Assert.Single(await store.ListAsync());
        Assert.Equal(binding, Assert.Single(persisted.ExecutionRuntimeBindings));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<InstalledPackageExecutionRuntimeBindingRecord>)persisted.ExecutionRuntimeBindings).Add(binding));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(paths.StateFilePath))!.AsObject();
        Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task WriteAsync_RejectsNonCanonicalBindingWithoutReplacingCatalog()
    {
        var paths = CreatePaths();
        var store = new InstalledPackageStore(paths);
        var original = InstalledPackageStoreTests.CreatePackage(paths, "bound.package") with
        {
            ExecutionRuntimeBindings = [CreateBinding()],
        };
        await store.WriteAsync([original]);
        var originalJson = await File.ReadAllTextAsync(paths.StateFilePath);
        var invalid = original with
        {
            ExecutionRuntimeBindings = [CreateBinding() with { RegistryOrigin = "https://registry.example" }],
        };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync([invalid]));

        Assert.Contains("invalid execution-runtime binding", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalJson, await File.ReadAllTextAsync(paths.StateFilePath));
    }

    [Fact]
    public async Task Catalog_IsTheOnlyDurableBindingRepresentation()
    {
        var paths = CreatePaths();
        var package = InstalledPackageStoreTests.CreatePackage(paths, "bound.package") with
        {
            ExecutionRuntimeBindings = [CreateBinding()],
        };

        await new InstalledPackageStore(paths).WriteAsync([package]);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(paths.StateFilePath))!.AsObject();
        var persistedBinding = root["packages"]!.AsArray()[0]!["executionRuntimeBindings"]!.AsArray()[0]!.AsObject();
        Assert.Equal(
            [
                "acquisitionSource",
                "moduleAbi",
                "policyObservedAtUtc",
                "registryOrigin",
                "resolvedAtUtc",
                "rid",
                "role",
                "runtimeArtifactSha256",
                "runtimeArtifactSize",
                "runtimeId",
                "runtimeLifecycleState",
                "runtimeManifestSha256",
                "runtimePackagingRevision",
                "runtimePolicyRevision",
                "runtimeVersion",
                "signingKeyId",
                "trustStoreGeneration",
            ],
            persistedBinding.Select(static property => property.Key).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(paths.ExecutionRuntimeStoreRootPath));
        Assert.Equal(
            [paths.StateFilePath],
            Directory.EnumerateFiles(paths.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ToDescriptor_ProjectsBindingAndStrictManifestRequirement()
    {
        var paths = CreatePaths();
        var packageId = "bound.package";
        var version = "1.0.0";
        var installPath = paths.GetInstalledPackagePath(packageId, version);
        var entryAssembly = typeof(PackageSessionOverlayTestPackageModule).Assembly.Location;
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            installPath,
            packageId,
            version,
            entryAssembly,
            roles: [SunderPackageFormat.RuntimeHostRole],
            runtimeIdentifiers: [RuntimeInformation.RuntimeIdentifier],
            runtimeRequiredHostCapabilities:
            [
                "sdk-baseline-1-1.v1",
                "core.v1",
                "execution-runtime.v1",
                "rpc.v1",
                "worker-protocol.v1",
            ]);
        AddExecutionRuntimeRequirement(installPath);
        var binding = CreateBinding(RuntimeInformation.RuntimeIdentifier);
        var package = CanonicalPackageTestBuilder.CreateInstalledRecord(installPath, packageId, version) with
        {
            ExecutionRuntimeBindings = [binding],
        };

        var descriptor = new InstalledPackageStore(paths).ToDescriptor(package);

        var projected = Assert.Single(descriptor.ExecutionRuntimeBindings);
        Assert.Equal(binding.Role, projected.Role);
        Assert.Equal(binding.Rid, projected.Rid);
        Assert.Equal(">=10.0.0 <11.0.0", projected.Requirement.VersionRange);
        Assert.Equal(binding.RuntimeId, projected.Runtime.RuntimeId);
        Assert.Equal(binding.RuntimeVersion, projected.Runtime.Version);
        Assert.Equal(binding.RuntimePackagingRevision, projected.Runtime.PackagingRevision);
        Assert.Equal(binding.RuntimeManifestSha256, projected.Runtime.ManifestSha256);
        Assert.Equal(binding.RuntimeArtifactSha256, projected.Runtime.ArtifactSha256);
        Assert.Equal(binding.RuntimePolicyRevision, projected.Lifecycle.RuntimePolicyRevision);
        Assert.Equal(binding.PolicyObservedAtUtc, projected.Lifecycle.PolicyObservedAtUtc);
        Assert.Equal(binding.ResolvedAtUtc, projected.ResolvedAtUtc);
    }

    [Fact]
    public async Task Preparation_CarriesExactRoleRidBindingAndRejectsCommittedMismatches()
    {
        var paths = CreatePaths();
        var packageId = "bound.package";
        var version = "1.0.0";
        var rid = RuntimeInformation.RuntimeIdentifier;
        var installPath = paths.GetInstalledPackagePath(packageId, version);
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            installPath,
            packageId,
            version,
            typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: [SunderPackageFormat.RuntimeHostRole],
            runtimeIdentifiers: [rid]);
        var binding = CreateBinding(rid);
        var package = CanonicalPackageTestBuilder.CreateInstalledRecord(
            installPath,
            packageId,
            version,
            executionRuntimeBindings: [binding]);
        var preparer = new PackageSessionPreparer(rid);
        var errors = new List<string>();

        var prepared = await preparer.PrepareInstalledPackageAsync(
            0,
            package,
            installPath,
            Path.Combine(paths.RootPath, "valid-session"),
            errors,
            CancellationToken.None);

        Assert.NotNull(prepared);
        Assert.Empty(errors);
        Assert.Equal(
            RuntimePackageExecutionRuntimeBinding.FromInstalled(binding),
            prepared.ExecutionRuntimeBinding);

        errors.Clear();
        var mismatch = package with
        {
            ExecutionRuntimeBindings = [binding with { RuntimeVersion = "11.0.0" }],
        };
        Assert.Null(await preparer.PrepareInstalledPackageAsync(
            0,
            mismatch,
            installPath,
            Path.Combine(paths.RootPath, "mismatch-session"),
            errors,
            CancellationToken.None));
        Assert.Contains(errors, error => error.Contains(
            "does not carry one exact execution-runtime binding",
            StringComparison.Ordinal));

        errors.Clear();
        var mismatchedTarget = package with
        {
            ExecutionRuntimeBindings =
            [
                binding with
                {
                    Rid = SunderPackageFormat.SupportedRuntimeIdentifiers.First(candidate => candidate != rid),
                },
            ],
        };
        var mismatchedTargetSession = Path.Combine(paths.RootPath, "mismatched-target-session");
        var testPreparer = new PackageSessionPreparer(
            rid,
            ephemeralRuntimeBindingResolver: TestExecutionRuntimeLaunchResolver.Instance);

        Assert.Null(await testPreparer.PrepareInstalledPackageAsync(
            0,
            mismatchedTarget,
            installPath,
            mismatchedTargetSession,
            errors,
            CancellationToken.None));
        Assert.Contains(errors, error => error.Contains(
            "does not carry one exact execution-runtime binding",
            StringComparison.Ordinal));
        Assert.False(Directory.Exists(mismatchedTargetSession));
    }

    private static InstalledPackageExecutionRuntimeBindingRecord CreateBinding(string rid = "osx-arm64")
        => new(
            SunderPackageFormat.RuntimeHostRole,
            rid,
            SunderExecutionRuntimeFormat.DotNetRuntimeId,
            "10.0.1",
            2,
            SunderExecutionRuntimeFormat.DotNetModuleAbi,
            new string('a', 64),
            new string('b', 64),
            104857600,
            "https://registry.example/",
            RuntimeExecutionRuntimeAcquisitionSources.Registry,
            "sunder-runtime-release-2026-01",
            2,
            42,
            "supported",
            DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
            DateTimeOffset.Parse("2026-07-11T10:01:00Z"));

    private static RuntimePackagePaths CreatePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "sunder-runtime-binding-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new RuntimePackagePaths(root);
    }

    private static void AddExecutionRuntimeRequirement(string installPath)
    {
        var manifestPath = Path.Combine(installPath, SunderPackageFormat.ManifestPath.Replace('/', Path.DirectorySeparatorChar));
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        foreach (var target in manifest["targets"]!.AsArray().Select(static target => target!.AsObject()))
        {
            target["executionRuntime"] = new JsonObject
            {
                ["id"] = SunderExecutionRuntimeFormat.DotNetRuntimeId,
                ["versionRange"] = ">=10.0.0 <11.0.0",
                ["moduleAbi"] = SunderExecutionRuntimeFormat.DotNetModuleAbi,
            };
        }
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        CanonicalPackageTestBuilder.WriteContentIndex(installPath);
    }
}
