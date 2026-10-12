using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Package.Projections;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class InstalledPackageExecutionRuntimeBindingTests
{
    [Fact]
    public async Task SchemaOne_RoundTripsEveryImmutableBindingFieldAndHasOneBindingAuthority()
    {
        using var temp = TemporaryDirectoryScope.Create("official-installed-bindings");
        var paths = new RuntimePackagePaths(temp.RootPath);
        var binding = Binding();
        var package = InstalledPackageStoreTests.CreatePackage(paths, "bound.package") with { ExecutionRuntimeBindings = [binding] };
        var store = new InstalledPackageStore(paths);
        await store.WriteAsync([package]);
        var persisted = Assert.Single(await store.ListAsync());
        Assert.Equal(binding, Assert.Single(persisted.ExecutionRuntimeBindings));
        Assert.Throws<NotSupportedException>(() => ((IList<InstalledPackageExecutionRuntimeBindingRecord>)persisted.ExecutionRuntimeBindings).Add(binding));
        var json = JsonNode.Parse(await File.ReadAllTextAsync(paths.StateFilePath))!;
        Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal(["installedArtifact", "requirement", "resolvedAtUtc", "rid", "role", "runtime"],
            json["packages"]![0]!["executionRuntimeBindings"]![0]!.AsObject().Select(value => value.Key).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(paths.ExecutionRuntimeStoreRootPath));
        Assert.Equal([paths.StateFilePath], Directory.EnumerateFiles(paths.RootPath, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("source")]
    [InlineData("profile")]
    [InlineData("rid")]
    public async Task WriteAsync_RejectsNoncanonicalOfficialEvidenceWithoutReplacingCatalog(string mismatch)
    {
        using var temp = TemporaryDirectoryScope.Create("official-invalid-binding");
        var paths = new RuntimePackagePaths(temp.RootPath);
        var binding = Binding();
        var package = InstalledPackageStoreTests.CreatePackage(paths, "bound.package") with { ExecutionRuntimeBindings = [binding] };
        var store = new InstalledPackageStore(paths);
        await store.WriteAsync([package]);
        var before = await File.ReadAllBytesAsync(paths.StateFilePath);
        var invalid = mismatch switch
        {
            "digest" => binding with { Runtime = binding.Runtime with { VendorChecksumDigest = new('a', 64) } },
            "source" => binding with { Runtime = binding.Runtime with { SourceId = ConfiguredOfficialSourceId.Node } },
            "rid" => binding with { Rid = "linux-musl-x64" },
            _ => binding with { InstalledArtifact = binding.InstalledArtifact with
                { Provenance = binding.InstalledArtifact.Provenance with { Distribution = binding.InstalledArtifact.Provenance.Distribution with { MaterializationProfile = "unreviewed" } } } },
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync([package with { ExecutionRuntimeBindings = [invalid] }]));
        Assert.Equal(before, await File.ReadAllBytesAsync(paths.StateFilePath));
    }

    [Fact]
    public async Task PreparationAndDescriptorCarryExactBindingAndRejectCommittedTargetMismatch()
    {
        using var temp = TemporaryDirectoryScope.Create("official-binding-preparation");
        var paths = new RuntimePackagePaths(temp.RootPath);
        Sunder.Runtime.LocalState.RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var binding = Binding();
        var root = paths.GetInstalledPackagePath("bound.package", "1.0.0");
        CanonicalPackageTestBuilder.WriteExplodedPackage(root, "bound.package", "1.0.0", typeof(PackageSessionOverlayTestPackageModule).Assembly.Location,
            roles: ["runtime"], runtimeIdentifiers: [binding.Rid]);
        var package = CanonicalPackageTestBuilder.CreateInstalledRecord(root, "bound.package", "1.0.0", executionRuntimeBindings: [binding]);
        var projected = Assert.Single(new InstalledPackageStore(paths).ToDescriptor(package).ExecutionRuntimeBindings);
        Assert.Equal(binding.Requirement, projected.Requirement);
        Assert.Equal(binding.Runtime, projected.Runtime);
        Assert.Equal(binding.InstalledArtifact, projected.InstalledArtifact);
        Assert.Equal(binding.ResolvedAtUtc, projected.ResolvedAtUtc);
        var errors = new List<string>();
        var preparer = new PackageSessionPreparer(binding.Rid, projectionCache: new PackageProjectionCache(paths.RootPath));
        var prepared = await preparer.PrepareInstalledPackageAsync(0, package, root, Path.Combine(paths.RootPath, "session"), errors, default);
        Assert.True(prepared is not null, string.Join("; ", errors));
        Assert.NotNull(prepared);
        Assert.Empty(errors);
        Assert.Equal(RuntimePackageExecutionRuntimeBinding.FromInstalled(binding), prepared.ExecutionRuntimeBinding);
        if (prepared.PackageProjectionLease is not null) await prepared.PackageProjectionLease.DisposeAsync();
        var invalid = package with { ExecutionRuntimeBindings = [binding with { Runtime = binding.Runtime with { VendorVersion = "11.0.0" } }] };
        Assert.Null(await preparer.PrepareInstalledPackageAsync(0, invalid, root, Path.Combine(paths.RootPath, "bad-session"), errors, default));
        Assert.Contains(errors, error => error.Contains("does not carry one exact execution-runtime binding", StringComparison.Ordinal));
    }

    internal static InstalledPackageExecutionRuntimeBindingRecord Binding(string? rid = null)
    {
        var value = TestExecutionRuntimeLaunchResolver.CreateBinding("runtime", rid ?? PackageTargetSelection.GetCurrentRuntimeIdentifier());
        return new(value.Role, value.Rid, value.Requirement, value.Runtime, value.InstalledArtifact, value.ResolvedAtUtc);
    }
}
