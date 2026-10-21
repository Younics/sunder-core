using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Framework;
using TaskItem = Microsoft.Build.Utilities.TaskItem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpImportPrepassTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sunder-rpc-import-prepass", Guid.NewGuid().ToString("N"));
    private readonly SunderRpcContractDescriptor _descriptor;
    private readonly SunderRpcContractLock _lock;

    public SunderRpcCSharpImportPrepassTests()
    {
        Directory.CreateDirectory(_root);
        SunderRpcContractCacheStore.InitializeRoot(_root);
        _lock = SunderRpcAuthoringDocuments.ReadContractLock(Fixture("authoring/conformance/valid/package-api-contract-lock.json"));
        _descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Fixture("conformance/valid/minimal.rpc.json")));
        File.WriteAllBytes(SunderRpcContractCache.GetDescriptorPath(_root, _descriptor.Sha256), _descriptor.GetCanonicalUtf8());
        File.Copy(Fixture("authoring/conformance/valid/cache-entry-receipt.json"), SunderRpcContractCache.GetReceiptPath(_root, _descriptor.Sha256));
        ModelProjectionTestEvidence.Seed(_root, _lock.PackageArtifacts["example.dependency"]);
    }

    [Fact]
    public void Prepare_IsDeterministicReadOnlyAndCompilesWithoutOwnedSourceOrWorkerReference()
    {
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().Select(path => (path, File.ReadAllText(path), File.GetLastWriteTimeUtc(path))).ToArray();
        var result = SunderRpcCSharpImportPrepass.Prepare(_lock, _root);
        var repeated = SunderRpcCSharpImportPrepass.Prepare(_lock, _root);
        Assert.Equal(result.Sources, repeated.Sources);
        Assert.Equal(before, Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order().Select(path => (path, File.ReadAllText(path), File.GetLastWriteTimeUtc(path))).ToArray());
        var contract = Assert.Single(result.Contracts);
        Assert.Equal(_descriptor.ContractId, contract.ContractKey);
        Assert.Equal("Sunder.Generated.Packages.Example_Dependency_Internal.CExample_Rpc_V1_Minimal", contract.GeneratedLogicalName);
        Assert.Equal(_descriptor.GetCanonicalUtf8(), contract.Descriptor.GetCanonicalUtf8());
        Assert.Empty(result.ProviderSurfaces);
        Assert.All(result.Sources, source =>
        {
            Assert.DoesNotContain("Sunder.Sdk.Worker", source.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(_root, source.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", source.Text, StringComparison.Ordinal);
            Assert.EndsWith("\n", source.Text, StringComparison.Ordinal);
            Assert.False(Path.IsPathRooted(source.Path));
        });
        var compiler = new SunderRpcCSharpSourceCompiler();
        var compiled = compiler.Compile(new("example.consumer", "1.0.0", [], References(), SupplementalSources: result.Sources));
        Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Empty(compiled.Contracts);
        using var probe = CompileProbe(result);
        Assert.Null(probe.Assembly.GetType("Sunder.Generated.Packages.Example_Dependency_Internal.CExample_Rpc_V1_Minimal.Api.Provider")!.GetProperty("Endpoint"));
    }

    [Theory]
    [InlineData("example.rpc.v1.minimal", "CExample_Rpc_V1_Minimal")]
    [InlineData("example.rpcv1.minimal", "CExample_Rpcv1_Minimal")]
    [InlineData("1.2.a", "C1_2_A")]
    [InlineData("1a.2", "C1a_2")]
    public void ContractNames_AreAutomaticallyAndInjectivelyDerived(string id, string expected)
        => Assert.Equal(expected, SunderRpcCSharpImportPrepass.ContractName(id));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prepare_SharedConsumerProviderContractGeneratesOneBindingAndTrustedAggregateInterface(bool canonicalAggregate)
    {
        var selected = WithProvider(_lock);
        var result = SunderRpcCSharpImportPrepass.Prepare(selected, _root);
        Assert.Single(result.Contracts);
        var surface = Assert.Single(result.ProviderSurfaces);
        Assert.Same(result.Contracts[0], surface.Contract);
        Assert.Equal("Sunder.Generated.Packages.Example_Dependency_Services.IMinimalProviderService", surface.InterfaceMetadataName);
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Dependency_Services;
            [SunderServiceProvider(typeof(IMinimalProviderService))]
            public partial class LocalProvider : IMinimalProviderService
            {
                public ValueTask PingAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
            }
            """;
        if (canonicalAggregate) source = source.Replace("using Sunder.Generated.Packages.Example_Dependency_Services;",
            "using IMinimalProviderService = Sunder.Generated.Packages.Example_Dependency_Services.CExample_Rpc_V1_Minimal.IContractServices;", StringComparison.Ordinal);
        var input = new SunderRpcCSharpCompilerInput("example.consumer", "3.0.0", [new("Provider.cs", source)],
            References().Append(typeof(SunderWorkerProviderRegistration).Assembly.Location).ToArray(),
            SupplementalSources: result.Sources, ImportedProviderSurfaces: result.ProviderSurfaces);
        var compiler = new SunderRpcCSharpSourceCompiler();
        var first = compiler.Compile(input);
        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics));
        Assert.Empty(first.Contracts);
        var registration = SunderRpcCSharpImplementationEmitter.Emit(first.Contracts, first.ImportedProviders);
        var verified = compiler.Compile(input with
        {
            SupplementalSources = result.Sources.Append(new SunderRpcCSharpSourceFile("authoring.g.cs", registration)).ToArray(),
            RequireGeneratedMembersResolved = true,
        });
        Assert.True(verified.Success, string.Join(Environment.NewLine, verified.Diagnostics));
        Assert.Single(verified.ImportedProviders);

        var providerOnly = SunderRpcCSharpImportPrepass.Prepare(selected with { PackageImports = new Dictionary<string, SunderRpcLockedPackageImport>() }, _root);
        Assert.Single(providerOnly.Contracts);
        Assert.DoesNotContain(providerOnly.Sources, item => item.Path.StartsWith("apis/", StringComparison.Ordinal));
        Assert.DoesNotContain("PackageApi", providerOnly.Sources.Single(item => item.Path == "imports.g.cs").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_ServicePunctuationPreservesTransportNamesAndRejectsContractLocalNaturalCollisions()
    {
        using var parity = JsonDocument.Parse(File.ReadAllBytes(Fixture("authoring/current-parity/service-name-vectors.json")));
        var vectors = parity.RootElement.GetProperty("clientServiceNameVectors").EnumerateArray().ToArray();
        var artifact = _lock.PackageArtifacts["example.dependency"];
        var contracts = new Dictionary<string, SunderRpcLockedArtifactContract>(StringComparer.Ordinal);
        for (var index = 0; index < vectors.Length; index++)
        {
            var json = JsonNode.Parse(_descriptor.GetCanonicalUtf8())!.AsObject();
            var services = json["services"]!.AsObject();
            var service = services["example"]!.DeepClone();
            services.Clear();
            services[vectors[index].GetProperty("serviceId").GetString()!] = service;
            services["secondservice"] = service.DeepClone();
            json["contractId"] = "example.naming.c" + index;
            var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));
            var entry = artifact.Contracts[_descriptor.ContractId] with
            {
                ContractId = descriptor.ContractId,
                DescriptorBytes = descriptor.GetCanonicalUtf8().Length,
                DescriptorSha256 = descriptor.Sha256,
                AbiSha256 = descriptor.AbiSha256,
                Etag = "\"" + descriptor.Sha256 + "\"",
            };
            contracts.Add(descriptor.ContractId, entry);
            File.WriteAllBytes(SunderRpcContractCache.GetDescriptorPath(_root, descriptor.Sha256), descriptor.GetCanonicalUtf8());
            File.WriteAllText(SunderRpcContractCache.GetReceiptPath(_root, descriptor.Sha256), $$"""
                {
                  "schemaVersion": 1,
                  "contractId": "{{descriptor.ContractId}}",
                  "contractVersion": "{{descriptor.Version}}",
                  "descriptorBytes": {{entry.DescriptorBytes}},
                  "descriptorSha256": "{{descriptor.Sha256}}",
                  "abiSha256": "{{descriptor.AbiSha256}}"
                }
                """ + "\n");
            var individual = _lock with
            {
                PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact>
                { [artifact.PackageId] = SeedDescriptorOnlyArtifact(artifact with { Contracts = new Dictionary<string, SunderRpcLockedArtifactContract> { [entry.ContractId] = entry } }) },
            };
            var generated = SunderRpcCSharpImportPrepass.Prepare(individual, _root);
            var api = generated.Sources.Single(item => item.Path == $"apis/example.naming.c{index}.g.cs").Text;
            Assert.Contains(" " + vectors[index].GetProperty("symbol").GetString() + " { get; }", api, StringComparison.Ordinal);
            Assert.Contains(" SSecondservice { get; }", api, StringComparison.Ordinal);
            var compiled = new SunderRpcCSharpSourceCompiler().Compile(new("example.consumer", "1.0.0", [], References(), SupplementalSources: generated.Sources));
            Assert.True(compiled.Success, string.Join(Environment.NewLine, compiled.Diagnostics));
        }
        var selected = _lock with
        {
            PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact>
            {
                [artifact.PackageId] = SeedDescriptorOnlyArtifact(artifact with { Contracts = contracts, PublicContractCount = contracts.Count }),
            },
        };
        var combined = SunderRpcCSharpImportPrepass.Prepare(selected, _root);
        var compilation = new SunderRpcCSharpSourceCompiler().Compile(new("example.consumer", "1.0.0", [], References(), SupplementalSources: combined.Sources));
        Assert.True(compilation.Success, string.Join(Environment.NewLine, compilation.Diagnostics));
        var reversed = selected with
        {
            PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact>
            {
                [artifact.PackageId] = selected.PackageArtifacts[artifact.PackageId] with { Contracts = contracts.Reverse().ToDictionary() },
            },
        };
        Assert.Equal(combined.Sources, SunderRpcCSharpImportPrepass.Prepare(reversed, _root).Sources);

        var collidingJson = JsonNode.Parse(_descriptor.GetCanonicalUtf8())!;
        var collidingServices = collidingJson["services"]!.AsObject();
        var method = collidingServices["example"]!.DeepClone();
        collidingServices.Clear();
        collidingServices["same-service"] = method;
        collidingServices["same.service"] = method.DeepClone();
        var collidingDescriptor = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(collidingJson));
        File.WriteAllBytes(SunderRpcContractCache.GetDescriptorPath(_root, collidingDescriptor.Sha256), collidingDescriptor.GetCanonicalUtf8());
        File.WriteAllBytes(SunderRpcContractCache.GetReceiptPath(_root, collidingDescriptor.Sha256),
            SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1, contractId = collidingDescriptor.ContractId, contractVersion = collidingDescriptor.Version,
                descriptorBytes = collidingDescriptor.GetCanonicalUtf8().Length, descriptorSha256 = collidingDescriptor.Sha256, abiSha256 = collidingDescriptor.AbiSha256,
            })));
        var colliding = _lock with { PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact>
        {
            [artifact.PackageId] = SeedDescriptorOnlyArtifact(artifact with { Contracts = new Dictionary<string, SunderRpcLockedArtifactContract>
            {
                [collidingDescriptor.ContractId] = artifact.Contracts[_descriptor.ContractId] with
                {
                    DescriptorBytes = collidingDescriptor.GetCanonicalUtf8().Length, DescriptorSha256 = collidingDescriptor.Sha256,
                    AbiSha256 = collidingDescriptor.AbiSha256, Etag = "\"" + collidingDescriptor.Sha256 + "\"",
                },
            } }),
        } };
        var collision = Assert.Throws<InvalidDataException>(() => SunderRpcCSharpImportPrepass.Prepare(colliding, _root));
        Assert.StartsWith("rpc-authoring.generated-name-collision:", collision.Message, StringComparison.Ordinal);
        Assert.Contains("CExample_Rpc_V1_Minimal.ISameService", collision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionTask_ConsumesCachedImportsAndRejectsMissingCacheWithoutReplacingOutput()
    {
        File.Copy(Fixture("authoring/conformance/valid/minimal-package-owned-baseline.json"), Path.Combine(_root, "sunder-contracts.owned.json"));
        var lockPath = Path.Combine(_root, "sunder-contracts.lock.json");
        File.Copy(Fixture("authoring/conformance/valid/minimal-contract-lock.json"), lockPath);
        var intermediate = Path.Combine(_root, "obj");
        Directory.CreateDirectory(intermediate);
        GenerateSunderRpcBindingsTask CreateTask() => new()
        {
            BuildEngine = new BuildEngine(), ProjectDirectory = _root, IntermediateOutputPath = intermediate,
            PackageVersion = "1.0.0", ProjectIdentity = "Consumer", TargetFramework = "net10.0", Configuration = "Debug",
            ContractCacheRoot = _root,
            ReferencePaths = References().Select(path => (ITaskItem)new TaskItem(path)).ToArray(),
        };
        var initial = CreateTask();
        Assert.True(initial.Execute(), string.Join(Environment.NewLine, ((BuildEngine)initial.BuildEngine).Errors));
        var generated = Path.Combine(intermediate, "sunder-rpc");
        File.Copy(Fixture("authoring/conformance/valid/package-api-contract-lock.json"), lockPath, overwrite: true);
        var imported = CreateTask();
        Assert.True(imported.Execute(), string.Join(Environment.NewLine, ((BuildEngine)imported.BuildEngine).Errors));
        Assert.Single(imported.ContractBundles);
        Assert.Single(imported.RpcUses);
        Assert.Single(imported.AuthoringReceipts);
        var before = Directory.GetFiles(generated, "*", SearchOption.AllDirectories).Order()
            .Select(path => (path, File.ReadAllText(path), File.GetLastWriteTimeUtc(path))).ToArray();
        File.Delete(SunderRpcContractCache.GetDescriptorPath(_root, _descriptor.Sha256));
        var failed = CreateTask();
        Assert.False(failed.Execute());
        Assert.Contains(((BuildEngine)failed.BuildEngine).Errors, message => message.Contains("contract-cache.entry-missing", StringComparison.Ordinal));
        Assert.Empty(failed.CompileItems);
        Assert.Empty(failed.ContractBundles);
        Assert.Equal(before, Directory.GetFiles(generated, "*", SearchOption.AllDirectories).Order()
            .Select(path => (path, File.ReadAllText(path), File.GetLastWriteTimeUtc(path))).ToArray());
    }

    [Fact]
    public void Prepare_MissingCorruptAndPlatformEvidenceFailsWithoutPublishing()
    {
        var missingRoot = Path.Combine(_root, "missing");
        var missing = Assert.Throws<InvalidDataException>(() => SunderRpcCSharpImportPrepass.Prepare(_lock, missingRoot));
        Assert.StartsWith("contract-cache.entry-missing:", missing.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(missingRoot));

        var platform = SunderRpcAuthoringDocuments.ReadContractLock(Fixture("authoring/conformance/valid/mixed-contract-lock.json"));
        var unsupported = Assert.Throws<InvalidDataException>(() => SunderRpcCSharpImportPrepass.Prepare(platform, missingRoot));
        Assert.StartsWith("rpc-authoring.authority-mismatch:", unsupported.Message, StringComparison.Ordinal); // corpus range predates the coordinated tooling line
        var metadataOnly = SunderRpcAuthoringDocuments.ReadContractLock(Fixture("authoring/conformance/valid/package-api-mixed-authority-contract-lock.json"));
        var absentCatalog = Assert.Throws<InvalidDataException>(() => SunderRpcCSharpImportPrepass.Prepare(metadataOnly, _root));
        Assert.StartsWith("contract-cache.entry-missing:", absentCatalog.Message, StringComparison.Ordinal);

        File.AppendAllText(SunderRpcContractCache.GetDescriptorPath(_root, _descriptor.Sha256), "\n");
        var corrupt = Assert.Throws<InvalidDataException>(() => SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        Assert.StartsWith("contract-cache.entry-corrupt:", corrupt.Message, StringComparison.Ordinal);
        Assert.Equal(3, Directory.GetFiles(_root).Length);
        Assert.Equal(Path.Combine(_root, "model-projections"), Assert.Single(Directory.GetDirectories(_root)));

        var empty = SunderRpcAuthoringDocuments.ReadContractLock(Fixture("authoring/conformance/valid/minimal-contract-lock.json"));
        Assert.Empty(SunderRpcCSharpImportPrepass.Prepare(empty, missingRoot).Contracts);
    }

    [Fact]
    public async Task Resolver_UsesRangeAndExactRpcIdentityRatherThanRestoreVersionOrContent()
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var expected = Provider("1.9.9+new.build");
        var rpc = new RpcStub
        {
            Providers =
            [
                Provider("2.0.0"),
                Provider("1.9.9-beta.1"),
                Provider("1.9.9") with { PackageId = "example.extension" },
                Provider("1.9.9") with { ContractVersion = "2.0.1" },
                Provider("1.9.9") with { DescriptorSha256 = new string('a', 64) },
                Provider("1.9.9") with { AbiSha256 = new string('b', 64) },
                Provider("1.9.9") with { State = SunderRpcProviderState.Inactive },
                expected,
            ],
        };
        var actual = await probe.Resolve(rpc, CancellationToken.None);
        Assert.Equal(expected, actual);
        Assert.Equal(1, rpc.DiscoverCount);
        Assert.Equal(_descriptor.ContractId, rpc.DiscoveredContract);
        Assert.Equal(0, rpc.InvokeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolver_HonorsPrereleasePolicy(bool includePrerelease)
    {
        var artifacts = _lock.PackageArtifacts.ToDictionary();
        artifacts["example.dependency"] = artifacts["example.dependency"] with { IncludePrerelease = includePrerelease };
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock with { PackageArtifacts = artifacts }, _root));
        var rpc = new RpcStub { Providers = [Provider("1.8.0-beta.1")] };
        if (includePrerelease)
        {
            Assert.Equal(rpc.Providers[0], await probe.Resolve(rpc, CancellationToken.None));
        }
        else
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => probe.Resolve(rpc, CancellationToken.None));
            Assert.Equal("rpc.provider-not-found", error.Error.Code);
        }
    }

    [Theory]
    [InlineData(0, "rpc.provider-not-found")]
    [InlineData(2, "rpc.provider-ambiguous")]
    public async Task Resolver_DoesNotChooseOrInvokeOnZeroOrMultipleMatches(int count, string code)
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var rpc = new RpcStub { Providers = Enumerable.Range(0, count).Select(_ => Provider("1.2.3")).ToArray() };
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => probe.Resolve(rpc, CancellationToken.None));
        Assert.Equal(code, error.Error.Code);
        Assert.Equal(SunderRpcDispatchState.NotDispatched, error.Error.DispatchState);
        Assert.Equal(1, rpc.DiscoverCount);
        Assert.Equal(0, rpc.InvokeCount);
    }

    [Fact]
    public async Task Catalog_PreservesExtensionProvidersCursorsAndLifecycleEventsAndRevalidatesExactEndpoints()
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var extension = Provider("9.0.0") with { PackageId = "example.extension", State = SunderRpcProviderState.Inactive };
        var matching = Provider("1.2.3");
        var mismatched = Provider("1.2.3") with { ContractId = "example.other" };
        var rpc = new RpcStub
        {
            Providers = [extension, matching, mismatched],
            Events =
            [
                new(8, 11, SunderRpcCatalogEventKind.Removed, extension),
                new(9, 12, SunderRpcCatalogEventKind.Added, mismatched),
                new(10, 13, SunderRpcCatalogEventKind.Faulted, matching with { State = SunderRpcProviderState.Faulted }),
                new(11, 14, SunderRpcCatalogEventKind.ResetRequired, null),
            ],
        };
        var listed = await probe.List(rpc);
        Assert.Equal(new[] { extension, matching }, listed.Providers);
        Assert.Equal((7, 10, true), (listed.Revision, listed.Sequence, listed.ResetRequired));
        var events = await probe.Watch(rpc, CancellationToken.None);
        Assert.Equal(new long[] { 11, 13, 14 }, events.Select(item => item.Sequence));
        Assert.Equal(SunderRpcCatalogEventKind.Removed, events[0].Kind);
        Assert.Equal(SunderRpcProviderState.Faulted, events[1].Provider!.State);
        Assert.Null(events[2].Provider);
        Assert.Equal((7L, 10L), rpc.WatchCursor);

        rpc.Current = matching with { State = SunderRpcProviderState.Inactive, CatalogRevision = 12 };
        Assert.Equal(rpc.Current, await probe.Get(rpc, matching));
        Assert.Equal(matching.Endpoint, rpc.RequestedEndpoint);
        rpc.Current = matching with { SessionGeneration = matching.SessionGeneration + 1 };
        Assert.Equal(rpc.Current, await probe.Get(rpc, matching));
        rpc.Current = matching with { SessionGeneration = matching.SessionGeneration - 1 };
        Assert.Null(await probe.Get(rpc, matching));
        rpc.Current = matching with { ActivationId = Guid.NewGuid() };
        Assert.Null(await probe.Get(rpc, matching));
        rpc.Current = matching with { Endpoint = new("replacement") };
        Assert.Null(await probe.Get(rpc, matching));
    }

    [Fact]
    public async Task Client_UsesCallerScopeAndNeverRetriesOrReplacesEndpoints()
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var selected = Provider("1.2.3");
        var rpc = new RpcStub { Providers = [selected] };
        var scope = new RpcStub();
        await probe.Call(rpc, scope);
        Assert.Equal(selected.Endpoint, scope.RequestedEndpoint);
        Assert.Equal(1, scope.InvokeCount);
        Assert.Equal(0, rpc.InvokeCount);
        Assert.False(scope.Disposed);
        scope.Failure = new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "rpc.stale-endpoint", "retired"));
        Assert.Same(scope.Failure, await Assert.ThrowsAsync<SunderRpcException>(() => probe.Call(rpc, scope)));
        Assert.Equal(2, scope.InvokeCount);
        Assert.False(scope.Disposed);
    }

    [Fact]
    public async Task Client_ContentRegistrationIsBoundToTheValidatedProviderAndCallerScope()
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var selected = Provider("1.2.3");
        using var source = new MemoryStream([1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        var options = new SunderRpcContentRegistrationOptions("application/octet-stream", "test.bin", 3,
            Repeatability: SunderRpcContentRepeatability.SingleUse, MaximumUses: 1);
        var failure = new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "test.retired", "retired"));
        var calls = 0;
        var scope = new RpcStub
        {
            Register = (endpoint, stream, policy, token) =>
            {
                calls++;
                Assert.Equal(selected.Endpoint, endpoint);
                Assert.Same(source, stream);
                Assert.Equal(options, policy);
                Assert.Equal(cancellation.Token, token);
                token.ThrowIfCancellationRequested();
                return ValueTask.FromException<SunderRpcContentReference>(failure);
            },
        };
        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => probe.RegisterContent(scope, selected, source, options, cancellation.Token)));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.RegisterContent(scope, selected, source, options, cancellation.Token));
        Assert.Equal(2, calls);
        Assert.False(scope.Disposed);
        Assert.True(source.CanRead);
        await Assert.ThrowsAsync<SunderRpcException>(() => probe.RegisterContent(scope, selected with { ContractId = "other.contract" }, source, options, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Catalog_PreservesErrorsCancellationAndRejectsMalformedMetadata()
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var rpc = new RpcStub { Providers = [Provider("1.2.3") with { ProviderId = "INVALID" }] };
        Assert.Equal("rpc.binding.invalid-provider", (await Assert.ThrowsAsync<SunderRpcException>(() => probe.List(rpc))).Error.Code);
        rpc.Failure = new SunderRpcException(new(SunderRpcErrorKind.PermissionDenied, "rpc.permission-denied", "denied"));
        Assert.Same(rpc.Failure, await Assert.ThrowsAsync<SunderRpcException>(() => probe.Resolve(rpc, CancellationToken.None)));
        rpc.Failure = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.Resolve(rpc, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.Watch(rpc, cancellation.Token));
    }

    [Fact]
    public void Client_RejectsForeignContractAndMalformedSnapshotsFromTheSameAssembly()
    {
        using var probe = CompileProbe(SunderRpcCSharpImportPrepass.Prepare(_lock, _root));
        var rpc = new RpcStub();
        var provider = Provider("1.2.3");
        foreach (var invalid in new[]
                 {
                     provider with { ContractId = "example.other" },
                     provider with { ContractVersion = "2.0.1" },
                     provider with { DescriptorSha256 = new string('a', 64) },
                     provider with { AbiSha256 = new string('a', 64) },
                     provider with { ProviderId = "INVALID" },
                 })
        {
            Assert.Equal("rpc.binding.invalid-provider", Assert.Throws<SunderRpcException>(() => probe.CreateClient(rpc, invalid)).Error.Code);
        }
        Assert.Equal("rpc.stale-endpoint", Assert.Throws<SunderRpcException>(() =>
            probe.CreateClient(rpc, provider with { State = SunderRpcProviderState.Inactive })).Error.Code);
        Assert.Equal(0, rpc.InvokeCount);
    }

    private SunderRpcProviderSnapshot Provider(string version)
        => new("example.dependency", version, "example.provider", _descriptor.ContractId, _descriptor.Version,
            _descriptor.Sha256, _descriptor.AbiSha256, Guid.NewGuid(), 1, 1,
            new(Guid.NewGuid().ToString("N")), 7, SunderRpcProviderState.Active);

    private static SunderRpcContractLock WithProvider(SunderRpcContractLock value)
        => value with
        {
            ProviderImports = new Dictionary<string, SunderRpcLockedProviderImport>
            {
                ["minimalProvider"] = new("minimalProvider", "example.dependency", "example.rpc.v1.minimal"),
            },
        };

    private static string[] References()
        => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !Path.GetFileName(path).Equals("Sunder.Sdk.Worker.dll", StringComparison.Ordinal))
            .Append(typeof(SunderRpcContractDescriptor).Assembly.Location).Distinct(StringComparer.Ordinal).ToArray();

    private static Probe CompileProbe(SunderRpcCSharpImportPrepassResult prepass)
    {
        var sources = prepass.Sources.Append(new SunderRpcCSharpSourceFile("Probe.cs", """
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;
            using System.Collections.Generic;
            using Sunder.Sdk.Rpc;
            using Sunder.Generated.Packages.Example_Dependency_Internal;
            using Sunder.Generated.Packages.Example_Dependency_Internal.CExample_Rpc_V1_Minimal.Api;
            using Empty = Sunder.Generated.Packages.Example_Dependency_Models.CExample_Rpc_V1_Minimal_Empty;
            public static class Probe
            {
                public static async Task<SunderRpcProviderSnapshot> Resolve(ISunderRpcClient rpc, CancellationToken cancellationToken)
                    => (await new DependencyPackageApi(rpc).CExample_Rpc_V1_Minimal.ResolveSelectedPackageAsync(cancellationToken)).Snapshot;
                public static async Task<SunderRpcCatalogSnapshot> List(ISunderRpcClient rpc)
                {
                    var catalog = await new DependencyPackageApi(rpc).CExample_Rpc_V1_Minimal.ListAsync();
                    return new(catalog.Revision, catalog.Sequence, catalog.Providers.Select(provider => provider.Snapshot), catalog.ResetRequired);
                }
                public static async Task<SunderRpcProviderSnapshot?> Get(ISunderRpcClient rpc, SunderRpcProviderSnapshot previous)
                    => (await new DependencyPackageApi(rpc).CExample_Rpc_V1_Minimal.GetAsync(new Provider(previous)))?.Snapshot;
                public static async Task<SunderRpcCatalogEvent[]> Watch(ISunderRpcClient rpc, CancellationToken cancellationToken)
                {
                    var events = new List<SunderRpcCatalogEvent>();
                    await foreach (var item in new DependencyPackageApi(rpc).CExample_Rpc_V1_Minimal.WatchAsync(7, 10, cancellationToken))
                        events.Add(new(item.Revision, item.Sequence, item.Kind, item.Provider?.Snapshot));
                    return events.ToArray();
                }
                public static async Task Call(ISunderRpcClient rpc, ISunderRpcCallScope scope)
                {
                    var api = new DependencyPackageApi(rpc).CExample_Rpc_V1_Minimal;
                    var provider = await api.ResolveSelectedPackageAsync();
                    await api.CreateClient(scope, provider).SExample.PingAsync(new Empty());
                }
                public static void CreateClient(ISunderRpcCallScope scope, SunderRpcProviderSnapshot snapshot)
                    => new DependencyPackageApi(scope).CExample_Rpc_V1_Minimal.CreateClient(scope, new Provider(snapshot));
                public static async Task<SunderRpcContentReference> RegisterContent(ISunderRpcCallScope scope, SunderRpcProviderSnapshot snapshot,
                    System.IO.Stream source, SunderRpcContentRegistrationOptions options, CancellationToken token)
                    => await new DependencyPackageApi(scope).CExample_Rpc_V1_Minimal.CreateClient(scope, new Provider(snapshot)).RegisterContentAsync(source, options, token);
                public static object CreateNaturalCatalog(ISunderRpcClient rpc)
                    => new Sunder.Generated.Packages.Example_Dependency_Internal.CExample_Rpc_V1_Minimal_ExampleServiceClientProviders(rpc);
                public static async Task<object> ListNatural(object catalog, CancellationToken token)
                    => await ((Sunder.Sdk.Services.IServiceProviders<Sunder.Generated.Packages.Example_Dependency_Services.CExample_Rpc_V1_Minimal.IExampleService>)catalog).ListAsync(token);
                public static async Task CallNatural(object provider, CancellationToken token)
                    => await ((Sunder.Sdk.Services.ServiceProvider<Sunder.Generated.Packages.Example_Dependency_Services.CExample_Rpc_V1_Minimal.IExampleService>)provider).Service.PingAsync(token);
                public static async Task<object?> BorrowNatural(object catalog, CancellationToken token)
                {
                    var snapshot = await ((Sunder.Sdk.Services.IServiceProviders<Sunder.Generated.Packages.Example_Dependency_Services.CExample_Rpc_V1_Minimal.IExampleService>)catalog).ListAsync(token);
                    foreach (var provider in snapshot.Providers.Where(value => value.State == Sunder.Sdk.Services.ServiceProviderState.Active))
                    {
                        if (provider.RetirementToken.IsCancellationRequested) continue;
                        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, provider.RetirementToken);
                        try { await provider.EnsureCurrentAsync(invocation.Token); }
                        catch (SunderRpcException error) when (error.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable) { continue; }
                        await provider.Service.PingAsync(invocation.Token);
                        await provider.EnsureCurrentAsync(invocation.Token);
                        return provider;
                    }
                    return null;
                }
                public static async Task<object[]> WatchNatural(object catalog, long revision, long sequence, CancellationToken token)
                {
                    var result = new List<object>();
                    await foreach (var item in ((Sunder.Sdk.Services.IServiceProviders<Sunder.Generated.Packages.Example_Dependency_Services.CExample_Rpc_V1_Minimal.IExampleService>)catalog).WatchAsync(revision, sequence, token)) result.Add(item);
                    return result.ToArray();
                }
            }
            """));
        var compilation = CSharpCompilation.Create("Prepass_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source.Text, new CSharpParseOptions(LanguageVersion.CSharp14), source.Path)),
            References().Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        stream.Position = 0;
        var context = new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true);
        return new(context, context.LoadFromStream(stream));
    }

    private sealed class Probe(AssemblyLoadContext context, Assembly assembly) : IDisposable
    {
        public Assembly Assembly { get; } = assembly;
        public Func<ISunderRpcClient, object> CreateNaturalCatalog { get; } = assembly.GetType("Probe")!.GetMethod("CreateNaturalCatalog")!.CreateDelegate<Func<ISunderRpcClient, object>>();
        public Func<object, CancellationToken, Task<object>> ListNatural { get; } = assembly.GetType("Probe")!.GetMethod("ListNatural")!.CreateDelegate<Func<object, CancellationToken, Task<object>>>();
        public Func<object, CancellationToken, Task> CallNatural { get; } = assembly.GetType("Probe")!.GetMethod("CallNatural")!.CreateDelegate<Func<object, CancellationToken, Task>>();
        public Func<object, CancellationToken, Task<object?>> BorrowNatural { get; } = assembly.GetType("Probe")!.GetMethod("BorrowNatural")!.CreateDelegate<Func<object, CancellationToken, Task<object?>>>();
        public Func<object, long, long, CancellationToken, Task<object[]>> WatchNatural { get; } = assembly.GetType("Probe")!.GetMethod("WatchNatural")!.CreateDelegate<Func<object, long, long, CancellationToken, Task<object[]>>>();
        public Func<ISunderRpcClient, CancellationToken, Task<SunderRpcProviderSnapshot>> Resolve { get; } =
            assembly.GetType("Probe")!.GetMethod("Resolve")!.CreateDelegate<Func<ISunderRpcClient, CancellationToken, Task<SunderRpcProviderSnapshot>>>();
        public Func<ISunderRpcClient, Task<SunderRpcCatalogSnapshot>> List { get; } =
            assembly.GetType("Probe")!.GetMethod("List")!.CreateDelegate<Func<ISunderRpcClient, Task<SunderRpcCatalogSnapshot>>>();
        public Func<ISunderRpcClient, SunderRpcProviderSnapshot, Task<SunderRpcProviderSnapshot?>> Get { get; } =
            assembly.GetType("Probe")!.GetMethod("Get")!.CreateDelegate<Func<ISunderRpcClient, SunderRpcProviderSnapshot, Task<SunderRpcProviderSnapshot?>>>();
        public Func<ISunderRpcClient, CancellationToken, Task<SunderRpcCatalogEvent[]>> Watch { get; } =
            assembly.GetType("Probe")!.GetMethod("Watch")!.CreateDelegate<Func<ISunderRpcClient, CancellationToken, Task<SunderRpcCatalogEvent[]>>>();
        public Func<ISunderRpcClient, ISunderRpcCallScope, Task> Call { get; } =
            assembly.GetType("Probe")!.GetMethod("Call")!.CreateDelegate<Func<ISunderRpcClient, ISunderRpcCallScope, Task>>();
        public Action<ISunderRpcCallScope, SunderRpcProviderSnapshot> CreateClient { get; } =
            assembly.GetType("Probe")!.GetMethod("CreateClient")!.CreateDelegate<Action<ISunderRpcCallScope, SunderRpcProviderSnapshot>>();
        public Func<ISunderRpcCallScope, SunderRpcProviderSnapshot, Stream, SunderRpcContentRegistrationOptions, CancellationToken, Task<SunderRpcContentReference>> RegisterContent { get; } =
            assembly.GetType("Probe")!.GetMethod("RegisterContent")!.CreateDelegate<Func<ISunderRpcCallScope, SunderRpcProviderSnapshot, Stream, SunderRpcContentRegistrationOptions, CancellationToken, Task<SunderRpcContentReference>>>();
        public void Dispose() => context.Unload();
    }

    private sealed class RpcStub : ISunderRpcCallScope
    {
        public SunderRpcProviderSnapshot[] Providers { get; set; } = [];
        public SunderRpcCatalogEvent[] Events { get; init; } = [];
        // Ordinary clients share a live receiver observer. Raw transport facade
        // tests deliberately keep their finite, reset-marked catalog fixture.
        public bool LiveCatalog { get; init; }
        public System.Collections.Concurrent.ConcurrentQueue<(long Revision, long Sequence)> WatchCursors { get; } = new();
        public SunderRpcProviderSnapshot? Current { get; set; }
        public SunderRpcException? Failure { get; set; }
        public SunderRpcException? LookupFailure { get; set; }
        public int LookupCount { get; private set; }
        public string? DiscoveredContract { get; private set; }
        public SunderRpcEndpointReference? RequestedEndpoint { get; private set; }
        public int DiscoverCount { get; private set; }
        public int InvokeCount { get; private set; }
        public bool Disposed { get; private set; }
        public int ScopeCount { get; private set; }
        public Func<CancellationToken, ValueTask<ISunderRpcCallScope>>? ScopeFactory { get; set; }
        public Func<CancellationToken, ValueTask<JsonElement>>? Invoke { get; set; }
        public Func<CancellationToken, IAsyncEnumerable<JsonElement>>? Subscribe { get; set; }
        public Func<SunderRpcEndpointReference, Stream, SunderRpcContentRegistrationOptions, CancellationToken, ValueTask<SunderRpcContentReference>>? Register { get; init; }
        public (long, long) WatchCursor { get; private set; }
        public DateTimeOffset DeadlineUtc => DateTimeOffset.MaxValue;

        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default)
        {
            DiscoverCount++;
            DiscoveredContract = contractId;
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult(new SunderRpcCatalogSnapshot(7, 10, Providers, resetRequired: !LiveCatalog));
        }
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        {
            RequestedEndpoint = endpoint;
            LookupCount++;
            if (LookupFailure is { } failure) throw failure;
            return ValueTask.FromResult(Current);
        }
        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WatchCursor = (afterRevision, afterSequence);
            WatchCursors.Enqueue((afterRevision, afterSequence));
            cancellationToken.ThrowIfCancellationRequested();
            if (LiveCatalog && (afterRevision, afterSequence) == (7, 10))
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            await Task.CompletedTask;
            foreach (var item in Events) yield return item;
        }
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            InvokeCount++;
            RequestedEndpoint = endpoint;
            Assert.Equal("example", serviceId);
            Assert.Equal("ping", methodId);
            if (Invoke is not null) return Invoke(cancellationToken);
            if (Failure is not null) throw Failure;
            return ValueTask.FromResult(request.Clone());
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); ScopeCount++; return ScopeFactory?.Invoke(cancellationToken) ?? throw new NotSupportedException(); }
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => Subscribe?.Invoke(cancellationToken) ?? throw new NotSupportedException();
        public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
            => Register?.Invoke(endpoint, source, options, cancellationToken) ?? throw new NotSupportedException();
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static string Fixture(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, "specs/rpc/v1", path);
    }

    private sealed class BuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message!);
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, System.Collections.IDictionary globalProperties, System.Collections.IDictionary targetOutputs) => throw new NotSupportedException();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
