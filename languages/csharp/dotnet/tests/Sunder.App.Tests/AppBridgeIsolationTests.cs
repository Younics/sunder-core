using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Controls;
using Sunder.App.Bridge;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppBridgeIsolationTests
{
    [Fact]
    public void OptionalHostAssembliesBypassPackageContextualReflection()
    {
        var expected = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName("Avalonia.Markup"));
        using var shared = new AppSharedAssemblyRegistry();
        var package = new RejectingLoadContext();
        try
        {
            using var reflection = package.EnterContextualReflection();
            var resolved = shared.ResolveSharedAssembly(expected.GetName());
            Assert.Equal(0, package.LoadAttempts);
            Assert.Same(expected, resolved);
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(resolved!));
        }
        finally { package.Unload(); }
    }

    private sealed class RejectingLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        internal int LoadAttempts;
        protected override Assembly? Load(AssemblyName name)
        {
            LoadAttempts++;
            // Bound a regression instead of allowing recursive resolution to
            // overflow the test host's stack as it does during real view creation.
            throw new InvalidOperationException("Host assembly resolution reentered the package context: " + name);
        }
    }

    [Fact]
    public void IncompatiblePackageSdksShareOnlyTheBridgeAndReleaseTheirContexts()
    {
        var references = ActivateTogether();
        for (var attempt = 0; attempt < 10 && references.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ActivateTogether()
    {
        using var shared = new AppSharedAssemblyRegistry();
        var fixtures = new[] { "one", "two" }.Select(name => Path.Combine(AppContext.BaseDirectory, "bridge-fixtures", name)).ToArray();
        var contexts = new List<AppPackageLoadContext>();
        var sdks = new List<Assembly>();
        try
        {
            foreach (var (folder, index) in fixtures.Select((folder, index) => (folder, index)))
            {
                WriteDeps(folder, index + 1);
                var context = new AppPackageLoadContext("test." + index, Path.Combine(folder, "Package.dll"), AppPackageTargetEnvironment.CurrentRid, shared, (_, _) => { });
                contexts.Add(context);
                var entry = (IAppPackageEntryPointV1)Activator.CreateInstance(context.LoadPackageEntryAssembly().GetType("EntryPoint")!)!;
                var sink = new Sink();
                var host = new ProbeHost(folder, "test." + index);
                var activation = entry.CreateAsync(host, sink).GetAwaiter().GetResult();
                var view = Assert.IsType<TextBlock>(sink.Factory!());
                Assert.Equal(index == 0 ? "one" : "2", view.Text);
                activation.Start(default);
                AppPackageViewNavigator.WarmupViewAsync(view, default).GetAwaiter().GetResult();
                AppPackageViewNavigator.NotifyViewNavigatedAsync(view, sink.Id!, new Dictionary<string, string?> { ["value"] = "isolated" }, default).GetAwaiter().GetResult();
                Assert.True(AppPackageViewNavigator.SupportsNavigationPreparation(view));
                Assert.Equal(AppPackageViewNavigator.NavigationPreparationStatus.Ready,
                    AppPackageViewNavigator.PrepareViewNavigationAsync(view, sink.Id!, null, default).GetAwaiter().GetResult());
                AppPackageViewNavigator.NotifyViewNavigationPresentedAsync(view, sink.Id!, null, default).GetAwaiter().GetResult();
                Assert.EndsWith(":warm:isolated:presented", view.Text);
                activation.DisposeAsync().GetAwaiter().GetResult();
                Assert.Equal(host.PackageId, File.ReadAllText(Path.Combine(folder, "started")));
                Assert.Equal(host.PackageId, File.ReadAllText(Path.Combine(folder, "disposed")));
                sdks.Add(Assert.Single(context.Assemblies, assembly => assembly.GetName().Name == "Sunder.Sdk"));
                Assert.DoesNotContain(context.Assemblies, assembly => assembly.GetName().Name == "Sunder.App.Bridge");
            }
            Assert.Equal(1, sdks[0].GetName().Version!.Major);
            Assert.Equal(2, sdks[1].GetName().Version!.Major);
            Assert.NotSame(sdks[0].GetType("IncompatibleSdk.PackageSdk"), sdks[1].GetType("IncompatibleSdk.PackageSdk"));
            return contexts.Select(context => new WeakReference(context)).ToArray();
        }
        finally { foreach (var context in contexts) context.Unload(); }
    }

    [Fact]
    public void MissingLocalSdkCannotFallBackToTheShellSdk()
    {
        using var shared = new AppSharedAssemblyRegistry();
        var folder = Path.Combine(Path.GetTempPath(), "sunder-missing-app-sdk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "bridge-fixtures", "one", "Package.dll"), Path.Combine(folder, "Package.dll"));
            WriteDeps(folder, 1);
            var context = new AppPackageLoadContext("test.missing", Path.Combine(folder, "Package.dll"), AppPackageTargetEnvironment.CurrentRid, shared, (_, _) => { });
            try
            {
                var entry = (IAppPackageEntryPointV1)Activator.CreateInstance(context.LoadPackageEntryAssembly().GetType("EntryPoint")!)!;
                Assert.Throws<FileLoadException>(() => entry.CreateAsync(new ProbeHost(folder, "test.missing"), new Sink()).GetAwaiter().GetResult());
            }
            finally { context.Unload(); }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static void WriteDeps(string folder, int major) => File.WriteAllText(Path.Combine(folder, "Package.deps.json"), """
        {"runtimeTarget":{"name":".NETCoreApp,Version=v10.0"},"targets":{".NETCoreApp,Version=v10.0":{
          "Package/1.0.0":{"dependencies":{"Sunder.Sdk":"SDK_VERSION"},"runtime":{"Package.dll":{}}},
          "Sunder.Sdk/SDK_VERSION":{"runtime":{"Sunder.Sdk.dll":{}}}}},"libraries":{
          "Package/1.0.0":{"type":"project","serviceable":false,"sha512":""},
          "Sunder.Sdk/SDK_VERSION":{"type":"project","serviceable":false,"sha512":""}}}
        """.Replace("SDK_VERSION", $"{major}.0.0", StringComparison.Ordinal));

    private sealed class Sink : IAppContributionSinkV1
    {
        internal Func<Control>? Factory;
        internal string? Id;
        public void RegisterView(AppViewRegistrationV1 registration, Func<Control> factory) { Id = registration.Id; Factory = factory; }
        public void RegisterSettingsView(Func<Control> factory) => throw new NotSupportedException();
    }
    private sealed class ProbeHost(string folder, string id) : AppBridgeServiceChannel, IAppPackageHostV1
    {
        public string PackageId => id;
        public string PackageVersion => "3.4.5";
        public string ContentRootPath => folder;
        public Task RunStateUpdateAsync(string key, Func<CancellationToken, CancellationToken, Task> update, CancellationToken token) => throw new NotSupportedException();
        public JsonElement QueueBackgroundTask(JsonElement request, Func<AppBackgroundTaskContextV1, Task> execute) => throw new NotSupportedException();
        public IDisposable SubscribeBackgroundTasks(Action<JsonElement> changed) => throw new NotSupportedException();
    }
}
