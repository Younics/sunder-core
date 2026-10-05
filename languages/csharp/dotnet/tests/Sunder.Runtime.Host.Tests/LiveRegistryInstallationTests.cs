using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class LiveRegistryInstallationTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_LIVE_INSTALLATION", "SUNDER_LIVE_INSTALLATION_INPUT", "SUNDER_LIVE_INSTALLATION_PHASE")]
    [Trait("Category", "ExternalArtifact")]
    public async Task RealRegistryTransactionsPreserveReadinessAtomicityAndDurablePolicy()
    {
        var inputPath = Environment.GetEnvironmentVariable("SUNDER_LIVE_INSTALLATION_INPUT")!;
        Assert.InRange(new FileInfo(inputPath).Length, 1, 32_768);
        var input = JsonSerializer.Deserialize<Input>(await File.ReadAllBytesAsync(inputPath), RegistryActivePolicyClient.Json)!;
        var phase = Environment.GetEnvironmentVariable("SUNDER_LIVE_INSTALLATION_PHASE")!;
        Assert.Contains(phase, new[] { "matrix", "install", "update", "offline", "denied", "offline-denied" });
        Assert.Equal("https", new Uri(input.RegistryOrigin).Scheme); Assert.Equal("localhost", new Uri(input.RegistryOrigin).Host);
        if (phase == "matrix") foreach (var scenario in input.Matrices) await Exercise(scenario.PackageId, scenario);
        else await Exercise("available", null);

        async Task Exercise(string name, Scenario? scenario)
        {
            var root = Path.Combine(input.RuntimeRoot, name); RuntimeLocalState.EnsureInitialized(root); var paths = new RuntimePackagePaths(root);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate is not null
                && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0 && Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)) == input.CertificateSha256 };
            using var network = new NetworkGate(handler, phase is "offline" or "offline-denied"); using var http = new HttpClient(network) { Timeout = Timeout.InfiniteTimeSpan };
            var services = new ServiceCollection(); services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator("installation-test"));
            services.AddSingleton<IHttpClientFactory>(new Factory(http));
            await using var provider = services.BuildServiceProvider();
            var lifecycle = provider.GetRequiredService<InstalledPackageLifecycleService>(); await lifecycle.InitializeAsync();
            var installed = provider.GetRequiredService<InstalledPackageStore>(); var changes = provider.GetRequiredService<RegistryPackageChangeOrchestrator>();
            try
            {
                if (scenario is not null)
                {
                    if (scenario.Optional && scenario.Required)
                    {
                        var denied = await Assert.ThrowsAsync<RuntimeRequiredProviderException>(() => changes.InstallAsync(Request(scenario.PackageId, "1.0.0"), default));
                        Assert.Equal(RpcReadinessErrorCodes.RequiredProviderUnsatisfied, denied.Code);
                        Assert.Empty(await installed.ListAsync()); Assert.False(provider.GetRequiredService<PackageStoreCoordinator>().HasPreparedStages); return;
                    }
                    var result = await changes.InstallAsync(Request(scenario.PackageId, "1.0.0"), default);
                    Assert.Equal(scenario.Optional && !scenario.Required, result.Success);
                    var records = await installed.ListAsync();
                    if (result.Success)
                    {
                        var consumer = Assert.Single(records); Assert.Equal(scenario.PackageId, consumer.PackageId); Assert.Empty(consumer.DependsOn);
                        Assert.Equal("omitted", Assert.Single(consumer.Compatibility!.Resolution.OptionalPackages).Disposition);
                        Assert.NotNull(consumer.Policy);
                    }
                    else
                    {
                        Assert.Empty(records);
                        if (scenario.Optional) Assert.Contains(RpcReadinessErrorCodes.RequiredProviderUnsatisfied, JsonSerializer.Serialize(result));
                    }
                    Assert.False(provider.GetRequiredService<PackageStoreCoordinator>().HasPreparedStages); return;
                }
                if (phase == "install")
                {
                    var result = await changes.InstallAsync(Request("example.install.consumer", "1.0.0"), default);
                    Assert.True(result.Success, JsonSerializer.Serialize(result)); Assert.True(result.RuntimeSessionApplied);
                    Assert.Equal(2, (await installed.ListAsync()).Count);
                }
                if (phase == "update")
                {
                    var result = await changes.InstallAsync(Request("example.install.authority", "1.1.0"), default);
                    Assert.True(result.Success, JsonSerializer.Serialize(result));
                    var before = File.ReadAllBytes(paths.StateFilePath);
                    var rejected = await changes.InstallAsync(Request("example.install.authority", "2.0.0"), default);
                    Assert.False(rejected.Success); Assert.Equal(before, File.ReadAllBytes(paths.StateFilePath));
                }
                if (phase == "denied")
                {
                    await provider.GetRequiredService<ActivePackagePolicySynchronizer>().RefreshInstalledAsync(default);
                    await provider.GetRequiredService<InstalledPackagePolicyCoordinator>().ReconcileAsync(default);
                }
                var state = await installed.ListAsync(); var imported = Assert.Single(state, value => value.PackageId == "example.install.consumer");
                Assert.NotNull(imported.Compatibility); Assert.NotNull(imported.Policy);
                Assert.Equal("1.0.0", Assert.Single(imported.Compatibility.Resolution.RestoreProvenance).Artifact.Inventory.PackageVersion);
                if (phase != "install") Assert.Equal("1.1.0", Assert.Single(state, value => value.PackageId == "example.install.authority").Version);
                if (phase is "denied" or "offline-denied")
                {
                    Assert.True(imported.Policy.IsBlocked);
                    Assert.Equal(PackagePolicyErrorCodes.SecurityBlocked, imported.Policy.ErrorCode);
                    await lifecycle.LoadInstalledPackagesAsync();
                    Assert.DoesNotContain(provider.GetRequiredService<PackageSessionState>().GetActivePackageSources(), value => value.PackageId == imported.PackageId);
                }
                else if (phase == "offline")
                {
                    var loaded = await lifecycle.LoadInstalledPackagesAsync(); Assert.True(loaded.Success, string.Join(" | ", loaded.Errors));
                }
                if (phase is "offline" or "offline-denied") Assert.Equal(0, network.Requests);
            }
            finally { await provider.GetRequiredService<RuntimeSessionOwner>().State.ClearActiveSessionAsync(); }
        }
        RuntimeRegistryPackageRequest Request(string id, string version) => new(input.RegistryOrigin, id, Version: version,
            DesiredTargets: [new("app", PackageTargetSelection.GetCurrentRuntimeIdentifier())]);
    }
    private sealed record Scenario(string PackageId, bool Optional, bool Required);
    private sealed record Input(string RegistryOrigin, string CertificateSha256, string RuntimeRoot, IReadOnlyList<Scenario> Matrices);
    private sealed class Factory(HttpClient http) : IHttpClientFactory { public HttpClient CreateClient(string name) => http; }
    private sealed class NetworkGate(HttpMessageHandler inner, bool offline) : DelegatingHandler(inner)
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.False(offline, "Restarted installed-package loading must consume retained policy without HTTP.");
            Requests++; return base.SendAsync(request, token);
        }
    }
}
