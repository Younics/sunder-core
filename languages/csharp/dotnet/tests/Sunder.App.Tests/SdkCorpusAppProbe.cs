// Compiled as a separate test-only executable named Sunder.App.Tests, matching the
// existing friend-assembly boundary. It runs against provided App assemblies only.
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
global using Sunder.Runtime.LocalState;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Controls;
using Sunder.App.Services;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;

namespace Sunder.App.Tests;

internal static class SdkCorpusAppProbe
{
    private static string _appRoot = null!;
    public static async Task<int> Main(string[] arguments)
    {
        try
        {
            if (arguments.Length != 4) throw new InvalidDataException("Expected exact App root, archive, work and challenge.");
            _appRoot = Path.GetFullPath(arguments[0]);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                var path = Path.Combine(_appRoot, name.Name + ".dll");
                return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path)
                    : throw new FileNotFoundException("Provided App dependency is missing: " + name.Name);
            };
            await ExecuteAsync(arguments[1], arguments[2], arguments[3]);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ExecuteAsync(string archive, string work, string nonce)
    {
        var sdk = typeof(IPackageContext).Assembly;
        var avaloniaSdk = typeof(IAvaloniaPackageContributionRegistry).Assembly;
        foreach (var assembly in new[] { typeof(PackageViewHostService).Assembly, sdk, avaloniaSdk, typeof(Control).Assembly })
            Check(Path.GetDirectoryName(assembly.Location) == _appRoot, "Current test output substituted for provided App/SDK: " + assembly.Location);
        var extracted = Path.Combine(work, "package");
        var inspected = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, extracted);
        Check(inspected.Success && inspected.Manifest!.Id == "compatibility.app.probe", "Invalid retained App probe package.");
        var manifest = inspected.Manifest!;
        var key = new SunderPackageTargetKey("app", RuntimeInformation.RuntimeIdentifier);
        var projection = Path.Combine(work, "projection");
        Directory.CreateDirectory(Path.Combine(projection, "manifest"));
        File.Copy(Path.Combine(extracted, SunderPackageFormat.ManifestPath), Path.Combine(projection, SunderPackageFormat.ManifestPath));
        foreach (var file in SunderPackageTargetResolver.CreateProjectionPlan(manifest, inspected.ContentIndex!, key).Files)
        {
            var output = file.LogicalPath.ToPlatformPath(projection);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(file.PhysicalPath.ToPlatformPath(extracted), output);
        }
        var snapshot = RuntimeContractTestData.Snapshot(manifest.Id!, PackageSourceKind.Dev, projection);
        var package = new ActivePackageDescriptor(manifest.Id!, manifest.Name!, manifest.Version!, PackageHostRoles.App, null,
            true, PackageReadinessState.Ready, [new("compatibility.app.probe", manifest.Id!, "Probe", null, "middle")]);
        await using var host = TestAppPackageHost.Create(new AppPackageViewRegistry(), [], [], [], Path.Combine(work, "session"),
            downloadPackageUiSnapshotAsync: RuntimeContractTestData.DownloadSnapshotAsync, uiDispatcher: new ImmediateUiDispatcher());
        await host.ApplyPackageDeltaAsync([package], [snapshot]);
        Check(host.LoadedPackageCount == 1 && host.LoadContextCount == 1, "Provided App did not activate the retained module.");
        var view = host.GetOrCreateView("compatibility.app.probe") ?? throw new InvalidDataException("Retained view missing.");
        Check(view.GetType().FullName == "Compatibility.App.ProbeView", "Unexpected retained App view.");
        Check(AssemblyLoadContext.GetLoadContext(view.GetType().Assembly) != AssemblyLoadContext.Default, "Package did not use its isolated App ALC.");
        Check(ReferenceEquals(view.GetType().GetProperty("SdkAssembly")!.GetValue(view), sdk), "Package SDK was not substituted with provided App SDK.");
        Check(ReferenceEquals(view.GetType().GetProperty("AvaloniaSdkAssembly")!.GetValue(view), avaloniaSdk), "Avalonia SDK identity substitution failed.");
        foreach (var requested in view.GetType().Assembly.GetReferencedAssemblies().Where(x => x.Name is "Sunder.Sdk" or "Sunder.Sdk.Avalonia"))
        {
            using var registry = new AppSharedAssemblyRegistry([]);
            Check(ReferenceEquals(registry.ResolveSharedAssembly(requested), requested.Name == "Sunder.Sdk" ? sdk : avaloniaSdk), "Retained assembly reference is not compatible.");
        }
        Check(((ContentControl)view).Content?.ToString() == "retained-app-probe:compatibility.app.probe", "Retained view/DI behavior changed.");
        Check((string?)view.GetType().GetMethod("Echo")!.Invoke(view, [nonce]) == "app:" + nonce, "Retained view behavior did not execute.");
        await host.DisposeAsync();
        Check((bool)view.GetType().GetProperty("IsDisposed")!.GetValue(view)!, "Retained view was not disposed.");
        Console.WriteLine(JsonSerializer.Serialize(new { nonce, sdk = sdk.Location, avaloniaSdk = avaloniaSdk.Location,
            app = typeof(PackageViewHostService).Assembly.Location, packageId = manifest.Id, packageVersion = manifest.Version,
            rid = RuntimeInformation.RuntimeIdentifier, disposed = true, isolatedPackage = true }));
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private sealed class ImmediateUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
