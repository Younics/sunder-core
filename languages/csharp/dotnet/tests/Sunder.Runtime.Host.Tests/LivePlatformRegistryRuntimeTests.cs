using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class LivePlatformRegistryRuntimeTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_LIVE_PLATFORM_RUNTIME", "SUNDER_LIVE_PLATFORM_INPUT", "SUNDER_LIVE_PLATFORM_PHASE")]
    [Trait("Category", "ExternalArtifact")]
    public async Task RealCatalogPolicyAdmitsOfflineStackAndRevocationSurvivesRestart()
    {
        var inputPath = Environment.GetEnvironmentVariable("SUNDER_LIVE_PLATFORM_INPUT")!;
        Assert.InRange(new FileInfo(inputPath).Length, 1, 32_768);
        using var input = JsonDocument.Parse(await File.ReadAllBytesAsync(inputPath));
        var origin = input.RootElement.GetProperty("registryOrigin").GetString()!;
        var fingerprint = input.RootElement.GetProperty("certificateSha256").GetString()!;
        var folder = Path.Combine(input.RootElement.GetProperty("csharpOutput").GetString()!, "sunder-dev");
        var nodeFolders = JsonSerializer.Deserialize<string[]>(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(inputPath)!, "node-platform-packages.json")))!;
        Assert.Equal(2, nodeFolders.Length); string[] folders = [folder, .. nodeFolders];
        var phase = Environment.GetEnvironmentVariable("SUNDER_LIVE_PLATFORM_PHASE");
        Assert.Contains(phase, new[] { "prepare", "offline", "revoked", "offline-denied" });
        Assert.Equal("https", new Uri(origin).Scheme); Assert.Equal("localhost", new Uri(origin).Host);
        var root = input.RootElement.GetProperty("runtimeRoot").GetString()!; RuntimeLocalState.EnsureInitialized(root);
        var paths = new RuntimePackagePaths(root); var clock = new PolicyContinuousClock(); var policies = new PackageLifecyclePolicyCache(paths, clock);
        using var handler = new HttpClientHandler { AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate is not null
                && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0
                && Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)) == fingerprint };
        using var gate = new NetworkGate(handler, phase is "offline" or "offline-denied");
        using var http = new HttpClient(gate) { Timeout = Timeout.InfiniteTimeSpan };
        var registry = new RegistryHttpClient(new Factory(http), new(), TimeProvider.System);
        using var synchronize = new RegistryPackagePolicySynchronizer(new(registry, clock, TimeProvider.System, policies), policies);
        var store = new DevelopmentImportAuthorityStore(paths, policies);
        var preparation = new DevelopmentImportAuthorityPreparation(registry, store, synchronize, policies, TimeProvider.System);
        foreach (var packageFolder in folders) await ValidatePackage(packageFolder);
        if (phase == "offline") await RunNativeStack(paths, store, folders);
        Assert.Empty(await new InstalledPackageStore(paths).ListAsync());
        if (phase is "offline" or "offline-denied") Assert.Equal(0, gate.Requests);

        async Task ValidatePackage(string packageFolder)
        {
            var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(packageFolder);
            Assert.True(validation.Success, string.Join(" | ", validation.Errors)); var manifest = validation.Manifest!;
            var source = new RuntimePackageSource(manifest.Id!, PackageSourceKind.Dev, packageFolder, ContentIdentity: PackageSessionPreparer.ComputeContentIdentity(packageFolder),
                Manifest: manifest, ContentIndex: validation.ContentIndex);
            var subjects = DevelopmentImportAuthorityStore.Platforms(manifest);
            Assert.Equal(input.RootElement.GetProperty("catalogSha256").GetString(), Assert.Single(subjects).CatalogSha256);
            if (phase == "prepare")
            {
                var result = await preparation.PrepareAsync(new(packageFolder, origin), default);
                Assert.Equal(0, result.PackageArtifacts); Assert.Equal(1, result.PlatformCatalogs);
            }
            if (phase == "revoked") await synchronize.RefreshAsync([new(origin, [], subjects, [])], default);
            if (phase is "revoked" or "offline-denied")
            {
                var receipt = File.ReadAllBytes(store.PathFor(RuntimeRpcPackageMetadata.GetManifestSha256(validation.ContentIndex!)));
                Assert.Equal(PackagePolicyErrorCodes.ContractAuthorityRevoked,
                    (await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => store.ResolveAsync(source, default))).Code);
                if (phase == "revoked")
                    Assert.Equal(PackagePolicyErrorCodes.ContractAuthorityRevoked,
                        (await Assert.ThrowsAsync<PackagePolicyDeniedException>(() => preparation.PrepareAsync(new(packageFolder, origin), default))).Code);
                Assert.Equal(receipt, File.ReadAllBytes(store.PathFor(RuntimeRpcPackageMetadata.GetManifestSha256(validation.ContentIndex!))));
                var errors = new List<string>(); var session = Path.Combine(root, "denied-session", phase!, manifest.Version!); Directory.CreateDirectory(session);
                var prepared = await new PackageSessionPreparer(developmentImports: store).PrepareDevPackageAsync(0, packageFolder, session, errors, default);
                Assert.Null(prepared); Assert.Contains(errors, error => error.Contains(PackagePolicyErrorCodes.ContractAuthorityRevoked, StringComparison.Ordinal));
            }
            else
            {
                source = source with { DevelopmentImports = await store.ResolveAsync(source, default) };
                var retained = Assert.Single(source.DevelopmentImports.Catalogs);
                Assert.Equal(File.ReadAllBytes(Path.Combine(input.RootElement.GetProperty("csharpOutput").GetString()!, "catalog.json")), Convert.FromBase64String(retained));
                var admission = new PackagePolicyAdmission(policies); admission.Require(source, null);
                var group = Assert.Single(admission.Subjects(source, null)); Assert.Empty(group.Packages); Assert.Single(group.Platforms);
                Assert.Equal("valid", policies.PlatformLease(origin, subjects).State);
            }
        }
    }

    private static async Task RunNativeStack(RuntimePackagePaths paths, DevelopmentImportAuthorityStore store, IReadOnlyList<string> folders)
    {
        var runtimes = new SignedCurrentRidExecutionRuntimeFixture(); await runtimes.InitializeAsync();
        RuntimePackageSessionTestHost? host = null; int[] processIds = [];
        try
        {
            var receipt = await runtimes.EnsureDotNetAsync(CurrentRidRuntimeHostTestSupport.GetCurrentDotNetVersion(), 1);
            await runtimes.ObserveSupportedAsync(receipt);
            var node = Node24Availability.Current; Assert.True(node.IsAvailable, node.UnavailableReason);
            var nodeReceipt = await runtimes.EnsureNodeAsync(node.Version!, 1, node.ExecutablePath!);
            await runtimes.ObserveSupportedAsync(nodeReceipt); var logs = new RecordingLogSink();
            host = new(logs.CreateLogger<RuntimePackageSessionTestHost>(), new(paths), new(paths), executionRuntimeLaunchResolver: runtimes.LaunchResolver,
                localExecutionRuntimeBindingResolver: runtimes.LocalBindingResolver, executionRuntimeLifecyclePolicies: runtimes.LifecyclePolicies, developmentImports: store);
            await host.InitializeAsync();
            foreach (var packageFolder in folders)
            {
                var result = await host.LoadStartupDevPackagesAsync([packageFolder]);
                Assert.True(result.Success, string.Join(" | ", result.Errors) + "\n" + string.Join("\n", logs.Entries.Select(value => value.Message + value.Exception)));
                var package = Assert.Single(host.ActiveSession.LoadedPackageMap.Values);
                CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(package, package.ExecutionRuntimeBinding!.RuntimeId == SunderPackageFormat.NodeExecutionRuntimeId ? nodeReceipt : receipt, runtimes);
                processIds = CurrentRidRuntimeHostTestSupport.GetActiveProcessIds(host); Assert.Single(processIds);
                Assert.Empty(package.RpcContractUses);
                var discovery = await host.ListStackExportItemsAsync(); Assert.Empty(discovery.Errors);
                Assert.Equal("item", Assert.Single(discovery.Items).ItemId);
                Assert.True((await host.LoadStartupDevPackagesAsync([])).Success);
                await CurrentRidRuntimeHostTestSupport.AssertProcessesStoppedAsync(processIds);
            }
        }
        finally
        {
            if (host is not null) await host.ShutdownAsync();
            await CurrentRidRuntimeHostTestSupport.AssertProcessesStoppedAsync(processIds);
            await runtimes.DisposeAsync();
        }
    }
    private sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    private sealed class NetworkGate(HttpMessageHandler inner, bool offline) : DelegatingHandler(inner)
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.False(offline, "Platform evidence and activation replay must remain offline."); Requests++;
            return base.SendAsync(request, token);
        }
    }
}
