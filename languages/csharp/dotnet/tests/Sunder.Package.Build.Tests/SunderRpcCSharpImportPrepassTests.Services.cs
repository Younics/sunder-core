using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedCancellation_UsesOriginalCallerAndRetainsRemoteDispatchMetadata(bool selectedProvider)
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var provider = Provider("1.2.3");
        using var caller = new CancellationTokenSource();
        var error = new SunderRpcException(new(SunderRpcErrorKind.Cancelled, "rpc.call.cancelled", "remote",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        var scope = new RpcStub { Current = provider, Invoke = _ => { caller.Cancel(); return ValueTask.FromException<JsonElement>(error); } };
        var rpc = new RpcStub { LiveCatalog = true, Current = provider, Providers = [provider], ScopeFactory = _ => ValueTask.FromResult<ISunderRpcCallScope>(scope) };
        object owner;
        Func<Task> invoke;
        if (selectedProvider)
        {
            owner = probe.CreateNaturalCatalog(rpc);
            var catalog = await probe.ListNatural(owner, default);
            var entry = ((System.Collections.IEnumerable)catalog.GetType().GetProperty("Providers")!.GetValue(catalog)!).Cast<object>().Single();
            invoke = () => probe.CallNatural(entry, caller.Token);
        }
        else
        {
            owner = Activator.CreateInstance(probe.Assembly.GetType("Sunder.Generated.Packages.Example_Dependency_Internal.PackageServices")!, rpc)!;
            var service = owner.GetType().GetProperty("Example")!.GetValue(owner)!;
            invoke = () => ((ValueTask)service.GetType().GetMethod("PingAsync")!.Invoke(service, [caller.Token])!).AsTask();
        }
        await using var lifetime = (IAsyncDisposable)owner;
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(invoke);
        Assert.Equal(caller.Token, actual.CancellationToken);
        Assert.Same(error, actual.InnerException);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, error.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, error.Error.RetrySafety);
        Assert.Equal(1, scope.InvokeCount); Assert.True(scope.Disposed);
    }

    [Fact]
    public void InjectedPackageNamespaceAndTypesDoNotDependOnLocalAlias()
    {
        var original = SunderRpcCSharpImportPrepass.Prepare(_lock, _root);
        var imports = _lock.PackageImports.ToDictionary(pair => pair.Key, pair => pair.Value with { LocalName = "renamed" });
        var renamed = SunderRpcCSharpImportPrepass.Prepare(_lock with { PackageImports = imports }, _root);
        Assert.Equal(original.PackageApis.Select(api => (api.PackageId, api.InterfaceMetadataName, api.ImplementationMetadataName)),
            renamed.PackageApis.Select(api => (api.PackageId, api.InterfaceMetadataName, api.ImplementationMetadataName)));
        Assert.Equal(original.PackageApis.SelectMany(api => api.Services), renamed.PackageApis.SelectMany(api => api.Services));
        Assert.Equal(original.Sources.Where(file => file.Path.StartsWith("injected-apis/", StringComparison.Ordinal)),
            renamed.Sources.Where(file => file.Path.StartsWith("injected-apis/", StringComparison.Ordinal)));
        using var probe = CompileProbe(original);
        Assert.NotNull(probe.Assembly.GetType("Sunder.Generated.Packages.Example_Dependency_Services.IPackageApi"));
        Assert.NotNull(probe.Assembly.GetType("Sunder.Generated.Packages.Example_Dependency_Services.IExampleService"));
        Assert.Null(probe.Assembly.GetType("Sunder.Generated.Services.IDependencyPackageApi"));
    }

    [Fact]
    public void ProviderOnlyIntentCannotInjectAConsumerCatalog()
    {
        var selected = WithProvider(_lock) with { PackageImports = new Dictionary<string, SunderRpcLockedPackageImport>() };
        var imports = SunderRpcCSharpImportPrepass.Prepare(selected, _root);
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Dependency_Services;
            [SunderServiceProvider(typeof(IExampleService))]
            public sealed partial class Provider(IServiceProviders<IExampleService> providers) : IExampleService
            {
                public object Catalog => providers;
                public ValueTask PingAsync(CancellationToken token = default) => ValueTask.CompletedTask;
            }
            """;
        var analysis = Sunder.Package.Build.Authoring.SunderPackageAuthoringCompiler.Analyze(new("example.consumer", "1.0.0",
            [new("provider.cs", source)], References().Append(typeof(Sunder.Sdk.Worker.SunderWorker).Assembly.Location).ToArray(),
            SupplementalSources: imports.Sources, ImportedProviderSurfaces: imports.ProviderSurfaces, ImportedPackageApis: imports.PackageApis));
        Assert.True(analysis.Compilation.Success, string.Join(Environment.NewLine, analysis.Compilation.Diagnostics));
        Assert.Contains(analysis.RolePlan.Diagnostics, value => value.Id == "package-authoring.di-use-required");
    }

    [Fact]
    public async Task InjectedPackageApiIsLazyOwnsUnaryScopesRejectsDriftAndDoesNotRetry()
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var provider = Provider("1.2.3");
        var scopes = new List<RpcStub>();
        var rpc = new RpcStub { LiveCatalog = true, Providers = [provider], Current = provider,
            ScopeFactory = _ => { var scope = new RpcStub { Providers = [provider], Current = provider }; scopes.Add(scope); return ValueTask.FromResult<ISunderRpcCallScope>(scope); } };
        var api = Activator.CreateInstance(probe.Assembly.GetType("Sunder.Generated.Packages.Example_Dependency_Internal.PackageServices")!, rpc)!;
        var service = api.GetType().GetProperty("Example")!.GetValue(api)!;
        var method = service.GetType().GetMethod("PingAsync")!;
        Task Call(CancellationToken token = default) => ((ValueTask)method.Invoke(service, [token])!).AsTask();
        Assert.Equal(0, rpc.ScopeCount); Assert.Equal(0, rpc.DiscoverCount);
        await Call(); Assert.Equal(1, rpc.ScopeCount); Assert.True(Assert.Single(scopes).Disposed); Assert.Equal(1, scopes[0].InvokeCount);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(canceled.Token)); Assert.Equal(1, rpc.ScopeCount);
        var failed = new RpcStub { Providers = [provider], Current = provider, Invoke = _ => ValueTask.FromException<JsonElement>(new SunderRpcException(
            new(SunderRpcErrorKind.StaleEndpoint, "rpc.stale-endpoint", "retired after dispatch"))) };
        rpc.ScopeFactory = _ => ValueTask.FromResult<ISunderRpcCallScope>(failed);
        await Assert.ThrowsAsync<SunderRpcException>(() => Call()); Assert.Equal(1, failed.InvokeCount); Assert.True(failed.Disposed);
        var scopedDrift = new RpcStub { Current = provider with { AbiSha256 = new string('f', 64) } };
        rpc.ScopeFactory = _ => ValueTask.FromResult<ISunderRpcCallScope>(scopedDrift);
        var scopedError = await Assert.ThrowsAsync<SunderRpcException>(() => Call());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, scopedError.Error.Kind);
        Assert.Equal(0, scopedDrift.InvokeCount); Assert.True(scopedDrift.Disposed);
        var drift = new RpcStub { Providers = [provider], Current = provider with { SessionGeneration = provider.SessionGeneration + 1 } };
        rpc.Current = drift.Current;
        rpc.ScopeFactory = _ => ValueTask.FromResult<ISunderRpcCallScope>(drift);
        var scopesBeforeDrift = rpc.ScopeCount;
        await Assert.ThrowsAsync<SunderRpcException>(() => Call()); Assert.Equal(0, drift.InvokeCount); Assert.False(drift.Disposed);
        Assert.Equal(scopesBeforeDrift, rpc.ScopeCount); // Reject stale metadata before allocating a call scope.
        await ((IAsyncDisposable)api).DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Call());
    }

    [Fact]
    public async Task InjectedPackageStreamOpensLazilyAndRetirementCancelsAndDrainsItsScope()
    {
        var json = JsonNode.Parse(_descriptor.GetCanonicalUtf8())!;
        json["$defs"]!["Count"] = new JsonObject { ["type"] = "integer" };
        json["services"]!["example"]!["methods"]!["watch"] = new JsonObject
        {
            ["kind"] = "server-stream", ["requestSchema"] = new JsonObject { ["$ref"] = "#/$defs/Empty" },
            ["eventSchema"] = new JsonObject { ["$ref"] = "#/$defs/Count" }, ["idempotency"] = "none", ["errors"] = new JsonArray(),
        };
        var descriptor = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(json));
        File.WriteAllBytes(SunderRpcContractCache.GetDescriptorPath(_root, descriptor.Sha256), descriptor.GetCanonicalUtf8());
        var receipt = JsonNode.Parse(File.ReadAllBytes(SunderRpcContractCache.GetReceiptPath(_root, _descriptor.Sha256)))!;
        receipt["descriptorBytes"] = descriptor.GetCanonicalUtf8().Length; receipt["descriptorSha256"] = descriptor.Sha256; receipt["abiSha256"] = descriptor.AbiSha256;
        File.WriteAllBytes(SunderRpcContractCache.GetReceiptPath(_root, descriptor.Sha256), SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(receipt)));
        var artifact = _lock.PackageArtifacts["example.dependency"];
        var selected = _lock with { PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact>
        {
            [artifact.PackageId] = SeedDescriptorOnlyArtifact(artifact with { Contracts = new Dictionary<string, SunderRpcLockedArtifactContract>
            {
                [descriptor.ContractId] = artifact.Contracts[descriptor.ContractId] with { DescriptorBytes = descriptor.GetCanonicalUtf8().Length, DescriptorSha256 = descriptor.Sha256, AbiSha256 = descriptor.AbiSha256, Etag = "\"" + descriptor.Sha256 + "\"" },
            } }),
        } };
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(selected, _root));
        var provider = Provider("1.2.3") with { DescriptorSha256 = descriptor.Sha256, AbiSha256 = descriptor.AbiSha256 };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new RpcStub { Providers = [provider], Current = provider, Subscribe = Values };
        var rpc = new RpcStub { LiveCatalog = true, Providers = [provider], Current = provider, ScopeFactory = _ => ValueTask.FromResult<ISunderRpcCallScope>(scope) };
        var api = Activator.CreateInstance(probe.Assembly.GetType("Sunder.Generated.Packages.Example_Dependency_Internal.PackageServices")!, rpc)!;
        var service = api.GetType().GetProperty("Example")!.GetValue(api)!;
        var stream = (IAsyncEnumerable<long>)service.GetType().GetMethod("WatchAsync")!.Invoke(service, [CancellationToken.None])!;
        Assert.Equal(0, rpc.ScopeCount);
        await using var iterator = stream.GetAsyncEnumerator(); Assert.True(await iterator.MoveNextAsync()); Assert.Equal(1, iterator.Current);
        var next = iterator.MoveNextAsync().AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retirement = ((IAsyncDisposable)api).DisposeAsync().AsTask();
        var canceledByRetirement = await Assert.ThrowsAsync<SunderRpcException>(() => next);
        Assert.Equal("rpc.binding.retired", canceledByRetirement.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, canceledByRetirement.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, canceledByRetirement.Error.RetrySafety);
        await retirement.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(scope.Disposed); Assert.Equal(1, rpc.ScopeCount);

        async IAsyncEnumerable<JsonElement> Values([EnumeratorCancellation] CancellationToken token)
        {
            yield return JsonSerializer.SerializeToElement(1);
            entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }
}
