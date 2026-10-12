using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

// Invoked by the private Registry integration lane. Only this test composition
// trusts the exact ephemeral TLS leaf supplied by that lane; Host trust is unchanged.
public sealed class LiveRegistryDevelopmentImportTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_LIVE_REGISTRY_IMPORTS", "SUNDER_LIVE_REGISTRY_IMPORTS_INPUT", "SUNDER_LIVE_REGISTRY_IMPORTS_PHASE")]
    [Trait("Category", "ExternalArtifact")]
    public async Task GeneratedUnpublishedProvidersPrepareOnlineAndReplayExactAuthorityOffline()
    {
        var inputPath = Environment.GetEnvironmentVariable("SUNDER_LIVE_REGISTRY_IMPORTS_INPUT")!;
        Assert.InRange(new FileInfo(inputPath).Length, 1, 32_768);
        var input = JsonSerializer.Deserialize<Input>(await File.ReadAllBytesAsync(inputPath), RegistryActivePolicyClient.Json)!;
        var phase = Environment.GetEnvironmentVariable("SUNDER_LIVE_REGISTRY_IMPORTS_PHASE");
        Assert.Contains(phase, new[] { "prepare", "offline" });
        var origin = new Uri(input.RegistryOrigin);
        Assert.Equal("https", origin.Scheme); Assert.Equal("localhost", origin.Host);
        Assert.Matches("^[0-9a-f]{64}$", input.CertificateSha256); Assert.Equal(3, input.Packages.Count);
        RuntimeLocalState.EnsureInitialized(input.RuntimeRoot);
        var paths = new RuntimePackagePaths(input.RuntimeRoot); var clock = new PolicyContinuousClock();
        var policies = new PackageLifecyclePolicyCache(paths, clock);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate is not null
                && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0
                && Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)) == input.CertificateSha256 };
        using var gate = new OfflineGate(handler, phase == "offline");
        using var http = new HttpClient(gate) { Timeout = Timeout.InfiniteTimeSpan };
        var registry = new RegistryHttpClient(new Factory(http), new(), TimeProvider.System);
        using var synchronizer = new RegistryPackagePolicySynchronizer(new(registry, clock, TimeProvider.System, policies), policies);
        var store = new DevelopmentImportAuthorityStore(paths, policies);
        var preparation = new DevelopmentImportAuthorityPreparation(registry, store, synchronizer, policies, TimeProvider.System);
        var authority = await Source(input.AuthorityFolder, PackageSourceKind.Installed);
        foreach (var folder in input.Packages)
        {
            var source = await Source(folder, PackageSourceKind.Dev);
            if (phase == "prepare")
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => store.ResolveAsync(source, default));
                var prepared = await preparation.PrepareAsync(new(folder, input.RegistryOrigin), default);
                Assert.Equal(source.PackageId, prepared.PackageId);
                Assert.Equal(1, prepared.PackageArtifacts); Assert.Equal(0, prepared.PlatformCatalogs);
            }
            var imports = await store.ResolveAsync(source, default);
            Assert.Equal(source.Manifest!.Version, imports.Version);
            var inventory = Assert.Single(imports.Inventories).Artifact.Inventory;
            Assert.Equal(authority.PackageId, inventory.PackageId); Assert.Equal(2, inventory.PublicContractCount);
            Assert.Equal("1.0.0", inventory.PackageVersion); Assert.Equal("1.1.0", authority.Manifest!.Version);
            Assert.NotEqual(authority.ContentIdentity, inventory.PackageContentIdentity);
            source = source with { DevelopmentImports = imports };
            var graph = PackageCompatibilityAuthority.ResolveDevelopmentGraph([Prepared(source), Prepared(authority)]);
            PackageCompatibilityAuthority.ValidateGraph(graph.Select(value => value.Source).ToArray());
            Assert.Empty(source.Manifest.UsesContracts!);
            Assert.Single(source.Manifest.ProviderImports!);
            Assert.Null(source.Compatibility); Assert.Null(source.InstalledPolicy); Assert.Null(source.RegistryPolicyContentIdentity);
            Assert.Throws<InvalidDataException>(() => PackageCompatibilityAuthority.ValidateGraph([source]));
        }
        Assert.Empty(await new InstalledPackageStore(paths).ListAsync());
        if (phase == "offline")
        {
            var folder = input.Packages[0]; var manifestPath = Path.Combine(folder, SunderPackageFormat.ManifestPath);
            var original = await File.ReadAllBytesAsync(manifestPath);
            try
            {
                await File.AppendAllTextAsync(manifestPath, " "); CanonicalPackageTestBuilder.WriteContentIndex(folder);
                await Assert.ThrowsAsync<InvalidDataException>(async () => await store.ResolveAsync(await Source(folder, PackageSourceKind.Dev), default));
            }
            finally { await File.WriteAllBytesAsync(manifestPath, original); CanonicalPackageTestBuilder.WriteContentIndex(folder); }
            await store.ResolveAsync(await Source(folder, PackageSourceKind.Dev), default);
            await ActivateOfflineProviders(input, paths, store, authority);
        }
        Assert.Equal(phase == "prepare" ? 3 : 0, gate.Requests);
    }

    private static async Task ActivateOfflineProviders(Input input, RuntimePackagePaths paths, DevelopmentImportAuthorityStore imports, RuntimePackageSource authority)
    {
        // The dependency is acquired as metadata only: its real Registry archive
        // proves the owned contract without launching the API suite's fixture DLL.
        var installed = new InstalledPackageStore(paths);
        var destination = paths.GetInstalledPackagePath(authority.PackageId, authority.Manifest!.Version!);
        var projection = await PackageProjectionMaterializer.MaterializeCurrentRidInstallPackageAsync(authority.SourceFolder, destination,
            authority.Manifest, authority.ContentIndex!, PackageTargetSelection.GetCurrentRuntimeIdentifier(), default, acquiredTargets: []);
        Assert.True(projection.Success, string.Join(" | ", projection.Errors));
        var manifest = projection.Manifest!;
        await installed.WriteAsync([new(authority.PackageId, manifest.Name!, manifest.Summary, manifest.Version!, manifest.Icon, destination,
            Path.Combine(destination, SunderPackageFormat.ManifestPath), PackageSessionPreparer.ComputeContentIdentity(destination),
            projection.ContentIndex!.Files!.Select(entry => new InstalledPackageContentRecord(entry.Path!, entry.Sha256!, entry.Size)).ToArray(), [], true, DateTimeOffset.UtcNow,
            new(InstalledPackageSourceKind.Registry, InstalledPackageVersionPolicy.ExplicitVersion, input.RegistryOrigin, authority.PackageId,
                RequestedVersion: manifest.Version, SourceIdentity: authority.ContentIdentity), []) { AcquiredTargets = [], SourcePackageContentIdentity = authority.ContentIdentity }]);
        var runtimes = new OfficialRuntimeFixture(); await runtimes.InitializeAsync();
        RuntimePackageSessionTestHost? host = null; int[] processIds = [];
        try
        {
            var dotnet = await runtimes.EnsureDotNetAsync("10.0.12");
            var node = Node24Availability.Current; Assert.True(node.IsAvailable, node.UnavailableReason);
            var nodeRuntime = await runtimes.EnsureNodeAsync("24.21.0");
            var logs = new RecordingLogSink();
            host = new(logs.CreateLogger<RuntimePackageSessionTestHost>(), installed, new(paths),
                executionRuntimeLaunchResolver: runtimes.LaunchResolver, localExecutionRuntimeBindingResolver: runtimes.LocalBindingResolver,
                developmentImports: imports);
            await host.InitializeAsync();
            foreach (var folder in input.Packages)
            {
                var result = await host.LoadStartupDevPackagesAsync([folder]);
                Assert.True(result.Success, string.Join(" | ", result.Errors) + "\n" + string.Join("\n", logs.Entries.Select(entry => entry.Message + "\n" + entry.Exception)));
                var loaded = Assert.Single(host.ActiveSession.LoadedPackageMap.Values);
                var receipt = loaded.ExecutionRuntimeBinding!.Runtime.RuntimeId == SunderPackageFormat.NodeExecutionRuntimeId ? nodeRuntime : dotnet;
                CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(loaded, receipt, runtimes);
                processIds = CurrentRidRuntimeHostTestSupport.GetActiveProcessIds(host); Assert.Single(processIds);
                var provider = Assert.Single(host.RpcCatalog.GetSnapshot().Providers);
                Assert.Equal("example.owner.first", provider.ProviderId); Assert.Equal("example.dependency.first", provider.ContractId);
                Assert.Empty(loaded.RpcContractUses);
                var handler = Assert.Single(loaded.RpcProviders.Values).Handler;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var context = new SunderRpcInvocationContext(provider.PackageId, provider.PackageVersion, provider, DateTimeOffset.UtcNow.AddSeconds(20), 1, deadline.Token);
                var reply = await handler.InvokeUnaryAsync(context, "messages", "send", JsonSerializer.SerializeToElement(new { }), deadline.Token);
                Assert.Equal("live-provider", reply.GetProperty("message").GetString());
                if (folder == input.Packages[0])
                {
                    var assets = Path.Combine(folder, "payload/shared/assets"); Directory.CreateDirectory(assets);
                    await File.WriteAllTextAsync(Path.Combine(assets, "iteration.txt"), "local payload iteration");
                    CanonicalPackageTestBuilder.WriteContentIndex(folder);
                    var generation = host.SessionGeneration;
                    var reload = await host.LoadDevPackageFromRuntimeInputAsync(folder, watch: false);
                    Assert.True(reload.Success, string.Join(" | ", reload.Errors)); Assert.True(host.SessionGeneration > generation);
                    await CurrentRidRuntimeHostTestSupport.AssertProcessesStoppedAsync(processIds);
                    processIds = CurrentRidRuntimeHostTestSupport.GetActiveProcessIds(host); Assert.Single(processIds);
                    var retained = Assert.Single(host.RpcCatalog.GetSnapshot().Providers); generation = host.SessionGeneration;
                    var manifestPath = Path.Combine(folder, SunderPackageFormat.ManifestPath); var bytes = await File.ReadAllBytesAsync(manifestPath);
                    try
                    {
                        await File.AppendAllTextAsync(manifestPath, " "); CanonicalPackageTestBuilder.WriteContentIndex(folder);
                        Assert.False((await host.LoadDevPackageFromRuntimeInputAsync(folder, watch: false)).Success);
                        Assert.Equal(generation, host.SessionGeneration);
                        Assert.Equal(retained, Assert.Single(host.RpcCatalog.GetSnapshot().Providers));
                    }
                    finally { await File.WriteAllBytesAsync(manifestPath, bytes); CanonicalPackageTestBuilder.WriteContentIndex(folder); }
                }
                Assert.True((await host.LoadStartupDevPackagesAsync([])).Success);
                if (folder == input.Packages[0])
                    Assert.True((await host.UnloadPackageSessionAsync("example.owner", PackageSourceKind.Dev)).Success);
                await CurrentRidRuntimeHostTestSupport.AssertProcessesStoppedAsync(processIds);
                Assert.Empty(host.RpcCatalog.GetSnapshot().Providers);
            }
            Assert.Equal(authority.PackageId, Assert.Single(await installed.ListAsync()).PackageId);
        }
        finally
        {
            if (host is not null) await host.ShutdownAsync();
            await CurrentRidRuntimeHostTestSupport.AssertProcessesStoppedAsync(processIds);
            await runtimes.DisposeAsync();
        }
    }

    private static async Task<RuntimePackageSource> Source(string folder, PackageSourceKind kind)
    {
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(folder);
        Assert.True(validation.Success, string.Join(" | ", validation.Errors));
        var manifest = validation.Manifest!;
        return new(manifest.Id!, kind, folder, ContentIdentity: PackageSessionPreparer.ComputeContentIdentity(folder), Manifest: manifest,
            ContentIndex: validation.ContentIndex) { Dependencies = (manifest.DependsOn ?? []).Select(value => new PackageDependencyDescriptor(value.PackageId!, value.VersionRange!)).ToArray() };
    }
    private static PreparedRuntimePackage Prepared(RuntimePackageSource source) => new(source.SourceFolder, source, source.SourceFolder,
        source.PackageId, source.Manifest!.Version!, PackageHostRoles.None, new(source.PackageId, source.PackageId, source.Manifest.Version!, PackageHostRoles.None, null),
        null, null, null, source.PackageDependencies);
    private sealed record Input(string RegistryOrigin, string CertificateSha256, string RuntimeRoot, string AuthorityFolder, IReadOnlyList<string> Packages);
    private sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    private sealed class OfflineGate(HttpMessageHandler inner, bool offline) : DelegatingHandler(inner)
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("localhost", request.RequestUri!.Host);
            Assert.DoesNotContain("execution-runtimes", request.RequestUri.AbsolutePath, StringComparison.Ordinal);
            Assert.False(offline, "Offline Runtime import replay must not make a Registry request.");
            Requests++; return base.SendAsync(request, token);
        }
    }
}
