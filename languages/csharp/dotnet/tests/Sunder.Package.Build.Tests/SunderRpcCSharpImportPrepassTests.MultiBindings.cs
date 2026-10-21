using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    [Fact]
    public async Task MultiContractProviderUsesOneInstanceAndExplicitDescribeBindingsWithRealContext()
    {
        var imports = PrepareMigrationImports("""
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public, ServiceId = "shared")] public interface FirstService { ValueTask<TextReply> DescribeAsync(CancellationToken token = default); }
            [SunderService(SunderServiceExposure.Public, ServiceId = "shared")] public interface SecondService { ValueTask<NumberReply> DescribeAsync(CancellationToken token = default); }
            [SunderService(SunderServiceExposure.Public)] public interface OwnerService { ValueTask<string> OwnerAsync(); }
            public sealed record TextReply(string Value);
            public sealed record NumberReply(long Value);
            """, implement: true);
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            using IFirstService = Sunder.Generated.Packages.Example_Dependency_Services.CExample_Dependency_First.ISharedService;
            using ISecondService = Sunder.Generated.Packages.Example_Dependency_Services.CExample_Dependency_Second.ISharedService;
            using Sunder.Generated.Packages.Example_Dependency_Services.CExample_Dependency_Owner;
            using Sunder.Generated.Packages.Example_Dependency_Models;
            [SunderServiceProvider(typeof(IFirstService))]
            [SunderServiceProvider(typeof(ISecondService))]
            [SunderServiceProvider(typeof(IOwnerService))]
            public sealed partial class Provider : IFirstService, ISecondService, IDisposable
            {
                public static int Created { get; private set; }
                public static int Disposed { get; private set; }
                private int calls;
                public Provider() { Created++; }
                ValueTask<TextReply> IFirstService.DescribeAsync(CancellationToken token)
                    => ValueTask.FromResult(new TextReply { Value = "original" } with { Value = "text-" + ++calls });
                ValueTask<NumberReply> ISecondService.DescribeAsync(CancellationToken token)
                    => ValueTask.FromResult(new NumberReply { Value = ++calls });
                public ValueTask<string> OwnerAsync(SunderRpcInvocationContext context)
                    => ValueTask.FromResult(context.CallerPackageId + ":" + ++calls);
                public void Dispose() { Disposed++; }
            }
            """;
        var analysis = SunderPackageAuthoringCompiler.Analyze(new("example.provider", "1.0.0", [new("provider.cs", source)], NaturalReferences(),
            SupplementalSources: imports.Sources, ImportedProviderSurfaces: imports.ProviderSurfaces, ImportedPackageApis: imports.PackageApis));
        Assert.True(analysis.Success, string.Join(Environment.NewLine, analysis.Compilation.Diagnostics.Concat(analysis.RolePlan.Diagnostics)));
        Assert.Equal(3, analysis.Compilation.ImportedProviders.Count);
        Assert.Single(analysis.Composition!.Worker, entry => entry.Type.Name == "Provider");
        Assert.Equal(3, analysis.Compilation.ImportedProviders.Select(binding => binding.Provider.ServicesFactory).Distinct(StringComparer.Ordinal).Count());
        var composition = SunderPackageCompositionEmitter.EmitWorker(analysis);
        var compilation = analysis.Compilation.SourceCompilation!.AddSyntaxTrees(CSharpSyntaxTree.ParseText(composition.Text,
            new CSharpParseOptions(LanguageVersion.CSharp14), composition.Path));
        using var bytes = new MemoryStream(); var emission = compilation.Emit(bytes);
        Assert.True(emission.Success, string.Join(Environment.NewLine, emission.Diagnostics)); bytes.Position = 0;
        var load = new AssemblyLoadContext("multi-contract-provider", isCollectible: true);
        try
        {
            var assembly = load.LoadFromStream(bytes);
            var providerType = assembly.GetType("Provider", true)!;
            var context = DispatchProxy.Create<IPackageContext, SunderPackageRoleProjectionTests.MetadataOnlyContext>();
            var options = (SunderWorkerOptions)assembly.GetType("Sunder.Generated.Package.WorkerEntryPoint", true)!
                .GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static, [typeof(IPackageContext), typeof(ISunderRpcClient)])!.Invoke(null, [context, new RpcStub()])!;
            try
            {
                Assert.Equal(0, providerType.GetProperty("Created")!.GetValue(null));
                var empty = JsonSerializer.SerializeToElement(new { });
                var first = Assert.Single(options.Providers, value => value.ContractId.EndsWith(".first", StringComparison.Ordinal));
                var second = Assert.Single(options.Providers, value => value.ContractId.EndsWith(".second", StringComparison.Ordinal));
                var owner = Assert.Single(options.Providers, value => value.ContractId.EndsWith(".owner", StringComparison.Ordinal));
                Assert.Equal("text-1", (await first.Handler.InvokeUnaryAsync(null!, "shared", "describe", empty)).GetProperty("value").GetString());
                Assert.Equal(2L, (await second.Handler.InvokeUnaryAsync(null!, "shared", "describe", empty)).GetProperty("value").GetInt64());
                Assert.Equal("caller.package:3", (await owner.Handler.InvokeUnaryAsync(new StackContributorSourceParityTests.ContentHost().Context(), "owner", "owner", empty)).GetString());
                Assert.Equal(1, providerType.GetProperty("Created")!.GetValue(null));
                var services = (IServiceProvider)options.OnShutdown!.Target!.GetType().GetProperty("Services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(options.OnShutdown.Target)!;
                var instance = services.GetService(providerType)!;
                var registrations = (IReadOnlyList<SunderWorkerProviderRegistration>)providerType.GetMethod("CreateSunderRpcProviderRegistrations")!.Invoke(instance, null)!;
                Assert.Equal(options.Providers.Select(value => value.ProviderId).Order(), registrations.Select(value => value.ProviderId).Order());
            }
            finally
            {
                var reason = (SunderWorkerShutdownContext)Activator.CreateInstance(typeof(SunderWorkerShutdownContext), BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null, args: ["multi-binding-test"], culture: null)!;
                await options.OnShutdown!(reason, CancellationToken.None);
            }
            Assert.Equal(1, providerType.GetProperty("Disposed")!.GetValue(null));
        }
        finally { load.Unload(); }
    }

    private SunderRpcCSharpImportPrepassResult PrepareMigrationImports(string declaration, bool implement = false, bool generateServiceInterfaces = false)
    {
        var input = new SunderRpcCSharpCompilerInput("example.dependency", "1.2.3", [new("declaration.cs", declaration)], NaturalReferences(), GenerateServiceInterfaces: generateServiceInterfaces);
        var compiled = generateServiceInterfaces
            ? SunderRpcCSharpOwnedClientPrepass.Compile(new(), input).Compilation
            : new SunderRpcCSharpSourceCompiler().Compile(input);
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        var original = _lock.PackageArtifacts["example.dependency"];
        var template = original.Contracts[_descriptor.ContractId];
        var entries = new Dictionary<string, SunderRpcLockedArtifactContract>(StringComparer.Ordinal);
        foreach (var contract in compiled.Contracts)
        {
            var descriptor = contract.Descriptor;
            File.WriteAllBytes(SunderRpcContractCache.GetDescriptorPath(_root, descriptor.Sha256), descriptor.GetCanonicalUtf8());
            File.WriteAllBytes(SunderRpcContractCache.GetReceiptPath(_root, descriptor.Sha256), SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1, contractId = descriptor.ContractId, contractVersion = descriptor.Version,
                descriptorBytes = descriptor.GetCanonicalUtf8().Length, descriptorSha256 = descriptor.Sha256, abiSha256 = descriptor.AbiSha256,
            })));
            entries.Add(descriptor.ContractId, template with
            {
                ContractId = descriptor.ContractId, ResolvedContractVersion = descriptor.Version, DescriptorBytes = descriptor.GetCanonicalUtf8().Length,
                DescriptorSha256 = descriptor.Sha256, AbiSha256 = descriptor.AbiSha256, Etag = "\"" + descriptor.Sha256 + "\"",
            });
        }
        var artifact = SeedProjectedArtifact(original with { Contracts = entries, PublicContractCount = entries.Count }, compiled.Contracts.Select(SunderRpcCSharpModelProjection.Entry).ToArray());
        return SunderRpcCSharpImportPrepass.Prepare(_lock with
        {
            PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact> { [artifact.PackageId] = artifact },
            PackageImports = new Dictionary<string, SunderRpcLockedPackageImport>
            { [artifact.PackageId] = _lock.PackageImports[artifact.PackageId] with { RequiredContracts = entries.Keys.Order(StringComparer.Ordinal).ToArray() } },
            ProviderImports = implement ? compiled.Contracts.ToDictionary(contract => contract.ContractKey,
                contract => new SunderRpcLockedProviderImport(contract.ContractKey, artifact.PackageId, contract.Descriptor.ContractId)) : new Dictionary<string, SunderRpcLockedProviderImport>(),
        }, _root);
    }
}
