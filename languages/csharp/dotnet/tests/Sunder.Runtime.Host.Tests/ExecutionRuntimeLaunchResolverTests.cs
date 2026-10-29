using System.Security.Cryptography;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeLaunchResolverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_UsesExactReceiptRecipeAndWarmVerification(bool nodeStyle)
    {
        using var fixture = new LaunchFixture(nodeStyle);
        await using var launch = await fixture.Resolver.ResolveAsync(fixture.Package, Guid.NewGuid(), default);
        Assert.Equal(fixture.RuntimeRoot, launch.RuntimeRootPath);
        Assert.Equal(fixture.LauncherPath, launch.LauncherPath);
        Assert.Equal(nodeStyle ? [fixture.EntryPoint] : new[] { "exec", fixture.EntryPoint }, launch.ArgumentTokens);
        Assert.Equal([false], fixture.Store.Depths);
        Assert.Equal(1, fixture.Store.ReceiptReads);
        Assert.Null(launch.ActivationWarning);
    }

    [Fact]
    public async Task Resolve_DoesNotRehashRuntimeLauncherAfterWarmVerification()
    {
        using var fixture = new LaunchFixture(false);
        await File.WriteAllBytesAsync(fixture.LauncherPath, [1, 2, 3]);
        await using var launch = await fixture.Resolver.ResolveAsync(fixture.Package, Guid.NewGuid(), default);
        Assert.Equal(fixture.LauncherPath, launch.LauncherPath);
        Assert.Equal([false], fixture.Store.Depths);
    }

    [Fact]
    public async Task Resolve_WarmHealthFailureBlocksBeforeLeaseReservation()
    {
        using var fixture = new LaunchFixture(false);
        fixture.Store.Healthy = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Resolver.ResolveAsync(fixture.Package, Guid.NewGuid(), default));
        Assert.Empty(fixture.Store.Leases);
        Assert.Equal([false], fixture.Store.Depths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_RejectsReceiptThatDiffersFromCommittedBinding(bool provenance)
    {
        using var fixture = new LaunchFixture(false);
        var binding = fixture.Package.ExecutionRuntimeBinding!;
        var changed = provenance
            ? binding.InstalledArtifact with { Provenance = binding.InstalledArtifact.Provenance with { RetrievedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) } }
            : binding.InstalledArtifact with { ArchiveSha256 = new('f', 64) };
        var package = fixture.Package with { ExecutionRuntimeBinding = binding with { InstalledArtifact = changed } };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Resolver.ResolveAsync(package, Guid.NewGuid(), default));
        Assert.Empty(fixture.Store.Depths);
        Assert.Empty(fixture.Store.Leases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolve_RejectsProjectedEntryPointEscapeOrHashMismatch(bool hashMismatch)
    {
        using var fixture = new LaunchFixture(false);
        var package = fixture.Package;
        if (hashMismatch) await File.WriteAllTextAsync(fixture.EntryPoint, "changed after preparation");
        else
        {
            var escaped = Path.Combine(fixture.Root, "escaped.dll");
            await File.WriteAllBytesAsync(escaped, [1, 2, 3, 4]);
            package = package with { EntryAssemblyPath = escaped };
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Resolver.ResolveAsync(package, Guid.NewGuid(), default));
        Assert.True(Assert.Single(fixture.Store.Leases).Released);
    }

    [Fact]
    public async Task Resolve_DevelopmentPackageWithoutExplicitEphemeralBindingFailsClosed()
    {
        using var fixture = new LaunchFixture(false);
        var package = fixture.Package with { Source = fixture.Package.Source with { Kind = PackageSourceKind.Dev }, ExecutionRuntimeBinding = null };
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Resolver.ResolveAsync(package, Guid.NewGuid(), default));
        Assert.Equal(0, fixture.Store.ReceiptReads);
    }

    [Fact]
    public async Task Resolve_VendorSupportObservationIsNotAnExecutionLease()
    {
        using var fixture = new LaunchFixture(false, RuntimeExecutionRuntimeSupportState.EndOfLife);
        await using var launch = await fixture.Resolver.ResolveAsync(fixture.Package, Guid.NewGuid(), default);
        Assert.Equal(fixture.LauncherPath, launch.LauncherPath);
        Assert.Equal([false], fixture.Store.Depths);
    }

    [Theory]
    [InlineData(false, 11)]
    [InlineData(true, 26)]
    public async Task Resolve_ValidHistoricalReceiptCannotAuthorizeAnUnsupportedMajor(bool nodeStyle, int major)
    {
        using var fixture = new LaunchFixture(nodeStyle, major: major);
        var binding = fixture.Package.ExecutionRuntimeBinding!;
        OfficialRuntimeSourcePolicy.ValidateInstalledArtifact(binding.Runtime, binding.InstalledArtifact);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Resolver.ResolveAsync(fixture.Package, Guid.NewGuid(), default));
        Assert.Contains("outside Sunder's supported range", error.Message);
        Assert.Equal(0, fixture.Store.ReceiptReads);
        Assert.Empty(fixture.Store.Leases);
    }

    private sealed class LaunchFixture : IDisposable
    {
        private readonly TemporaryDirectoryScope _temporary = TemporaryDirectoryScope.Create("official-runtime-launch");
        internal string Root => _temporary.RootPath;
        internal string RuntimeRoot { get; }
        internal string LauncherPath { get; }
        internal string EntryPoint { get; }
        internal PreparedRuntimePackage Package { get; }
        internal LaunchStore Store { get; }
        internal ExecutionRuntimeLaunchResolver Resolver { get; }

        internal LaunchFixture(bool node, RuntimeExecutionRuntimeSupportState support = RuntimeExecutionRuntimeSupportState.Supported, int? major = null)
        {
            var rid = PackageTargetSelection.GetCurrentRuntimeIdentifier();
            var runtimeMajor = major ?? (node ? 24 : 10);
            var version = node ? $"{runtimeMajor}.21.0" : $"{runtimeMajor}.0.12";
            var identity = new RuntimeExecutionRuntimeIdentity(node ? ConfiguredOfficialSourceId.Node : ConfiguredOfficialSourceId.DotNet,
                node ? SunderPackageFormat.NodeExecutionRuntimeId : SunderPackageFormat.DotNetExecutionRuntimeId,
                version, rid, node ? SunderPackageFormat.NodeWorkerModuleAbi : SunderPackageFormat.DotNetWorkerModuleAbi,
                node ? RuntimeExecutionRuntimeChecksumAlgorithm.Sha256 : RuntimeExecutionRuntimeChecksumAlgorithm.Sha512,
                new('b', node ? 64 : 128));
            var requirement = new RuntimeExecutionRuntimeRequirement(identity.RuntimeId,
                node ? $">={runtimeMajor}.18.1 <{runtimeMajor + 1}.0.0" : $">={runtimeMajor}.0.0 <{runtimeMajor + 1}.0.0", identity.ModuleAbi);
            var projection = Path.Combine(Root, "projection");
            Directory.CreateDirectory(projection);
            var entryName = node ? "worker.cjs" : "worker.dll";
            EntryPoint = Path.Combine(projection, entryName);
            byte[] bytes = [1, 2, 3, 4];
            File.WriteAllBytes(EntryPoint, bytes);
            var target = new SunderPackageTargetManifest
            {
                Role = "runtime", Rid = rid, Kind = "worker", EntryPoint = entryName,
                TargetFramework = node ? $"node{runtimeMajor}" : $"net{runtimeMajor}.0", SdkVersion = "1.1.0",
                RequiredHostCapabilities = ["core.v1", "execution-runtime.v1", "rpc.v1", "sdk-baseline-1-1.v1", "worker-protocol.v1"],
                ExecutionRuntime = new() { Id = identity.RuntimeId, VersionRange = requirement.VersionRange, ModuleAbi = identity.ModuleAbi },
            };
            var manifest = new SunderPackageManifest
            {
                ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion, ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
                Id = "launch.test", Name = "Launch Test", Version = "1.0.0", Targets = [target], DependsOn = [], ContractBundles = [], UsesContracts = [], Provides = [],
            };
            var content = new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion,
                [new($"payload/runtime/{rid}/{entryName}", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length)]);
            var key = new SunderPackageTargetKey("runtime", rid);
            var source = new RuntimePackageSource(manifest.Id, PackageSourceKind.Installed, projection, projection,
                PackageHostRoles.Runtime, [], new('c', 64), manifest, content, key, target);
            var layout = new ExecutionRuntimeStoreLayout(Path.Combine(Root, "store"));
            RuntimeRoot = layout.GetContentRootPath(new('d', 64));
            var launcher = OfficialRuntimeMaterializer.LauncherPath(identity);
            LauncherPath = Path.Combine(RuntimeRoot, launcher);
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherPath)!);
            File.WriteAllBytes(LauncherPath, bytes);
            var archiveName = OfficialRuntimeSourcePolicy.ArchiveName(identity.SourceId, version, rid);
            var url = node ? $"https://nodejs.org/dist/v{version}/{archiveName}"
                : $"https://builds.dotnet.microsoft.com/dotnet/Runtime/{version}/{archiveName}";
            var distribution = new RuntimeExecutionRuntimeDistribution(url, OfficialRuntimeSourcePolicy.MetadataUri(identity.SourceId, runtimeMajor).AbsoluteUri,
                new('a', 64), node ? OfficialRuntimeSourcePolicy.ChecksumUri(version).AbsoluteUri : null, node ? new('e', 64) : null,
                rid.StartsWith("win-", StringComparison.Ordinal) ? RuntimeExecutionRuntimeArchiveFormat.Zip : RuntimeExecutionRuntimeArchiveFormat.TarGzip,
                OfficialRuntimeSourcePolicy.Profile(identity.SourceId, version), null);
            var now = DateTimeOffset.UtcNow;
            var receipt = new ExecutionRuntimeReceiptV1(1, ExecutionRuntimeIdentity.FromContract(identity), new('b', 64), 100, new('d', 64),
                launcher, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length,
                node ? [OfficialRuntimeSourcePolicy.EntryPointToken] : ["exec", OfficialRuntimeSourcePolicy.EntryPointToken],
                [new(launcher, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, OfficialRuntimeMaterializer.ExecutableMode)],
                new(distribution, url, now), new(support, node ? $"{runtimeMajor} LTS" : $"{runtimeMajor}.0 {(runtimeMajor % 2 == 0 ? "LTS" : "STS")}", now.AddYears(-2), now.AddYears(-1), null), now);
            Package = new PreparedRuntimePackage(projection, source, projection, manifest.Id, manifest.Version, PackageHostRoles.Runtime,
                new(manifest.Id, manifest.Name, manifest.Version, PackageHostRoles.Runtime, null, key, target), EntryPoint, key, target,
                [], new Dictionary<string, Sunder.Sdk.Rpc.SunderRpcContractDescriptor>(), new('f', 64))
            { ExecutionRuntimeBinding = new("runtime", rid, requirement, identity, receipt.InstalledArtifact, now) };
            Store = new(layout, receipt);
            Resolver = new(Store);
        }
        public void Dispose() => _temporary.Dispose();
    }

    private sealed class LaunchStore(ExecutionRuntimeStoreLayout layout, ExecutionRuntimeReceiptV1 receipt) : IExecutionRuntimeLaunchStore
    {
        public ExecutionRuntimeStoreLayout Layout => layout;
        internal int ReceiptReads { get; private set; }
        internal bool Healthy { get; set; } = true;
        internal List<bool> Depths { get; } = [];
        internal List<TestExecutionRuntimeProcessLease> Leases { get; } = [];
        public Task<ExecutionRuntimeReceiptV1?> GetReceiptAsync(ExecutionRuntimeIdentity identity, CancellationToken token)
        { token.ThrowIfCancellationRequested(); ReceiptReads++; return Task.FromResult<ExecutionRuntimeReceiptV1?>(receipt); }
        public Task<ExecutionRuntimeVerificationResult> VerifyAsync(ExecutionRuntimeIdentity identity, bool deep, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Depths.Add(deep); return Task.FromResult(new ExecutionRuntimeVerificationResult(Healthy, deep, DateTimeOffset.UtcNow, Healthy ? null : "Missing health marker.")); }
        public Task<IExecutionRuntimeProcessLease> ReserveProcessLeaseAsync(ExecutionRuntimeLeaseOwner owner, string contentIdentity,
            string runtimeRootPath, string executablePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var lease = new TestExecutionRuntimeProcessLease(runtimeRootPath);
            Leases.Add(lease);
            return Task.FromResult<IExecutionRuntimeProcessLease>(lease);
        }
    }
}
