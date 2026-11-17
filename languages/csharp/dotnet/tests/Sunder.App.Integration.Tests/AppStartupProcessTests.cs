using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host;
using Sunder.Runtime.Host.Endpoints;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.Host.TestSupport;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.App.Integration.Tests;

[Collection("App/Runtime integration")]
public sealed class AppStartupProcessTests
{
    [Fact]
    public void ExecutableEntryPointIsSynchronousStaNotAnAsyncMainWrapper()
    {
        // Inspect the executable entry point, not the authored method: [STAThread]
        // on Task Main is not applied to the compiler-generated CLR entry point.
        var entry = typeof(Program).Assembly.EntryPoint;
        Assert.NotNull(entry);
        Assert.NotNull(entry.GetCustomAttribute<STAThreadAttribute>());
        Assert.Null(entry.GetCustomAttribute<AsyncStateMachineAttribute>());
        Assert.Contains(entry.ReturnType, new[] { typeof(void), typeof(int) });
    }

    [WindowsFact]
    [Trait("Capability", "NativeUI")]
    [Trait("Capability", "Process")]
    public Task NormalEntryPointOpensInteractsWithAndReopensSupervisedDevelopmentWebView()
        => TemporaryDirectoryScope.RunAsync("sunder-app", RunNormalStartupAsync);

    private static async Task RunNormalStartupAsync(TemporaryDirectoryScope temporary)
    {
        var paths = new RuntimePackagePaths(temporary.GetPath("runtime"));
        RuntimeLocalState.EnsureInitialized(paths.RootPath);
        var package = CanonicalPackageTestBuilder.WriteExplodedPackage(temporary.GetPath("development"),
            "test.startup.webview", "1.0.0", typeof(AppStartupProcessTests).Assembly.Location,
            roles: ["app"], appTargetKind: "web", appEntryPoint: "index.html",
            appViews: [new() { ViewId = "test.startup.webview.view", DisplayName = "Startup web view", Route = "/", DefaultPlacement = "middle", ShowInHotbar = true }]);
        await File.WriteAllTextAsync(Path.Combine(package, "payload/shared/index.html"),
            "<!doctype html><html><body><button id='press'>0</button><script src='script.js'></script></body></html>");
        await File.WriteAllTextAsync(Path.Combine(package, "payload/shared/script.js"),
            "const button = document.getElementById('press'); button.addEventListener('click', () => button.textContent = Number(button.textContent) + 1); globalThis.startupReady = true;");
        CanonicalPackageTestBuilder.WriteContentIndex(package);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(package);
        Assert.True(validation.Success, string.Join(" | ", validation.Errors));

        var endpoint = new RuntimeIpcEndpoint(RuntimeIpcTransportKind.NamedPipe, "sunder-app-startup-" + Guid.NewGuid().ToString("N"));
        var token = Guid.NewGuid().ToString("N");
        var builder = WebApplication.CreateSlimBuilder();
        RuntimeSupervisedTransport.Configure(builder.WebHost, endpoint);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddRuntimeHostServices(paths, new RuntimeBearerTokenValidator(token));
        await using var runtime = builder.Build();
        runtime.UseMiddleware<RuntimeProblemDetailsMiddleware>();
        runtime.UseMiddleware<RuntimeBearerAuthenticationMiddleware>();
        runtime.MapRuntimeHandshakeEndpoint();
        runtime.MapGroup("/api/v1").MapSystemEndpoints(DateTimeOffset.UtcNow)
            .MapPackageSessionEndpoints().MapContentTransferEndpoints().MapRuntimeRpcEndpoints()
            .MapAppPolicyEndpoints().MapDevPackageOwnerEndpoints().MapInstalledPackageEndpoints().MapRuntimeStreamEndpoints();
        var lifecycle = runtime.Services.GetRequiredService<InstalledPackageLifecycleService>();
        await lifecycle.InitializeAsync();
        await RuntimeHostBootstrapRunner.StartAsync(runtime, runtime.Services.GetRequiredService<RuntimeSessionOwner>(), lifecycle.LoadInstalledPackagesAsync);
        RuntimeSupervisedTransport.RestrictEndpoint(endpoint);
        try
        {
            var workerPath = temporary.GetPath("worker.json");
            RuntimeConnectionInfoStore.Save(new(RuntimeIpcEndpoint.LogicalRuntimeUrl, token) { IpcEndpoint = endpoint }, workerPath);
            var gateway = new RuntimeConnectionInfo(new Uri(Assert.Single(runtime.Urls,
                address => address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))), token)
            { WorkerConnectionInfoPath = workerPath };
            RuntimeConnectionInfoStore.Save(gateway, paths.ConnectionInfoFilePath);

            var image = CopyAppWithoutInstallers(temporary);
            var start = new ProcessStartInfo(Path.Combine(image, "Sunder.App.exe")) { UseShellExecute = false, WorkingDirectory = image };
            foreach (var argument in new[] { "--runtime-url", gateway.RuntimeUrl.AbsoluteUri,
                "--runtime-host-path", temporary.GetPath("external-runtime-owned-by-test"), "--dev-package", package })
                start.ArgumentList.Add(argument);
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("SUNDER_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("COREHOST_", StringComparison.OrdinalIgnoreCase)
                || key.StartsWith("DOTNET_STARTUP_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            start.Environment[AppLocalState.StateRootEnvironmentVariable] = temporary.GetPath("app-state");
            start.Environment[RuntimeLocalState.StateRootEnvironmentVariable] = paths.RootPath;
            start.Environment["SUNDER_HOST_STATE_ROOT"] = temporary.GetPath("host-state");
            start.Environment["DOTNET_STARTUP_HOOKS"] = Path.Combine(AppContext.BaseDirectory, "startup-webview-observer", "Sunder.App.Tests.dll");
            start.Environment["SUNDER_TEST_STARTUP_WEBVIEW"] = "1";
            start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
            await using var process = CapturedProcess.Start(start, 256 * 1024);
            var result = await WindowsWebViewBrowserProcess.EnsureSuccessAndExitAsync(process, TimeSpan.FromSeconds(60), async () =>
            {
                var readyDeadline = Stopwatch.StartNew();
                while (!process.StandardOutput.Contains("startup-webview: ready-for-forwarding", StringComparison.Ordinal))
                {
                    Assert.False(process.HasExited, process.Diagnostics);
                    Assert.True(readyDeadline.Elapsed < TimeSpan.FromSeconds(90), process.Diagnostics);
                    await Task.Delay(25);
                }

                // A second normal executable must forward, not create another desktop.
                var secondary = new ProcessStartInfo(start.FileName) { UseShellExecute = false, WorkingDirectory = image };
                secondary.Environment.Clear();
                foreach (var pair in start.Environment) secondary.Environment[pair.Key] = pair.Value;
                secondary.Environment.Remove("SUNDER_TEST_STARTUP_WEBVIEW");
                secondary.Environment.Remove("DOTNET_STARTUP_HOOKS");
                secondary.ArgumentList.Add("sunder://stacks/sunder.stack.startup-probe");
                await using var forwarded = CapturedProcess.Start(secondary, 128 * 1024);
                await forwarded.EnsureSuccessAsync(TimeSpan.FromSeconds(15));
            });
            Assert.Contains("startup-webview: original-main-thread; STA; native-package-page; interaction; reopen; forwarded-launch", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("move-preserved; reload-retired; rapid-reopen-fenced; hotbar-removal-retired", result.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("RPC_E_CHANGED_MODE", result.CombinedOutput, StringComparison.Ordinal);
            // Keep file-only native cleanup phases in the original TRX before
            // the test-owned home is reclaimed, including a failed verdict.
            foreach (var log in Directory.EnumerateFiles(temporary.GetPath("app-state", "logs"), "*.log"))
                Console.WriteLine(await File.ReadAllTextAsync(log));
            Assert.DoesNotContain("Cleanup exceeded its 3 second budget while draining shared package browser infrastructure", result.CombinedOutput, StringComparison.Ordinal);
            Assert.Empty(runtime.Services.GetRequiredService<PackageSessionState>().GetActivePackageSources());
            Assert.Empty(runtime.Services.GetRequiredService<AppProcessPolicySessions>().CapturePolicySubjects());
            var profileRoot = temporary.GetPath("app-state", "webview-data");
            Assert.True(!Directory.Exists(profileRoot) || !Directory.EnumerateFileSystemEntries(profileRoot).Any(),
                "Retired native browser profile data survived ordinary App shutdown.");
            var quarantineRoot = temporary.GetPath("app-state", "webview-quarantine");
            Assert.True(!Directory.Exists(quarantineRoot) || !Directory.EnumerateFileSystemEntries(quarantineRoot).Any(),
                "Ordinary browser retirement required leftover profile quarantine.");
            var engineRoot = temporary.GetPath("app-state", "webview-engines");
            Assert.True(!Directory.Exists(engineRoot) || !Directory.EnumerateFileSystemEntries(engineRoot).Any(),
                "Ordinary App shutdown retained the shared browser's user-data root.");
        }
        finally
        {
            await lifecycle.ShutdownAsync(runtime.Services.GetRequiredService<PackageSessionLifecycleService>(), default);
            await runtime.StopAsync();
        }
    }

    private static string CopyAppWithoutInstallers(TemporaryDirectoryScope temporary)
    {
        var target = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sunder-policy-app-target-path.txt")).Trim();
        var source = Path.GetDirectoryName(target)!;
        var image = temporary.CreateDirectory("app-image");
        // This gate owns startup/COM and package-view behavior, not persistent CLI
        // installation or Host service deployment. Omitting their payloads prevents
        // ordinary startup from modifying the current user's PATH or service files.
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, path);
            var first = relative.Split(Path.DirectorySeparatorChar)[0];
            if (first is "Cli" or "RuntimeHost") continue;
            var destination = Path.Combine(image, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }
        Assert.True(File.Exists(Path.Combine(image, "Sunder.App.exe")));
        return image;
    }
}
