using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.Host.TestSupport;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.App.Integration.Tests;

public sealed partial class GeneratedAppProcessPolicyTests
{
    [WindowsFact]
    [Trait("Capability", "NativeUI")]
    [Trait("Capability", "Process")]
    public Task RuntimeInstalledDeveloperToolCapturesItsSdkOwnedNativeDevelopmentView()
        => TemporaryDirectoryScope.RunAsync("sunder-installed-development-capture", RunInstalledDevelopmentCaptureAsync);

    private async Task RunInstalledDevelopmentCaptureAsync(TemporaryDirectoryScope temporary)
    {
        using var policy = new ActivePolicyTestContext();
        policy.Clock.ReadOverride = new PolicyContinuousClock().Read;
        RuntimeLocalState.EnsureInitialized(policy.Paths.RootPath);
        await policy.RefreshAsync(packages: [new RegistryPackageVersionIdentity("example.generated", "7.6.5",
            PackageSessionPreparer.ComputeContentIdentity(packageFixture.PackagePath))]);

        var development = CanonicalPackageTestBuilder.WriteExplodedPackage(temporary.GetPath("development"),
            "test.development.capture", "1.0.0", typeof(GeneratedAppProcessPolicyTests).Assembly.Location,
            roles: ["app"], appTargetKind: "web", appEntryPoint: "index.html",
            appViews: [new() { ViewId = "test.development.capture.view", DisplayName = "Capture target", Route = "/", DefaultPlacement = "middle", ShowInHotbar = true }]);
        await File.WriteAllTextAsync(Path.Combine(development, "payload/shared/index.html"),
            "<!doctype html><html><body style='margin:0;background:rgb(10,180,30)'><button aria-label='Capture target' style='position:absolute;left:40px;top:30px;width:100px;height:80px;border:0;border-radius:0;padding:0;background:rgb(20,40,220)'></button></body></html>");
        CanonicalPackageTestBuilder.WriteContentIndex(development);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(development);
        Assert.True(validation.Success, string.Join(" | ", validation.Errors));
        var sourceIdentity = PackageSessionPreparer.ComputeContentIdentity(development);

        var endpoint = new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "sunder-development-capture-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateSlimBuilder();
        RuntimeSupervisedTransport.Configure(builder.WebHost, endpoint);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddRuntimeHostServices(policy.Paths, new RuntimeBearerTokenValidator("development-capture-token"));
        builder.Services.UseTestExecutionRuntimeLaunchResolver();
        builder.Services.AddSingleton(policy.Cache);
        builder.Services.AddSingleton(policy.Client);
        builder.Services.AddSingleton<IPolicyContinuousClock>(policy.Clock);
        await using var app = builder.Build();
        app.UseMiddleware<RuntimeProblemDetailsMiddleware>();
        app.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();
        app.MapRuntimeHandshakeEndpoint();
        var api = app.MapGroup("/api/v1");
        api.MapInstalledPackageEndpoints().MapContentTransferEndpoints().MapPackageSessionEndpoints()
            .MapRuntimeRpcEndpoints().MapAppPolicyEndpoints().MapDevPackageOwnerEndpoints().MapRuntimeStreamEndpoints();
        // The existing package-only .NET launcher fixture omits installer prerequisites.
        // Expose the real preview service for the Web-only development source, without
        // enabling any software installation/update endpoints or changing that fixture.
        var localRuntimes = ActivatorUtilities.CreateInstance<LocalExecutionRuntimeBindingResolver>(app.Services);
        var prerequisites = ActivatorUtilities.CreateInstance<PackageRuntimePrerequisiteService>(app.Services, localRuntimes);
        api.MapPost("/packages/runtime-prerequisites", async (RuntimePackageRuntimePrerequisiteRequest request, CancellationToken token)
            => Results.Ok(await prerequisites.PreviewAsync(request, token)));
        var lifecycle = app.Services.GetRequiredService<InstalledPackageLifecycleService>();
        await lifecycle.InitializeAsync();
        await RuntimeHostBootstrapRunner.StartAsync(app, app.Services.GetRequiredService<RuntimeSessionOwner>(),
            lifecycle.LoadInstalledPackagesAsync);
        var gateway = new RuntimeConnectionInfo(new Uri(Assert.Single(app.Urls,
            address => address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))), "development-capture-token");
        var worker = new RuntimeConnectionInfo(RuntimeIpcEndpoint.LogicalRuntimeUrl, "development-capture-token") { IpcEndpoint = endpoint };
        using var transport = new RuntimeClientTransport(() => gateway);
        using var client = new RuntimeManagementClient(transport);
        using var shellClient = new Sunder.App.Services.RuntimeApiClient(transport);
        try
        {
            var archive = temporary.GetPath("developer-tool.sunderpkg");
            ZipFile.CreateFromDirectory(packageFixture.PackagePath, archive);
            var upload = await client.UploadPackageAsync(archive);
            var stage = await client.StagePackageStoreChangesAsync(new([new(PackageStoreMutationKind.Install, UploadId: upload.UploadId)]));
            Assert.True(stage.Success, stage.OperationResult.Message);
            var commit = await client.CommitPackageStoreStageAsync(stage.StageId!, stage.OperationResult);
            Assert.True(commit.Success, string.Join(" | ", commit.Errors));
            Assert.Equal("example.generated", Assert.Single(await client.GetInstalledPackagesAsync()).PackageId);

            var gatewayPath = temporary.GetPath("gateway.json");
            var workerPath = temporary.GetPath("worker.json");
            RuntimeConnectionInfoStore.Save(gateway, gatewayPath);
            RuntimeConnectionInfoStore.Save(worker, workerPath);
            var output = temporary.CreateDirectory("capture");
            var config = temporary.GetPath("capture.json");
            await File.WriteAllBytesAsync(config, JsonSerializer.SerializeToUtf8Bytes(new
            {
                gatewayPath, workerPath, development, output,
            }));
            var assembly = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sunder-policy-app-target-path.txt")).Trim();
            var executable = Path.Combine(Path.GetDirectoryName(assembly)!, "Sunder.App.exe");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
            start.Environment["DOTNET_STARTUP_HOOKS"] = Path.Combine(AppContext.BaseDirectory, "development-capture-hook", "Sunder.App.Tests.dll");
            start.Environment["SUNDER_TEST_DEVELOPMENT_CAPTURE_CONFIG"] = config;
            start.Environment["SUNDER_APP_STATE_ROOT"] = temporary.GetPath("app-state");
            start.Environment[RuntimeLocalState.StateRootEnvironmentVariable] = policy.Paths.RootPath;
            start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
            await using (var process = CapturedProcess.Start(start, 128 * 1024))
            {
                var result = await WindowsWebViewBrowserProcess.EnsureSuccessAndExitAsync(process, TimeSpan.FromSeconds(120));
                Assert.Contains("installed-consumer-capture: native-shell; runtime-installed; sdk-owned; package-only; selected-crop; retired-session-rejected", result.StandardOutput, StringComparison.Ordinal);
            }
            var observed = await File.ReadAllLinesAsync(Path.Combine(output, "consumer.txt"));
            Assert.Equal("test.development.capture", observed[0]);
            Assert.Equal(sourceIdentity, observed[1]);
            Assert.Contains("retired-session-rejected", observed[2], StringComparison.Ordinal);
            Assert.Equal("example.generated", Assert.Single((await shellClient.GetRuntimePackageSnapshotAsync()).ActivePackages).PackageId);
            Assert.Equal("example.generated", Assert.Single(await client.GetInstalledPackagesAsync()).PackageId);
        }
        finally
        {
            // The capture child has exited before its exact owned App/profile tree
            // is reclaimed. No production logs or unrelated profiles are removed.
            await lifecycle.ShutdownAsync(app.Services.GetRequiredService<PackageSessionLifecycleService>(), default);
            await app.StopAsync();
        }
    }

}
