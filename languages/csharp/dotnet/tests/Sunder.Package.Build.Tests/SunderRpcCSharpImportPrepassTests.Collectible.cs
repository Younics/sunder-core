using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedOrdinaryRecordGraphCollectsWithoutProcessGlobalJsonAccessors(bool invoke)
    {
        var weak = InvokeCollectibleImportedGraph(invoke);
        for (var attempt = 0; weak.IsAlive && attempt < 12; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(20);
        }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private WeakReference InvokeCollectibleImportedGraph(bool invoke)
    {
        var imports = PrepareMigrationImports("""
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            using Sunder.Sdk.Rpc;
            [SunderService(SunderServiceExposure.Public)] public interface GraphService {
                ValueTask<GraphValue> EchoAsync(GraphValue request);
                ValueTask<Empty?> AAsync(Empty? request);
            }
            public sealed record Empty;
            public enum State { [System.Text.Json.Serialization.JsonStringEnumMemberName("ready")] Ready }
            public sealed record Item(string Name, State State);
            [SunderUnion(typeof(TextChoice), typeof(NumberChoice)), SunderDiscriminator("kind")] public abstract record Choice;
            [SunderVariant("text")] public sealed record TextChoice : Choice { public required string Value { get; init; } }
            [SunderVariant("number")] public sealed record NumberChoice : Choice { public required int Value { get; init; } }
            public sealed record GraphValue {
                public required IReadOnlyList<Item> Items { get; init; }
                public required IReadOnlyDictionary<string, Item> Map { get; init; }
                public required Choice? Choice { get; init; }
                public required State? MaybeState { get; init; }
                public required System.Guid? RunId { get; init; }
                public SunderRpcOptional<string?> Optional { get; init; }
            }
            """, implement: true);
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Dependency_Services.CExample_Dependency_Graph;
            using Sunder.Generated.Packages.Example_Dependency_Models;
            [SunderServiceProvider(typeof(IGraphService))] public sealed partial class GraphProvider : IGraphService {
                public ValueTask<GraphValue> EchoAsync(GraphValue request, CancellationToken cancellationToken = default) => ValueTask.FromResult(request);
                public ValueTask<Empty?> AAsync(Empty? request, CancellationToken cancellationToken = default) => ValueTask.FromResult(request);
            }
            """;
        var analysis = SunderPackageAuthoringCompiler.Analyze(new("example.consumer", "1.0.0", [new("provider.cs", source)], NaturalReferences(),
            SupplementalSources: imports.Sources, ImportedPackageApis: imports.PackageApis, ImportedProviderSurfaces: imports.ProviderSurfaces));
        Assert.True(analysis.Success, string.Join(Environment.NewLine, analysis.Compilation.Diagnostics.Concat(analysis.RolePlan.Diagnostics)));
        using var binary = new MemoryStream();
        var emission = analysis.Compilation.SourceCompilation!.Emit(binary);
        Assert.True(emission.Success, string.Join(Environment.NewLine, emission.Diagnostics)); binary.Position = 0;
        var load = new AssemblyLoadContext("collectible-rpc-record-graph", isCollectible: true);
        var weak = new WeakReference(load, trackResurrection: true);
        try
        {
            var assembly = load.LoadFromStream(binary);
            var type = assembly.GetType("GraphProvider", throwOnError: true)!;
            var instance = Activator.CreateInstance(type)!;
            var registration = (SunderWorkerProviderRegistration)type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(instance, null)!;
            if (invoke)
            {
                const string payload = """{"items":[{"name":"one","state":"ready"}],"map":{"a":{"name":"two","state":"ready"}},"choice":{"kind":"text","value":"value"},"maybeState":null,"runId":null,"optional":null}""";
                foreach (var json in new[] { payload, payload.Replace(",\"optional\":null", "", StringComparison.Ordinal),
                             payload.Replace("\"maybeState\":null", "\"maybeState\":\"ready\"", StringComparison.Ordinal) })
                {
                    var wire = JsonSerializer.Deserialize<JsonElement>(json);
                    var result = registration.Handler.InvokeUnaryAsync(null!, "graph", "echo", wire).AsTask().GetAwaiter().GetResult();
                    Assert.True(JsonElement.DeepEquals(wire, result), result.GetRawText());
                }
                foreach (var json in new[] { "null", "{}" })
                {
                    var wire = JsonSerializer.Deserialize<JsonElement>(json);
                    var result = registration.Handler.InvokeUnaryAsync(null!, "graph", "a", wire).AsTask().GetAwaiter().GetResult();
                    Assert.True(JsonElement.DeepEquals(wire, result), result.GetRawText());
                }
            }
            AssertNoCollectibleJsonAccessorEntries(load);
        }
        finally { load.Unload(); }
        return weak;
    }

    // Diagnostic assertion only: never mutates framework caches or compensates
    // for a leak by waiting for their opportunistic expiration.
    private static void AssertNoCollectibleJsonAccessorEntries(AssemblyLoadContext load)
    {
        var accessor = typeof(DefaultJsonTypeInfoResolver).GetField("s_memberAccessor", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        if (accessor is null) return;
        var cache = accessor.GetType().GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(accessor);
        if (cache is null) return; // A non-caching framework implementation has no such root.
        var dictionary = (IEnumerable)cache.GetType().GetField("_cache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        foreach (var entry in dictionary)
        {
            var key = entry.GetType().GetProperty("Key")!.GetValue(entry)!;
            var type = (Type)key.GetType().GetField("Item2")!.GetValue(key)!;
            var member = (MemberInfo?)key.GetType().GetField("Item3")!.GetValue(key);
            Assert.False(Belongs(type) || member?.DeclaringType is { } declaring && Belongs(declaring), key.ToString());
        }
        bool Belongs(Type type) => AssemblyLoadContext.GetLoadContext(type.Assembly) == load
            || type.HasElementType && Belongs(type.GetElementType()!)
            || type.IsGenericType && type.GenericTypeArguments.Any(Belongs);
    }
}
