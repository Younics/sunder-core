using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.App.Tests;

public sealed partial class PackageViewHostServiceTests
{
    private static Task RunPackageTestAsync(string prefix, Func<TemporaryDirectoryScope, Task> test,
        bool untilProcessExit = false, object[]? arguments = null, [CallerMemberName] string scenario = "")
        => untilProcessExit
            ? RunPackageProcessTestAsync(prefix, test, arguments ?? [], scenario)
            : TemporaryDirectoryScope.RunAsync(prefix, async scope =>
        {
            var contexts = new ConcurrentDictionary<string, WeakReference>();
            void Loaded(object? sender, AssemblyLoadEventArgs args)
            {
                if (AssemblyLoadContext.GetLoadContext(args.LoadedAssembly) is AppPackageLoadContext context)
                    contexts.TryAdd(context.Name!, new WeakReference(context, trackResurrection: true));
            }
            AppDomain.CurrentDomain.AssemblyLoad += Loaded;
            try { await test(scope); }
            finally
            {
                AppDomain.CurrentDomain.AssemblyLoad -= Loaded;
                test = null!;
                // The scenario has returned and no longer roots its package views,
                // delegates or generations. Unload alone does not release CLR maps.
                await Task.Yield();
                for (var attempt = 0; attempt < 40 && contexts.Values.Any(context => context.IsAlive); attempt++)
                {
                    // .NET's reflection-JSON accessor cache expires idle entries
                    // only on subsequent metadata work, not on a timer or GC.
                    _ = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                        .GetTypeInfo(typeof(HostJsonProbe), new JsonSerializerOptions());
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    await Task.Delay(50);
                }
                Assert.All(contexts.Values, context => Assert.False(context.IsAlive, "A retired package context is still rooted."));
                await AppPackageFileReclamation.ReclaimAsync();
            }
        });

    private sealed class HostJsonProbe
    {
        public string Value { get; set; } = string.Empty;
    }

    private static async Task RunPackageProcessTestAsync(string prefix, Func<TemporaryDirectoryScope, Task> test,
        object[] arguments, string scenario)
    {
        const string childScenario = "SUNDER_APP_PACKAGE_TEST_SCENARIO";
        if (Environment.GetEnvironmentVariable(childScenario) == scenario)
        {
            // These scenarios intentionally retain package resources until exit.
            // The parent owns this temporary tree and deletes it after child exit.
            var parent = Environment.GetEnvironmentVariable("SUNDER_APP_PACKAGE_TEST_ROOT");
            Assert.Equal(Path.GetFullPath(parent!), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            await test(TemporaryDirectoryScope.Create(prefix));
            return;
        }

        await TemporaryDirectoryScope.RunAsync("sunder-app-package-process", async scope =>
        {
            var probe = Path.Combine(AppContext.BaseDirectory, "package-lifetime-probe", "Sunder.App.PackageLifetimeProbe.dll");
            Assert.True(File.Exists(probe), "Build the owning App test project before running package lifetime scenarios.");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { UseShellExecute = false };
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "Sunder.App.Tests.runtimeconfig.json"),
                         "--depsfile", Path.Combine(AppContext.BaseDirectory, "Sunder.App.Tests.deps.json"), probe, scenario, JsonSerializer.Serialize(arguments) })
                start.ArgumentList.Add(argument);
            start.Environment[childScenario] = scenario;
            start.Environment["SUNDER_APP_PACKAGE_TEST_ROOT"] = scope.RootPath;
            start.Environment["TEMP"] = scope.RootPath;
            start.Environment["TMP"] = scope.RootPath;
            start.Environment["TMPDIR"] = scope.RootPath;
            await using var process = CapturedProcess.Start(start);
            var result = await process.EnsureSuccessAsync(TimeSpan.FromSeconds(60));
            Assert.Contains("package-lifetime-scenario-passed:" + scenario, result.StandardOutput, StringComparison.Ordinal);
        });
    }
}
