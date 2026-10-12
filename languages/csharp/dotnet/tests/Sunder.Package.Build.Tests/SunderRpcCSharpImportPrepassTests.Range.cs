using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InjectedRoleClientsUseReviewedRangeAndExactTupleWithoutRestoreVersionAuthority(bool app, bool prerelease)
    {
        var artifact = _lock.PackageArtifacts["example.dependency"];
        var selected = _lock with
        {
            PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact>
            {
                [artifact.PackageId] = SeedDescriptorOnlyArtifact(artifact with { RequestedVersionRange = ">=2.0.0 <3.0.0", IncludePrerelease = prerelease, SelectedPackageVersion = "2.0.0" }),
            },
            PackageImports = new Dictionary<string, SunderRpcLockedPackageImport>
            {
                [artifact.PackageId] = _lock.PackageImports[artifact.PackageId] with { ConsumerRoles = ["app", "runtime"] },
            },
        };
        var imports = SunderRpcCSharpImportPrepass.Prepare(selected, _root);
        // Changing only restoration provenance must not change executable selection policy.
        var otherRestore = selected with { PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact>
        {
            [artifact.PackageId] = SeedDescriptorOnlyArtifact(selected.PackageArtifacts[artifact.PackageId] with { SelectedPackageVersion = "2.4.0", SelectedPackageContentIdentity = new string('f', 64) }),
        } };
        Assert.Equal(imports.Sources, SunderRpcCSharpImportPrepass.Prepare(otherRestore, _root).Sources);
        const string source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Packaging;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Dependency_Services;
            namespace RangeConsumer;
            [SunderService]
            public sealed partial class ConsumerService(IPackageApi api, IServiceProviders<IExampleService> providers)
            {
                public object Catalog => providers;
                public async Task<bool> RunAsync() { await api.Example.PingAsync(); return true; }
            }
            [SunderPackageView("range.consumer", "Range", ViewModel = typeof(PageModel))]
            public sealed class Page : Avalonia.Controls.UserControl;
            public sealed class PageModel(IPackageApi api, IServiceProviders<IExampleService> providers)
            { public object Catalog => providers; public ValueTask RunAsync() => api.Example.PingAsync(); }
            """;
        var references = References().Concat(new[] { typeof(SunderWorker).Assembly.Location,
            typeof(Sunder.Sdk.Avalonia.IAvaloniaPackageContributionRegistry).Assembly.Location,
            typeof(Avalonia.Controls.Control).Assembly.Location, typeof(Avalonia.AvaloniaObject).Assembly.Location,
            typeof(ServiceCollectionContainerBuilderExtensions).Assembly.Location }).Distinct(StringComparer.Ordinal).ToArray();
        var analysis = SunderPackageAuthoringCompiler.Analyze(new("example.range.consumer", "1.0.0", [new("Consumer.cs", source)], references,
            SupplementalSources: imports.Sources, ImportedPackageApis: imports.PackageApis));
        Assert.True(analysis.Success, string.Join(Environment.NewLine, analysis.Compilation.Diagnostics.Concat(analysis.RolePlan.Diagnostics)));
        var role = app ? SunderPackageRoleMembership.App : SunderPackageRoleMembership.Worker;
        var files = SunderPackageRoleProjection.Project(analysis, role).Concat(imports.Sources)
            .Concat(analysis.GeneratedSources.Where(file => !app || file.Path != "authoring.g.cs"))
            .Append(app ? SunderPackageCompositionEmitter.EmitApp(analysis) : SunderPackageCompositionEmitter.EmitWorker(analysis));
        if (app) files = files.Concat(SunderRpcCSharpServiceApiEmitter.EmitAppClients(analysis.Compilation.Contracts, "example.range.consumer", "1.0.0"));
        else files = files.Concat(SunderPackageCompositionEmitter.EmitWorkerClients(analysis, "example.range.consumer", "1.0.0"));
        var compilation = CSharpCompilation.Create("RangeRole_" + Guid.NewGuid().ToString("N"),
            files.Select(file => CSharpSyntaxTree.ParseText(file.Text, new CSharpParseOptions(LanguageVersion.CSharp14), file.Path)),
            references.Where(path => !app || Path.GetFileName(path) != "Sunder.Sdk.Worker.dll").Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var bytes = new MemoryStream(); var emitted = compilation.Emit(bytes);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics)); bytes.Position = 0;
        var load = new AssemblyLoadContext("range-role", isCollectible: true);
        ServiceProvider? appServices = null; SunderWorkerOptions? workerOptions = null;
        try
        {
            var assembly = load.LoadFromStream(bytes);
            var rpc = new RpcStub { LiveCatalog = true };
            var context = DispatchProxy.Create<IPackageContext, SunderPackageRoleProjectionTests.MetadataOnlyContext>();
            Func<Task> call;
            if (app)
            {
                var module = (ISunderAppPackageModule)Activator.CreateInstance(assembly.GetType("Sunder.Generated.Package.AppModule")!)!;
                var services = new ServiceCollection(); services.AddSingleton<ISunderRpcClient>(rpc);
                module.ConfigureAppServices(services, context);
                appServices = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
                var model = appServices.GetRequiredService(assembly.GetType("RangeConsumer.PageModel")!);
                call = () => ((ValueTask)model.GetType().GetMethod("RunAsync")!.Invoke(model, null)!).AsTask();
            }
            else
            {
                workerOptions = (SunderWorkerOptions)assembly.GetType("Sunder.Generated.Package.WorkerEntryPoint")!
                    .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static, [typeof(IPackageContext), typeof(ISunderRpcClient)])!.Invoke(null, [context, rpc])!;
                var handler = Assert.Single(workerOptions.Providers).Handler;
                call = () => handler.InvokeUnaryAsync(null!, "consumer", "run", JsonSerializer.SerializeToElement(new { })).AsTask();
            }
            Assert.Equal(0, rpc.ScopeCount);
            foreach (var version in new[] { "2.0.0", "2.0.1", "2.9.9", "2.0.1+another-artifact", "2.1.0-beta.1", "1.9.9", "2.0.0-beta.1", "3.0.0" })
            {
                var provider = Provider(version);
                var scope = new RpcStub { Providers = [provider], Current = provider };
                rpc.Providers = scope.Providers; rpc.Current = scope.Current;
                rpc.ScopeFactory = _ => ValueTask.FromResult<ISunderRpcCallScope>(scope);
                var scopesBefore = rpc.ScopeCount;
                var allowed = version is "2.0.0" or "2.0.1" or "2.9.9" or "2.0.1+another-artifact" || prerelease && version == "2.1.0-beta.1";
                if (allowed) { await call(); Assert.Equal(1, scope.InvokeCount); }
                else { Assert.Equal("rpc.provider-not-found", (await Assert.ThrowsAsync<SunderRpcException>(call)).Error.Code); Assert.Equal(0, scope.InvokeCount); }
                Assert.Equal(allowed, scope.Disposed);
                Assert.Equal(scopesBefore + (allowed ? 1 : 0), rpc.ScopeCount);
            }
            var valid = Provider("2.0.1");
            foreach (var invalid in new[] { valid with { PackageId = "example.other" }, valid with { ContractVersion = "9.0.0" },
                valid with { DescriptorSha256 = new string('a', 64) }, valid with { AbiSha256 = new string('a', 64) }, valid with { State = SunderRpcProviderState.Inactive } })
            {
                var scope = new RpcStub { Providers = [invalid], Current = invalid };
                rpc.Providers = scope.Providers; rpc.Current = scope.Current;
                rpc.ScopeFactory = _ => ValueTask.FromResult<ISunderRpcCallScope>(scope);
                var scopesBefore = rpc.ScopeCount;
                Assert.Equal("rpc.provider-not-found", (await Assert.ThrowsAsync<SunderRpcException>(call)).Error.Code);
                Assert.Equal(0, scope.InvokeCount); Assert.False(scope.Disposed); Assert.Equal(scopesBefore, rpc.ScopeCount);
            }
            foreach (var providers in new[] { Array.Empty<SunderRpcProviderSnapshot>(), new[] { valid, valid with { ProviderId = "example.second", Endpoint = new("second") } } })
            {
                var scope = new RpcStub { Providers = providers, Current = valid };
                rpc.Providers = scope.Providers; rpc.Current = scope.Current;
                rpc.ScopeFactory = _ => ValueTask.FromResult<ISunderRpcCallScope>(scope);
                var scopesBefore = rpc.ScopeCount;
                Assert.Equal(providers.Length == 0 ? "rpc.provider-not-found" : "rpc.provider-ambiguous", (await Assert.ThrowsAsync<SunderRpcException>(call)).Error.Code);
                Assert.Equal(0, scope.InvokeCount); Assert.False(scope.Disposed); Assert.Equal(scopesBefore, rpc.ScopeCount);
            }
            var stale = new RpcStub { Providers = [valid], Current = valid with { SessionGeneration = valid.SessionGeneration + 1 } };
            rpc.Providers = stale.Providers; rpc.Current = stale.Current;
            rpc.ScopeFactory = _ => ValueTask.FromResult<ISunderRpcCallScope>(stale);
            var scopesBeforeStale = rpc.ScopeCount;
            Assert.Equal("rpc.stale-endpoint", (await Assert.ThrowsAsync<SunderRpcException>(call)).Error.Code);
            Assert.Equal(0, stale.InvokeCount); Assert.False(stale.Disposed); Assert.Equal(scopesBeforeStale, rpc.ScopeCount);
            var retired = new RpcStub { Providers = [valid], Current = valid };
            rpc.Providers = retired.Providers; rpc.Current = retired.Current;
            retired.Invoke = _ => { retired.Current = valid with { State = SunderRpcProviderState.Inactive }; return ValueTask.FromResult(JsonSerializer.SerializeToElement(new { })); };
            rpc.ScopeFactory = _ => ValueTask.FromResult<ISunderRpcCallScope>(retired);
            Assert.Equal("rpc.stale-endpoint", (await Assert.ThrowsAsync<SunderRpcException>(call)).Error.Code);
            Assert.Equal(1, retired.InvokeCount); Assert.True(retired.Disposed);
        }
        finally
        {
            if (appServices is not null) await appServices.DisposeAsync();
            if (workerOptions?.OnShutdown is { } shutdown)
                await shutdown((SunderWorkerShutdownContext)Activator.CreateInstance(typeof(SunderWorkerShutdownContext), BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, args: ["range-test"], culture: null)!, CancellationToken.None);
            load.Unload();
        }
    }
}
