using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests
{
    [Fact]
    public async Task ImportedNaturalProviderNeedsNoContextUnitOrAuthoredTransportAdapter()
    {
        const string declaration = """
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)]
            public interface EmbeddingProviderService
            {
                ValueTask<Entry> DescribeAsync(CancellationToken token = default);
                ValueTask<IReadOnlyList<Entry>> ListModelsAsync(CancellationToken token = default);
                ValueTask<Entry> GetReadinessAsync(CancellationToken token = default);
                ValueTask<Entry> SpaceIdentityAsync(string modelId, CancellationToken token = default);
                ValueTask<Entry> GenerateAsync(string modelId, string text, CancellationToken token = default);
                ValueTask<IReadOnlyList<Entry>> GenerateBatchAsync(string modelId, IReadOnlyList<string> texts, CancellationToken token = default);
                ValueTask ResetAsync(CancellationToken token = default);
            }
            public sealed record Entry(string Value);
            """;
        var imports = PrepareNaturalImport(declaration, "localEmbedding");
        // The provider chooses the canonical interface even though its reviewed
        // implementation key has a different name. No key-specific wrapper is authored.
        const string providerSource = """
            using System.Collections.Generic;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Dependency_Services;
            using Sunder.Generated.Packages.Example_Dependency_Models;
            [SunderServiceProvider(typeof(IEmbeddingProviderService))]
            public sealed partial class LocalEmbedding : IEmbeddingProviderService
            {
                public int Resets { get; private set; }
                public ValueTask<Entry> DescribeAsync(CancellationToken token = default) => Reply("description");
                public ValueTask<IReadOnlyList<Entry>> ListModelsAsync(CancellationToken token = default) => ValueTask.FromResult<IReadOnlyList<Entry>>([new() { Value = "model" }]);
                public ValueTask<Entry> GetReadinessAsync(CancellationToken token = default) => Reply("ready");
                public ValueTask<Entry> SpaceIdentityAsync(string modelId, CancellationToken token = default) => Reply(modelId);
                public ValueTask<Entry> GenerateAsync(CExample_Dependency_Embedding_Provider_EmbeddingProviderGenerateRequest request, CancellationToken token = default) => Reply(request.ModelId + ":" + request.Text);
                public ValueTask<IReadOnlyList<Entry>> GenerateBatchAsync(CExample_Dependency_Embedding_Provider_EmbeddingProviderGenerateBatchRequest request, CancellationToken token = default)
                    => ValueTask.FromResult<IReadOnlyList<Entry>>(request.Texts.Select(text => new Entry { Value = request.ModelId + ":" + text }).ToArray());
                public ValueTask ResetAsync(CancellationToken token = default) { Resets++; return ValueTask.CompletedTask; }
                private static ValueTask<Entry> Reply(string value) => ValueTask.FromResult(new Entry { Value = value });
            }
            """;
        var verified = CompileNaturalProvider(imports, providerSource);
        using var bytes = new MemoryStream(); var emit = verified.SourceCompilation!.Emit(bytes);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics)); bytes.Position = 0;
        var load = new AssemblyLoadContext("natural-embedding-provider", isCollectible: true);
        try
        {
            var assembly = load.LoadFromStream(bytes);
            var api = assembly.GetType("Sunder.Generated.Packages.Example_Dependency_Services.IEmbeddingProviderService", true)!;
            Assert.DoesNotContain(api.GetMethods().SelectMany(method => method.GetParameters()), parameter => parameter.ParameterType == typeof(SunderRpcInvocationContext));
            Assert.Equal(typeof(CancellationToken), Assert.Single(api.GetMethod("DescribeAsync")!.GetParameters()).ParameterType);
            Assert.Equal(typeof(ValueTask), api.GetMethod("ResetAsync")!.ReturnType);
            var instance = Activator.CreateInstance(assembly.GetType("LocalEmbedding", true)!)!;
            var registration = (SunderWorkerProviderRegistration)instance.GetType().GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(instance, null)!;
            var result = await registration.Handler.InvokeUnaryAsync(null!, "embedding-provider", "generate", JsonSerializer.SerializeToElement(new { modelId = "model", text = "text" }));
            Assert.Equal("model:text", result.GetProperty("value").GetString());
            var unit = await registration.Handler.InvokeUnaryAsync(null!, "embedding-provider", "reset", JsonSerializer.SerializeToElement(new { }));
            Assert.Empty(unit.EnumerateObject()); Assert.Equal(1, instance.GetType().GetProperty("Resets")!.GetValue(instance));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registration.Handler.InvokeUnaryAsync(null!, "embedding-provider", "reset", JsonSerializer.SerializeToElement(new { }), canceled.Token).AsTask());
            Assert.Equal(1, instance.GetType().GetProperty("Resets")!.GetValue(instance));
        }
        finally { load.Unload(); }
    }

    private SunderRpcCSharpImportPrepassResult PrepareNaturalImport(string declaration, string key, bool consumer = true)
    {
        var compiled = new SunderRpcCSharpSourceCompiler().Compile(new("example.dependency", "1.0.0", [new("declaration.cs", declaration)], NaturalReferences()));
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        var contract = Assert.Single(compiled.Contracts);
        var descriptor = contract.Descriptor;
        File.WriteAllBytes(SunderRpcContractCache.GetDescriptorPath(_root, descriptor.Sha256), descriptor.GetCanonicalUtf8());
        File.WriteAllBytes(SunderRpcContractCache.GetReceiptPath(_root, descriptor.Sha256), SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(new
        {
            schemaVersion = 1, contractId = descriptor.ContractId, contractVersion = descriptor.Version,
            descriptorBytes = descriptor.GetCanonicalUtf8().Length, descriptorSha256 = descriptor.Sha256, abiSha256 = descriptor.AbiSha256,
        })));
        var artifact = _lock.PackageArtifacts["example.dependency"];
        var evidence = artifact.Contracts[_descriptor.ContractId] with
        {
            ContractId = descriptor.ContractId, ResolvedContractVersion = descriptor.Version, DescriptorBytes = descriptor.GetCanonicalUtf8().Length,
            DescriptorSha256 = descriptor.Sha256, AbiSha256 = descriptor.AbiSha256, Etag = "\"" + descriptor.Sha256 + "\"",
        };
        return SunderRpcCSharpImportPrepass.Prepare(_lock with
        {
            PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact>
            { [artifact.PackageId] = SeedProjectedArtifact(artifact with { Contracts = new Dictionary<string, SunderRpcLockedArtifactContract> { [descriptor.ContractId] = evidence } },
                [SunderRpcCSharpModelProjection.Entry(contract)]) },
            ProviderImports = new Dictionary<string, SunderRpcLockedProviderImport> { [key] = new(key, artifact.PackageId, descriptor.ContractId) },
            PackageImports = consumer ? _lock.PackageImports : new Dictionary<string, SunderRpcLockedPackageImport>(),
        }, _root);
    }

    private SunderRpcLockedPackageArtifact SeedDescriptorOnlyArtifact(SunderRpcLockedPackageArtifact artifact)
        => SeedProjectedArtifact(artifact, ModelProjectionTestEvidence.EmptySourceProjection(artifact).Contracts);

    private SunderRpcLockedPackageArtifact SeedProjectedArtifact(SunderRpcLockedPackageArtifact artifact, IReadOnlyList<SunderRpcModelProjectionContract> contracts)
    {
        var projection = new SunderRpcModelProjectionDocument(1, artifact.PackageId, artifact.SelectedPackageVersion, contracts);
        var path = SunderRpcContractCache.GetModelProjectionPath(_root, projection.ModelProjectionSha256);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, projection.ToCanonicalUtf8());
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(path)!, projection.ModelProjectionSha256 + SunderRpcContractCache.ReceiptSuffix),
            SunderRpcContractCache.ModelProjectionReceiptBytes(projection.ModelProjectionSha256, projection.ModelProjectionBytes));
        return artifact with { ModelProjectionBytes = projection.ModelProjectionBytes, ModelProjectionSha256 = projection.ModelProjectionSha256 };
    }

    private static string[] NaturalReferences()
        => References().Append(typeof(SunderWorkerProviderRegistration).Assembly.Location).Distinct(StringComparer.Ordinal).ToArray();

    private static SunderRpcCSharpCompilerInput NaturalProviderInput(SunderRpcCSharpImportPrepassResult imports, string source)
        => new("example.provider", "1.0.0", [new("provider.cs", source)], NaturalReferences(),
            SupplementalSources: imports.Sources, ImportedProviderSurfaces: imports.ProviderSurfaces);

    private static SunderRpcCSharpCompilationResult CompileNaturalProvider(SunderRpcCSharpImportPrepassResult imports, string source)
    {
        var input = NaturalProviderInput(imports, source);
        var compiler = new SunderRpcCSharpSourceCompiler();
        var provider = compiler.Compile(input);
        Assert.True(provider.Success, string.Join(Environment.NewLine, provider.Diagnostics));
        var registration = SunderRpcCSharpImplementationEmitter.Emit([], provider.ImportedProviders);
        var verified = compiler.Compile(input with
        {
            SupplementalSources = imports.Sources.Append(new SunderRpcCSharpSourceFile("authoring.g.cs", registration)).ToArray(),
            RequireGeneratedMembersResolved = true,
        });
        Assert.True(verified.Success, string.Join(Environment.NewLine, verified.Diagnostics));
        Assert.Equal(registration, SunderRpcCSharpImplementationEmitter.Emit([], verified.ImportedProviders));
        return verified;
    }
}
