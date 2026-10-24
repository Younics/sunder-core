using System.Text.Json;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class AgentPackageReleaseSmokeTests
{
    // A real generated Maker Worker in the real Host, without any Agent package,
    // test tool provider, model call, page resolution or live user state.
    [ExternalArtifactFact("SUNDER_REQUIRE_MAKER_STANDALONE", "SUNDER_MAKER_DEV_PACKAGE", "SUNDER_MAKER_IMPORT_CANDIDATE", "SUNDER_MAKER_PLATFORM_CATALOG")]
    [Trait("Category", "ExternalArtifact")]
    public async Task MakerStandalone_CurrentDevelopmentOutputActivatesAndRejectsInvalidArgumentsWithoutAgent()
    {
        const string makerId = "younics.sunder.package.maker";
        var folder = Path.GetFullPath(Environment.GetEnvironmentVariable("SUNDER_MAKER_DEV_PACKAGE")!);
        using var root = TemporaryDirectoryScope.Create("maker-standalone-host");
        var paths = new RuntimePackagePaths(root.GetPath("runtime"));
        var logger = new ReleaseSmokeLogger();
        var policy = new PackageLifecyclePolicyCache(paths, new MutablePolicyClock());
        var imports = new DevelopmentImportAuthorityStore(paths, policy);
        // Local API evidence is required even when the corresponding package is
        // optional and absent. No Registry client or fallback is available here.
        var preparation = new DevelopmentImportAuthorityPreparation(null!, imports, null!, policy, TimeProvider.System);
        var host = new RuntimePackageSessionTestHost(logger, new InstalledPackageStore(paths), new SunderPackageArchiveInstaller(paths),
            developmentImports: imports);
        var observedProcesses = new HashSet<int>();
        try
        {
            await host.InitializeAsync();
            await preparation.PrepareAsync(new(folder)
            {
                CandidateArchives = [Environment.GetEnvironmentVariable("SUNDER_MAKER_IMPORT_CANDIDATE")!],
                PlatformCatalogPath = Environment.GetEnvironmentVariable("SUNDER_MAKER_PLATFORM_CATALOG"),
            }, default);
            var loaded = await host.LoadStartupDevPackagesAsync([folder]);
            Assert.True(loaded.Success, logger.Describe("Load Maker without Agent", loaded.Errors));
            Assert.Empty(await host.GetInstalledPackagesAsync());
            var package = Assert.Single(host.GetSessionPackages());
            Assert.Equal(makerId, package.PackageId);
            Assert.True(package.IsEnabled);
            Assert.Equal(PackageReadinessState.Ready, package.Readiness);
            Assert.Equal(makerId, Assert.Single(host.GetActivePackageUiSnapshots()).PackageId);
            observedProcesses.UnionWith(GetWorkerProcessIds(host));
            Assert.Single(observedProcesses);

            var providers = host.RpcCatalog.GetSnapshot().Providers;
            Assert.Equal(new[] { "builder.runtime", "maker.permissions", "maker.tools" }, providers.Select(provider => provider.ProviderId).Order(StringComparer.Ordinal));
            Assert.All(providers, provider =>
            {
                Assert.Equal(makerId, provider.PackageId);
                Assert.Equal(SunderRpcProviderState.Active, provider.State);
            });
            var activation = host.ActiveSession.LoadedPackageMap[makerId];
            Assert.True(activation.Source.DevelopmentImports!.IsLocal);
            Assert.Null(activation.Source.DevelopmentImports.RegistryOrigin);
            var rpc = new RuntimeRpcClient(host.RpcBroker, new(makerId, activation.RuntimeActivationId));
            var denied = await Assert.ThrowsAsync<SunderRpcException>(() => rpc.DiscoverAsync("younics.ai.agent.tool.source").AsTask());
            Assert.Equal(SunderRpcErrorKind.PermissionDenied, denied.Error.Kind);
            // An absent optional owner grants no new caller authority. Exercise
            // the actual process handler directly at this provider-side boundary.
            var tools = Assert.Single(providers, provider => provider.ProviderId == "maker.tools");
            var handler = Assert.Single(activation.RpcProviders.Values, provider => provider.ProviderId == tools.ProviderId).Handler;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var invocation = new SunderRpcInvocationContext(makerId, package.Version, tools, DateTimeOffset.UtcNow.AddSeconds(5), 1, deadline.Token);
            var description = await handler.InvokeUnaryAsync(invocation, "tool-source", "describe", JsonSerializer.SerializeToElement(new { }), deadline.Token);
            Assert.Equal("package-maker", description.GetProperty("sourceId").GetString());
            var rejected = await handler.InvokeUnaryAsync(invocation, "tool-source", "validate-arguments", JsonSerializer.SerializeToElement(new
            {
                toolId = "maker_build_package",
                argumentsJson = JsonSerializer.Serialize(new { entryPoint = root.GetPath("project/app/index.html") }),
            }), deadline.Token);
            Assert.True(rejected.GetProperty("isError").GetBoolean());
            Assert.Equal("maker.invalid-request", rejected.GetProperty("errorCode").GetString());
            Assert.Equal(tools.Endpoint, Assert.Single(host.RpcCatalog.GetSnapshot().Providers, provider => provider.ProviderId == tools.ProviderId).Endpoint);
            Assert.False(activation.Worker.GenerationCompletion.IsCompleted);
            Assert.Single(host.GetSessionPackages());
        }
        finally
        {
            observedProcesses.UnionWith(GetWorkerProcessIds(host));
            await host.ShutdownAsync();
            await AssertProcessesStoppedAsync(observedProcesses);
        }
    }
}
