using System.Text.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

[Collection(RuntimeProcessIntegrationCollection.Name)]
public sealed class GeneratedTemplateRuntimeHostSmokeTests(
    OfficialRuntimeFixture executionRuntimes)
    : IClassFixture<OfficialRuntimeFixture>
{
    [ExternalArtifactFact(
        "SUNDER_REQUIRE_NODE_TEMPLATE_HOST_SMOKE",
        "SUNDER_NODE_TEMPLATE_DEV_PATH")]
    [Trait("Category", "ExternalArtifact")]
    public async Task GeneratedNodeTemplate_ActivatesThroughOfficialRuntimeHost()
    {
        var node = Node24Availability.Current;
        Assert.True(node.IsAvailable, node.UnavailableReason);
        var receipt = await executionRuntimes.EnsureNodeAsync("24.21.0");

        await RunSmokeAsync(
            RequiredDirectory("SUNDER_NODE_TEMPLATE_DEV_PATH"),
            receipt,
            "node-template");
    }

    [ExternalArtifactFact(
        "SUNDER_REQUIRE_DOTNET_TEMPLATE_HOST_SMOKE",
        "SUNDER_DOTNET_TEMPLATE_DEV_PATH")]
    [Trait("Category", "ExternalArtifact")]
    public async Task GeneratedDotNetTemplate_ActivatesThroughOfficialRuntimeHost()
    {
        var receipt = await executionRuntimes.EnsureDotNetAsync("10.0.12");

        await RunSmokeAsync(
            RequiredDirectory("SUNDER_DOTNET_TEMPLATE_DEV_PATH"),
            receipt,
            "dotnet-template");
    }

    private async Task RunSmokeAsync(
        string packagePath,
        ExecutionRuntimeReceiptV1 receipt,
        string runtime)
    {
        CurrentRidRuntimeHostTestSupport.AssertPackageContainsNoRuntime(packagePath);
        var root = CurrentRidRuntimeHostTestSupport.CreateRoot(runtime);
        var paths = new RuntimePackagePaths(Path.Combine(root, "runtime-host"));
        var installedStore = new InstalledPackageStore(paths);
        var transfers = new RuntimeContentTransferStore(paths);
        var host = CurrentRidRuntimeHostTestSupport.CreateHost(
            paths,
            installedStore,
            transfers,
            executionRuntimes);
        var initialized = false;
        int[] processIds = [];
        try
        {
            await host.InitializeAsync();
            initialized = true;
            var load = await host.LoadStartupDevPackagesAsync([packagePath]);
            Assert.True(load.Success, string.Join(Environment.NewLine, load.Errors));
            var package = Assert.Single(host.ActiveSession.LoadedPackageMap.Values);
            CurrentRidRuntimeHostTestSupport.AssertRuntimeLaunch(
                package,
                receipt,
                executionRuntimes);
            processIds = CurrentRidRuntimeHostTestSupport.GetActiveProcessIds(host);
            Assert.Single(processIds);
            var provider = Assert.Single(host.RpcCatalog.GetSnapshot().Providers);
            var registration = Assert.Single(package.RpcProviders.Values);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var context = new SunderRpcInvocationContext(provider.PackageId, provider.PackageVersion,
                provider, DateTimeOffset.UtcNow.AddSeconds(30), 1, deadline.Token);
            if (runtime == "dotnet-template")
            {
                Assert.DoesNotContain(Directory.EnumerateFiles(packagePath, "*.dll", SearchOption.AllDirectories),
                    path => Path.GetFileName(path).Contains(".Protocol", StringComparison.Ordinal));
                Assert.Equal(package.Source.PackageId + ".sample", provider.ContractId);
                Assert.Equal(package.Source.Manifest!.Version, provider.ContractVersion);
                var response = await registration.Handler.InvokeUnaryAsync(context, "sample", "greet",
                    JsonSerializer.SerializeToElement("source template"), deadline.Token);
                Assert.Equal("Hello, source template!", response.GetString());
                // This probes the process registration directly; the public broker
                // normally rejects invalid input before dispatching to the Worker.
                await Assert.ThrowsAsync<ProcessRpcProviderFaultException>(async () => await registration.Handler.InvokeUnaryAsync(
                    context, "sample", "greet", JsonSerializer.SerializeToElement(42), deadline.Token));
            }
            else
            {
                Assert.Equal(package.Source.PackageId + ".messages", provider.ContractId);
                Assert.Equal("1.0.0", provider.ContractVersion);
                var request = JsonSerializer.SerializeToElement(new { message = "managed runtime" });
                var response = await registration.Handler.InvokeUnaryAsync(context, "messages", "send", request, deadline.Token);
                Assert.Equal($"Hello from {package.Source.Manifest!.Name}: managed runtime", response.GetProperty("message").GetString());
                var events = new List<string?>();
                await foreach (var item in registration.Handler.InvokeServerStreamAsync(context, "messages", "watch", request, deadline.Token))
                    events.Add(item.GetProperty("message").GetString());
                Assert.Equal(["Watching managed runtime"], events);
                await Assert.ThrowsAsync<ProcessRpcProviderFaultException>(async () => await registration.Handler.InvokeUnaryAsync(
                    context, "messages", "send", JsonSerializer.SerializeToElement(new { message = 42 }), deadline.Token));
            }
        }
        finally
        {
            if (initialized)
            {
                await host.ShutdownAsync();
                await CurrentRidRuntimeHostTestSupport.AssertProcessesStoppedAsync(processIds);
            }
            transfers.Dispose();
            CurrentRidRuntimeHostTestSupport.DeleteDirectory(root);
        }
    }

    private static string RequiredDirectory(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        Assert.False(string.IsNullOrWhiteSpace(value), $"{variable} is required by the selected artifact lane.");
        var path = Path.GetFullPath(value!);
        Assert.True(Directory.Exists(path), $"Generated template development package does not exist: {path}");
        return path;
    }
}
