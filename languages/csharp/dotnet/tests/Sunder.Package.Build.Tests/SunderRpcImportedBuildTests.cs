using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using Xunit;
using TaskItem = Microsoft.Build.Utilities.TaskItem;

namespace Sunder.Package.Build.Tests;

[Collection(nameof(TaskShadowStagingTests))]
public sealed partial class SunderRpcImportedBuildTests : IDisposable
{
    private const string ContractId = "example.rpc.v1.minimal";
    private const string DependencyId = "example.dependency";
    private const string WorkerSource = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Packaging;
        using Sunder.Sdk.Worker;
        [assembly: SunderPackage(Id = "example.owner", Name = "Imported Fixture")]
        public static class Program
        {
            public static Task Main() => SunderWorker.RunAsync(_ => new SunderWorkerOptions([]));
        }
        """;
    private const string ConsumerSource = """
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        using Sunder.Generated.Packages.Example_Dependency_Internal;
        public static class Consumer
        {
            public static object Create(ISunderRpcClient rpc) => new DependencyPackageApi(rpc).CExample_Rpc_V1_Minimal;
        }
        """;
    private const string ProviderSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        using Sunder.Generated.Packages.Example_Dependency_Services;
        [SunderServiceProvider(typeof(IMinimalProviderService), ProviderId = "example.owner.minimal.provider")]
        public sealed partial class LocalProvider : IMinimalProviderService
        {
            public ValueTask PingAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        }
        public static class ProviderRegistrationProbe
        {
            public static object Create() => new LocalProvider().CreateSunderRpcProviderRegistration();
        }
        """;
    private const string PlatformProviderSource = """
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Services;
        using Sunder.Generated.Rpc;
        using Sunder.Generated.Rpc.Imports.CExample_Rpc_V1_Minimal;
        [SunderServiceProvider(typeof(IMinimalProviderService), ProviderId = "example.owner.minimal.provider")]
        public sealed partial class LocalProvider : IMinimalProviderService
        {
            public ValueTask<Empty> PingAsync(SunderRpcInvocationContext context, Empty request, CancellationToken cancellationToken)
                => ValueTask.FromResult(request);
        }
        public static class ProviderRegistrationProbe
        {
            public static object Create() => new LocalProvider().CreateSunderRpcProviderRegistration();
        }
        """;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _root;
    private readonly string _cache;
    private readonly SunderRpcContractDescriptor _descriptor;

    public SunderRpcImportedBuildTests()
    {
        _root = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(
            Path.Combine(Path.GetTempPath(), "sunder-rpc-imported-build", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_root);
        _cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(_cache);
        SunderRpcContractCacheStore.InitializeRoot(_cache);
        _descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(Fixture("conformance/valid/minimal.rpc.json")));
        File.WriteAllBytes(SunderRpcContractCache.GetDescriptorPath(_cache, _descriptor.Sha256), _descriptor.GetCanonicalUtf8());
        File.Copy(Fixture("authoring/conformance/valid/cache-entry-receipt.json"), SunderRpcContractCache.GetReceiptPath(_cache, _descriptor.Sha256));
        File.Copy(Fixture("authoring/conformance/valid/minimal-package-owned-baseline.json"), Path.Combine(_root, SunderRpcAuthoringDocuments.OwnedContractsFileName));
        File.Copy(Fixture("authoring/conformance/valid/package-api-contract-lock.json"), LockPath);
        ModelProjectionTestEvidence.Seed(_cache, SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId]);
        File.WriteAllText(Path.Combine(_root, "Program.cs"), WorkerSource);
        File.WriteAllText(Path.Combine(_root, "Consumer.cs"), ConsumerSource);
    }

    [Fact]
    public async Task CachedConsumer_FirstGenerationCompilesWithoutOwnedContractsAndProducesValidManifest()
    {
        Assert.Equal(SunderRpcContractCache.DefaultRoot, new GenerateSunderRpcBindingsTask().ContractCacheRoot);
        var cacheBefore = Snapshot(_cache);
        var generation = Generate();
        Assert.Single(generation.BindingSnapshots);
        Assert.Single(generation.ContractBundles);
        Assert.Single(generation.RpcUses);
        Assert.Empty(generation.RpcProviders);
        var receiptItem = Assert.Single(generation.AuthoringReceipts);
        Assert.Equal(Sha256(File.ReadAllBytes(receiptItem.ItemSpec)), receiptItem.GetMetadata("Sha256"));
        Assert.Equal(Sha256(File.ReadAllBytes(LockPath)), receiptItem.GetMetadata("LockSha256"));
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptItem.ItemSpec));
        using var export = JsonDocument.Parse(File.ReadAllBytes(Assert.Single(generation.PackageProjectExports).ItemSpec));
        Assert.Empty(export.RootElement.GetProperty("contracts").EnumerateArray());
        Assert.Equal(ContractId, Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray()).GetProperty("contractId").GetString());

        var task = BuildManifest(generation);
        var manifest = ReadManifest(task.ManifestOutputPath);
        var dependency = Assert.Single(manifest.DependsOn!);
        Assert.Equal((DependencyId, ">=1.0.0 <2.0.0", "required"), (dependency.PackageId, dependency.VersionRange, dependency.Presence));
        var bundle = Assert.Single(manifest.ContractBundles!)!;
        Assert.Equal(("imported", "package", DependencyId), (bundle.Source, bundle.AuthorityKind, bundle.AuthorityId));
        Assert.Equal("1.2.3", bundle.SelectedAuthorityRelease);
        Assert.Equal(_descriptor.Sha256, bundle.DescriptorSha256);
        Assert.Equal(_descriptor.AbiSha256, bundle.AbiSha256);
        Assert.Equal(new[] { "discover", "invoke" }, Assert.Single(manifest.UsesContracts!)!.Actions);
        Assert.Single(Assert.Single(manifest.PackageApiImports!)!.UseRefs!);
        Assert.Empty(manifest.ProviderImports!);
        Assert.Empty(manifest.PlatformImports!);
        var leaf = EmitLeaf(task);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(leaf);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Assert.Equal(_descriptor.GetCanonicalUtf8(), File.ReadAllBytes(Path.Combine(leaf, "payload/shared", bundle.DescriptorPath!)));
        Assert.Equal(cacheBefore, Snapshot(_cache));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ImportedProvider_CompilesRegistrationAndCoalescesOnlyExplicitConsumerGrants(bool consumer, bool contextBearing)
    {
        var contractLock = ReadJson(LockPath);
        contractLock["providerImports"]!["minimalProvider"] = new JsonObject { ["packageId"] = DependencyId, ["contractId"] = ContractId };
        if (!consumer)
        {
            contractLock["packageImports"]!.AsObject().Clear();
            File.Delete(Path.Combine(_root, "Consumer.cs"));
        }
        else contractLock["packageImports"]![DependencyId]!["presence"] = "optional";
        WriteJson(LockPath, contractLock);
        var providerSource = contextBearing ? ProviderSource.Replace(" : IMinimalProviderService", "", StringComparison.Ordinal)
            .Replace("public ValueTask PingAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;", """
                public string? Caller { get; private set; }
                public ValueTask PingAsync(global::Sunder.Sdk.Rpc.SunderRpcInvocationContext context)
                { Caller = context.CallerPackageId; return ValueTask.CompletedTask; }
                """, StringComparison.Ordinal) : ProviderSource;
        File.WriteAllText(Path.Combine(_root, "Provider.cs"), providerSource);
        File.WriteAllText(Path.Combine(_root, "Program.cs"), WorkerSource.Replace("new SunderWorkerOptions([])",
            "new SunderWorkerOptions([new LocalProvider().CreateSunderRpcProviderRegistration()])", StringComparison.Ordinal));
        var generation = Generate();
        Assert.Single(generation.ContractBundles);
        Assert.Single(generation.BindingSnapshots);
        Assert.Single(generation.RpcProviders);
        Assert.Equal(consumer ? 1 : 0, generation.RpcUses.Length);
        var task = BuildManifest(generation);
        var manifest = ReadManifest(task.ManifestOutputPath);
        Assert.Equal("required", Assert.Single(manifest.DependsOn!).Presence);
        Assert.Equal(consumer ? 1 : 0, manifest.UsesContracts!.Count);
        Assert.Single(Assert.Single(manifest.ProviderImports!)!.ProviderRefs!);
        Assert.Equal(consumer ? 1 : 0, manifest.PackageApiImports!.Count);
        if (consumer) Assert.Equal("optional", Assert.Single(manifest.PackageApiImports!)!.Presence);
        var context = new AssemblyLoadContext("imported-provider-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var stream = File.OpenRead(task.TargetAssemblyPath);
            var assembly = context.LoadFromStream(stream);
            var registration = Assert.IsType<SunderWorkerProviderRegistration>(assembly.GetType("ProviderRegistrationProbe")!
                .GetMethod("Create")!.Invoke(null, null));
            var provider = Assert.Single(manifest.Provides!)!;
            Assert.Equal(provider.ProviderId, registration.ProviderId);
            Assert.Equal(provider.ContractId, registration.ContractId);
            Assert.Equal(provider.ContractVersion, registration.ContractVersion);
            Assert.Equal(provider.DescriptorSha256, registration.DescriptorSha256);
            Assert.Equal(provider.AbiSha256, registration.AbiSha256);
            var local = Activator.CreateInstance(assembly.GetType("LocalProvider")!)!;
            var bound = (SunderWorkerProviderRegistration)local.GetType().GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(local, null)!;
            var call = new StackContributorSourceParityTests.ContentHost().Context(new("example.owner", "1.0.0", bound.ProviderId,
                bound.ContractId, bound.ContractVersion, bound.DescriptorSha256, bound.AbiSha256, Guid.NewGuid(), 1, 1,
                new("exact-imported-endpoint"), 1, SunderRpcProviderState.Active));
            var reply = await bound.Handler.InvokeUnaryAsync(call, "example", "ping", JsonSerializer.SerializeToElement(new { }));
            Assert.Empty(reply.EnumerateObject());
            if (contextBearing) Assert.Equal("caller.package", local.GetType().GetProperty("Caller")!.GetValue(local));
        }
        finally { context.Unload(); }
    }

    [Fact]
    public async Task MixedInventory_RetainsPlatformConstraintsButNeverMaterializesPlatformBindingsOrUses()
    {
        var platform = PlatformFixture();
        File.Copy(Fixture("authoring/conformance/valid/package-api-mixed-authority-contract-lock.json"), LockPath, overwrite: true);
        ModelProjectionTestEvidence.Seed(_cache, SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId]);
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Offline catalog hydration must not use HTTP.")));
        Assert.Equal(1, await new SunderRpcContractRestore(http).RestoreAsync(LockPath, _cache, [], platformCatalog: platform.Path));
        var generation = CreateGeneration();
        AssertSuccess(generation);
        Assert.Single(generation.ContractBundles);
        Assert.Single(generation.RpcUses);
        var task = BuildManifest(generation);
        var manifest = ReadManifest(task.ManifestOutputPath);
        var artifact = Assert.Single(manifest.PackageArtifactImports!)!;
        Assert.Equal(2, artifact.PublicContractCount);
        Assert.Contains(artifact.AuthorityConstraints!, constraint => constraint!.AuthorityKind == "platform" && constraint.SelectedAuthorityContentIdentity == platform.Requirement.ContentIdentity);
        Assert.DoesNotContain(manifest.ContractBundles!, bundle => bundle!.AuthorityKind == "platform");
        Assert.Empty(manifest.PlatformImports!);
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Assert.Single(generation.AuthoringReceipts).ItemSpec));
        Assert.Equal(2, Assert.Single(receipt.RootElement.GetProperty("packageArtifacts").EnumerateArray()).GetProperty("contracts").GetArrayLength());
        Assert.Equal(5, Directory.GetFiles(_cache).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedOwnedErrorConstantShadowing_RejectsChangedProviderProjectionBeforePublication(bool audienceOnly)
    {
        var previous = Generate();
        var before = Snapshot(previous.IntermediateOutputPath);
        var lockDocument = ReadJson(LockPath);
        lockDocument["providerImports"]!["minimalProvider"] = new JsonObject { ["packageId"] = DependencyId, ["contractId"] = ContractId };
        WriteJson(LockPath, lockDocument);
        var providerMetadata = audienceOnly
            ? "ProviderId = \"example.before\", Audience = ExampleAfterRpcError.Code == \"example.after\" ? SunderServiceAudience.Owner : SunderServiceAudience.Public"
            : "ProviderId = ExampleAfterRpcError.Code";
        File.WriteAllText(Path.Combine(_root, "Shadowing.cs"), $$"""
            using Legacy;
            using System.Threading;
            using System.Threading.Tasks;
            using Sunder.Sdk.Rpc;
            using Sunder.Sdk.Services;
            namespace Sunder.Generated.Packages.Example_Owner_Internal.CExample_Owner_Imports { }
            namespace Legacy
            {
                public static class ExampleAfterRpcError
                {
                    public const string Code = "example.before";
                }
            }
            namespace Owned
            {
                [SunderService(SunderServiceExposure.Public)]
                [SunderServiceError("example.after")]
                public sealed partial class ImportsService
                {
                    [SunderMethod(Errors = new[] { "example.after" })]
                    public ValueTask<Reply> ReadAsync() => ValueTask.FromResult(new Reply { Value = "ok" });
                }
                public sealed record Reply
                {
                    public required string Value { get; init; }
                }
            }
            namespace ImportedImplementation
            {
                using Sunder.Generated.Packages.Example_Owner_Internal;
                using Sunder.Generated.Packages.Example_Owner_Internal.CExample_Owner_Imports;
                using Sunder.Generated.Packages.Example_Dependency_Services;
                [SunderServiceProvider(typeof(IMinimalProviderService), {{providerMetadata}})]
                public sealed partial class LocalProvider : IMinimalProviderService
                {
                    public ValueTask PingAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
                }
            }
            """);
        var task = CreateGeneration();
        var contractLock = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        var imports = SunderRpcCSharpImportPrepass.Prepare(contractLock, _cache);
        var input = new SunderRpcCSharpCompilerInput("example.owner", "1.0.0",
            task.SourceFiles.Select(item => new SunderRpcCSharpSourceFile(item.ItemSpec, File.ReadAllText(item.ItemSpec))).ToArray(),
            task.ReferencePaths.Select(static item => item.ItemSpec).ToArray(),
            SupplementalSources: imports.Sources,
            CompilationOutputKind: SunderRpcCSharpCompilationOutputKind.ConsoleApplication,
            ImportedProviderSurfaces: imports.ProviderSurfaces);
        var compiler = new SunderRpcCSharpSourceCompiler();
        var candidate = compiler.Compile(input);
        Assert.True(candidate.Success, string.Join(Environment.NewLine, candidate.Diagnostics));
        var owned = Assert.Single(candidate.Contracts);
        Assert.Equal("imports", owned.ContractKey);
        Assert.Equal("Sunder.Generated.Packages.Example_Owner_Internal.CExample_Owner_Imports", owned.GeneratedLogicalName);
        var descriptor = owned.Descriptor;
        var baselinePath = Path.Combine(_root, SunderRpcAuthoringDocuments.OwnedContractsFileName);
        var baselineDocument = ReadJson(baselinePath);
        baselineDocument["contracts"]![owned.ContractKey] = new JsonObject
        {
            ["contractId"] = descriptor.ContractId,
            ["current"] = new JsonObject
            {
                ["state"] = "draft", ["exposure"] = "public", ["contractVersion"] = descriptor.Version,
                ["descriptorBytes"] = descriptor.GetCanonicalUtf8().Length,
                ["canonicalDescriptor"] = Encoding.UTF8.GetString(descriptor.GetCanonicalUtf8()),
                ["descriptorSha256"] = descriptor.Sha256,
                ["abiProjectionBytes"] = descriptor.GetCanonicalAbiProjectionUtf8().Length,
                ["canonicalAbiProjection"] = Encoding.UTF8.GetString(descriptor.GetCanonicalAbiProjectionUtf8()),
                ["abiSha256"] = descriptor.AbiSha256,
            },
            ["history"] = new JsonArray(),
            // This temporary draft has exact compiled source; review it explicitly
            // rather than treating descriptor-only evidence as a source API review.
            ["projectionReviews"] = new JsonArray(JsonNode.Parse(SunderRpcAuthoringDocuments
                .CreateProjectionReview(descriptor, owned.ModelProjection, owned.MethodPolicies).ReviewJson.GetRawText())),
        };
        WriteJson(baselinePath, baselineDocument);
        var baseline = SunderRpcAuthoringDocuments.ReadOwnedPackageBaseline(baselinePath);
        input = input with { OwnedContractsByContractKey = SunderRpcAuthoringDocuments.CompilerIdentities(baseline) };
        var accepted = compiler.Compile(input);
        Assert.True(accepted.Success, string.Join(Environment.NewLine, accepted.Diagnostics));
        var authoring = SunderRpcCSharpImplementationEmitter.Emit(accepted.Contracts, accepted.ImportedProviders);
        var ownedBinding = SunderRpcCSharpGenerator.Generate(descriptor, owned.GeneratedLogicalName);
        Assert.Contains("static class ExampleAfterRpcError", ownedBinding, StringComparison.Ordinal);
        Assert.Contains("public const string Code = \"example.after\";", ownedBinding, StringComparison.Ordinal);
        var verified = compiler.Compile(input with
        {
            SupplementalSources = [.. imports.Sources, new("bindings/imports.g.cs", ownedBinding), new("authoring.g.cs", authoring)],
            RequireGeneratedMembersResolved = true,
        });
        Assert.True(verified.Success, string.Join(Environment.NewLine, verified.Diagnostics));
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, accepted.Contracts);
        SunderRpcAuthoringDocuments.ValidateCompiledContracts(baseline, verified.Contracts);
        var acceptedProvider = Assert.Single(accepted.ImportedProviders).Provider;
        var verifiedProvider = Assert.Single(verified.ImportedProviders).Provider;
        Assert.Equal("example.before", acceptedProvider.ProviderId);
        Assert.Equal(audienceOnly ? "example.before" : "example.after", verifiedProvider.ProviderId);
        Assert.Equal(SunderServiceAudience.Public, acceptedProvider.Audience);
        Assert.Equal(audienceOnly ? SunderServiceAudience.Owner : SunderServiceAudience.Public, verifiedProvider.Audience);
        Assert.Equal(audienceOnly, authoring == SunderRpcCSharpImplementationEmitter.Emit(verified.Contracts, verified.ImportedProviders));
        var fingerprint = SunderRpcAuthoringDocuments.ComputeInputFingerprint("example.owner", "1.0.0",
            task.ProjectIdentity, task.Configuration, task.TargetFramework, task.NullableEnabled, [], input.Sources, input.ReferencePaths, baseline, contractLock);
        var acceptedReceipt = SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.0.0", fingerprint, contractLock, "example.owner", "1.0.0", accepted, imports);
        var verifiedReceipt = SunderRpcAuthoringDocuments.WriteAuthoringReceipt("1.0.0", fingerprint, contractLock, "example.owner", "1.0.0", verified, imports);
        Assert.False(acceptedReceipt.AsSpan().SequenceEqual(verifiedReceipt));
        _ = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(acceptedReceipt, contractLock.Sha256, "example.owner", "1.0.0");
        _ = SunderRpcAuthoringDocuments.ParseAuthoringReceiptProjection(verifiedReceipt, contractLock.Sha256, "example.owner", "1.0.0");

        Assert.False(task.Execute());
        Assert.Contains(((BuildEngine)task.BuildEngine).Errors, message =>
            message.Contains("rpc-authoring.evidence-mismatch", StringComparison.Ordinal)
            && message.Contains(audienceOnly ? "authoring receipt" : "authoring.g.cs", StringComparison.Ordinal));
        Assert.Empty(task.CompileItems);
        Assert.Empty(task.FileWrites);
        Assert.Empty(task.BindingSnapshots);
        Assert.Empty(task.ContractBundles);
        Assert.Empty(task.RpcProviders);
        Assert.Empty(task.RpcUses);
        Assert.Empty(task.PackageProjectExports);
        Assert.Empty(task.AuthoringReceipts);
        Assert.Equal(before, Snapshot(previous.IntermediateOutputPath));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(previous.IntermediateOutputPath),
            path => Path.GetFileName(path).Contains(".stage-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("corrupt-receipt")]
    [InlineData("source")]
    [InlineData("platform")]
    public void FailedGeneration_PreservesPreviousCompleteOutputsWithoutRestoring(string failure)
    {
        var generation = Generate();
        var manifest = BuildManifest(generation);
        var before = Snapshot(generation.IntermediateOutputPath);
        var cacheBefore = Snapshot(_cache);
        var failed = CreateGeneration();
        switch (failure)
        {
            case "missing": failed.ContractCacheRoot = Path.Combine(_root, "absent-cache"); break;
            case "corrupt": File.AppendAllText(SunderRpcContractCache.GetDescriptorPath(_cache, _descriptor.Sha256), "\n"); break;
            case "corrupt-receipt": File.AppendAllText(SunderRpcContractCache.GetReceiptPath(_cache, _descriptor.Sha256), "\n"); break;
            case "source": File.WriteAllText(Path.Combine(_root, "Consumer.cs"), ConsumerSource.Replace("DependencyPackageApi", "MissingPackageApi", StringComparison.Ordinal)); break;
            case "platform": File.Copy(Fixture("authoring/conformance/valid/mixed-contract-lock.json"), LockPath, overwrite: true); break;
        }
        Assert.False(failed.Execute());
        Assert.NotEmpty(((BuildEngine)failed.BuildEngine).Errors);
        Assert.Empty(failed.CompileItems);
        Assert.Empty(failed.ContractBundles);
        Assert.Empty(failed.AuthoringReceipts);
        Assert.Equal(before, Snapshot(generation.IntermediateOutputPath));
        Assert.True(File.Exists(manifest.TargetAssemblyPath));
        Assert.False(Directory.Exists(Path.Combine(_root, "absent-cache")));
        if (failure is not ("corrupt" or "corrupt-receipt")) Assert.Equal(cacheBefore, Snapshot(_cache));
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("lock")]
    [InlineData("root")]
    [InlineData("root-missing")]
    [InlineData("identity")]
    [InlineData("descriptor")]
    [InlineData("descriptor-field")]
    [InlineData("marker")]
    [InlineData("lease")]
    [InlineData("missing-receipt")]
    [InlineData("changed-use")]
    [InlineData("snapshot")]
    public void ReceiptTampering_CannotReplaceManifestOrCanonicalDescriptors(string failure)
    {
        var generation = Generate();
        var initial = BuildManifest(generation);
        var manifestBefore = File.ReadAllBytes(initial.ManifestOutputPath);
        var descriptorsBefore = Snapshot(Path.Combine(generation.IntermediateOutputPath, "sunder-contracts"));
        var task = CreateManifest(generation, initial.TargetAssemblyPath);
        var item = Assert.Single(task.AuthoringReceipts);
        var receipt = ReadJson(item.ItemSpec);
        switch (failure)
        {
            case "hash": receipt["inputFingerprint"] = new string('b', 64); break;
            case "lock": item.SetMetadata("LockSha256", new string('b', 64)); break;
            case "root": receipt["authoredAuthority"] = "example.other"; break;
            case "root-missing": receipt.Remove("packageArtifactImports"); break;
            case "identity": item.SetMetadata("PackageId", "example.other"); break;
            case "descriptor": receipt["contracts"]![0]!["descriptorSha256"] = new string('b', 64); break;
            case "descriptor-field": receipt["contracts"]![0]!["source"] = "owned"; break;
            case "marker": File.WriteAllText(GenerateSunderRpcBindingsTask.GetOwnershipMarkerPath(item.GetMetadata("GeneratedDirectory")), "not owned"); break;
            case "lease": task.RpcOutputLockLeaseId = "not-a-live-lease"; break;
            case "missing-receipt": task.AuthoringReceipts = []; break;
            case "changed-use": task.UsesContracts[0].SetMetadata("Required", "false"); break;
            case "snapshot": File.AppendAllText(task.BindingSnapshots[0].ItemSpec, "\n"); break;
        }
        WriteJson(item.ItemSpec, receipt);
        if (failure != "hash") item.SetMetadata("Sha256", Sha256(File.ReadAllBytes(item.ItemSpec)));
        Assert.False(task.Execute());
        Assert.NotEmpty(((BuildEngine)task.BuildEngine).Errors);
        Assert.Equal(manifestBefore, File.ReadAllBytes(initial.ManifestOutputPath));
        Assert.Equal(descriptorsBefore, Snapshot(Path.Combine(generation.IntermediateOutputPath, "sunder-contracts")));
    }

    [Fact]
    public void ReceiptSymlink_IsRejectedEvenWithExactBytesAndHash()
    {
        var generation = Generate();
        var initial = BuildManifest(generation);
        var task = CreateManifest(generation, initial.TargetAssemblyPath);
        var receipt = Assert.Single(task.AuthoringReceipts);
        var external = Path.Combine(_root, "external-receipt.json");
        File.Move(receipt.ItemSpec, external);
        try
        {
            File.CreateSymbolicLink(receipt.ItemSpec, external);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            File.Move(external, receipt.ItemSpec);
            return;
        }
        var before = File.ReadAllBytes(initial.ManifestOutputPath);
        Assert.False(task.Execute());
        Assert.Equal(before, File.ReadAllBytes(initial.ManifestOutputPath));
    }

    [Fact]
    public void AuthoredDependency_CannotDuplicateReviewedRpcDependencyEvenWithEqualRange()
    {
        var generation = Generate();
        var initial = BuildManifest(generation);
        var before = File.ReadAllBytes(initial.ManifestOutputPath);
        File.WriteAllText(Path.Combine(_root, "Dependency.cs"), """
            [assembly: Sunder.Sdk.Packaging.SunderPackageDependency(PackageId = "example.dependency", VersionRange = ">=1.0.0 <2.0.0")]
            """);
        var updated = Generate();
        var task = CreateManifest(updated, Compile(updated));
        Assert.False(task.Execute());
        Assert.Contains(((BuildEngine)task.BuildEngine).Errors, message => message.Contains("must not duplicate lock-derived", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(initial.ManifestOutputPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelfAuthorityAndMissingTransitiveReviewedRange_FailBeforePublication(bool self)
    {
        var generation = Generate();
        var before = Snapshot(generation.IntermediateOutputPath);
        var value = ReadJson(LockPath);
        var artifact = value["packageArtifacts"]![DependencyId]!;
        artifact["contracts"]![ContractId]!["artifactSource"] = "imported";
        artifact["contracts"]![ContractId]!["authorityId"] = self ? "example.owner" : "example.transitive";
        artifact["publicInventorySha256"] = InventorySha256(DependencyId, artifact);
        WriteJson(LockPath, value);
        _ = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        var failed = CreateGeneration();
        Assert.False(failed.Execute());
        Assert.Contains(((BuildEngine)failed.BuildEngine).Errors, message => message.Contains(
            self ? "cannot import the package being built" : "unreviewed authority range", StringComparison.Ordinal));
        Assert.Equal(before, Snapshot(generation.IntermediateOutputPath));
    }

    [Fact]
    public void ReviewedReexport_PropagatesRequiredPresenceWithoutPinningSelectedReleases()
    {
        var contractLock = ReadJson(LockPath);
        var reexport = contractLock["packageArtifacts"]![DependencyId]!.DeepClone();
        reexport["selectedPackageVersion"] = "1.4.0";
        reexport["selectedPackageContentIdentity"] = new string('2', 64);
        reexport["contracts"]![ContractId]!["artifactSource"] = "imported";
        reexport["publicInventorySha256"] = InventorySha256("example.reexport", reexport);
        ModelProjectionTestEvidence.PinEmptySourceProjection("example.reexport", reexport);
        contractLock["packageArtifacts"]!["example.reexport"] = reexport;
        contractLock["packageImports"]![DependencyId]!["presence"] = "optional";
        contractLock["packageImports"]!["example.reexport"] = new JsonObject
        {
            ["localName"] = "reexport", ["presence"] = "required", ["requiredContracts"] = new JsonArray(ContractId),
            ["consumerRoles"] = new JsonArray("runtime"),
        };
        WriteJson(LockPath, contractLock);
        ModelProjectionTestEvidence.Seed(_cache, SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts["example.reexport"]);
        var generation = Generate();
        var manifest = ReadManifest(BuildManifest(generation).ManifestOutputPath);
        Assert.Single(manifest.ContractBundles!);
        Assert.Single(manifest.UsesContracts!);
        Assert.Equal(2, manifest.DependsOn!.Count);
        Assert.All(manifest.DependsOn, dependency =>
        {
            Assert.Equal("required", dependency.Presence);
            Assert.Equal(">=1.0.0 <2.0.0", dependency.VersionRange);
        });
        Assert.Equal("optional", manifest.PackageApiImports!.Single(import => import!.PackageId == DependencyId)!.Presence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProducerExportAndOwnedOnlyApp_AggregateWithIdenticalWorkerEvidenceWithoutExternalAppGrants(bool provider)
    {
        var platform = PlatformFixture();
        UseOwnedFixture();
        File.Copy(Fixture("authoring/conformance/valid/package-api-mixed-authority-contract-lock.json"), LockPath, overwrite: true);
        ModelProjectionTestEvidence.Seed(_cache, SunderRpcAuthoringDocuments.ReadContractLock(LockPath).PackageArtifacts[DependencyId]);
        var contractLock = ReadJson(LockPath);
        contractLock["packageImports"]![DependencyId]!["presence"] = "optional";
        if (provider)
        {
            contractLock["providerImports"]!["minimalProvider"] = new JsonObject { ["packageId"] = DependencyId, ["contractId"] = ContractId };
            File.WriteAllText(Path.Combine(_root, "Provider.cs"), ProviderSource);
        }
        WriteJson(LockPath, contractLock);
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Unexpected HTTP.")));
        await new SunderRpcContractRestore(http).RestoreAsync(LockPath, _cache, [], platformCatalog: platform.Path);
        var producer = Generate("ProjectExport", "neutral");
        var export = Assert.Single(producer.PackageProjectExports);
        using (var document = JsonDocument.Parse(File.ReadAllBytes(export.ItemSpec)))
        {
            Assert.Equal(Sha256(File.ReadAllBytes(LockPath)), document.RootElement.GetProperty("lockSha256").GetString());
            Assert.Equal("example.generated.greeting", Assert.Single(document.RootElement.GetProperty("contracts").EnumerateArray()).GetProperty("contractId").GetString());
        }
        Assert.Single(producer.ContractBundles);
        Assert.Empty(producer.AuthoringReceipts);
        var first = Generate("Implementation", "linux-x64", producer.PackageProjectExports);
        var second = Generate("Implementation", "linux-arm64", producer.PackageProjectExports);
        var firstManifest = BuildManifest(first);
        var secondManifest = BuildManifest(second, rid: "linux-arm64");
        var appDirectory = Path.Combine(_root, "App");
        Directory.CreateDirectory(appDirectory);
        var appSource = Path.Combine(appDirectory, "App.cs");
        File.WriteAllText(appSource, """
            using System;
            using Microsoft.Extensions.DependencyInjection;
            using Sunder.Sdk.Abstractions;
            [assembly: Sunder.Sdk.Packaging.SunderPackage(Id = "example.generated", Name = "Implementation-First RPC Fixture")]
            public sealed class AppModule : ISunderAppPackageModule
            {
                public void ConfigureAppServices(IServiceCollection services, IPackageContext context) { }
                public void RegisterAppContributions(ISunderAppContributionRegistry registry, IServiceProvider services) { }
                public Type OwnedClientType => typeof(Sunder.Generated.Packages.Example_Generated_Services.CExample_Generated_Greeting.IGreetingService);
            }
            """);
        var app = CreateGeneration("ProjectImport", "app", producer.PackageProjectExports);
        app.ProjectDirectory = appDirectory;
        app.ProjectIdentity = "ImportedApp";
        app.SourceFiles = [new TaskItem(appSource)];
        app.ReferencePaths = ReferencePaths(worker: false).Select(static path => (ITaskItem)new TaskItem(path)).ToArray();
        AssertSuccess(app);
        var appManifest = BuildManifest(app, role: "app");
        Assert.Single(app.ContractBundles);
        Assert.Single(app.RpcUses);
        Assert.Equal("app", app.RpcUses[0].GetMetadata("Role"));
        Assert.Empty(ReadManifest(appManifest.ManifestOutputPath).PackageArtifactImports!);
        Assert.DoesNotContain(app.CompileItems, item => File.ReadAllText(item.ItemSpec).Contains("DependencyPackageApi", StringComparison.Ordinal));
        var leaves = new[] { EmitLeaf(appManifest), EmitLeaf(firstManifest), EmitLeaf(secondManifest) };
        var aggregate = CreateAggregate(leaves);
        AssertSuccess(aggregate);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(aggregate.OutputPath);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var manifest = validation.Manifest!;
        Assert.Equal(3, manifest.Targets!.Count);
        Assert.Equal(2, manifest.ContractBundles!.Count);
        Assert.Equal(provider ? "required" : "optional", Assert.Single(manifest.DependsOn!).Presence);
        Assert.Equal("optional", Assert.Single(manifest.PackageApiImports!)!.Presence);
        Assert.Contains(Assert.Single(manifest.PackageArtifactImports!)!.AuthorityConstraints!, constraint => constraint!.AuthorityKind == "platform");
        Assert.Equal(provider ? 1 : 0, manifest.ProviderImports!.Count);
        if (provider) Assert.Equal("runtime", Assert.Single(Assert.Single(manifest.ProviderImports!)!.ProviderRefs!)!.Role);
        Assert.All(Assert.Single(manifest.PackageApiImports!)!.UseRefs!, reference => Assert.Equal("runtime", reference!.Role));
        Assert.Equal("example.generated.greeting", Assert.Single(manifest.UsesContracts!, use => use!.Role == "app")!.ContractId);
        Assert.True(File.Exists(Path.Combine(aggregate.OutputPath, "payload/shared/contracts", ContractId + ".rpc.json")));

        var prior = Snapshot(aggregate.OutputPath);
        var changedPath = Path.Combine(leaves[2], "manifest/sunder-package.json");
        var changed = ReadJson(changedPath);
        changed["packageArtifactImports"]![0]!["requestedVersionRange"] = ">=1.1.0 <2.0.0";
        changed["dependsOn"]![0]!["versionRange"] = ">=1.1.0 <2.0.0";
        WriteJson(changedPath, changed);
        PackageContentIndexer.Write(leaves[2]);
        Assert.True((await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(leaves[2])).Success);
        aggregate.BuildEngine = new BuildEngine();
        Assert.False(aggregate.Execute());
        Assert.Contains(((BuildEngine)aggregate.BuildEngine).Errors, message => message.Contains("package-wide manifest metadata", StringComparison.Ordinal));
        Assert.Equal(prior, Snapshot(aggregate.OutputPath));

        var before = Snapshot(first.IntermediateOutputPath);
        contractLock["packageImports"]![DependencyId]!["presence"] = "required";
        WriteJson(LockPath, contractLock);
        var staleExport = CreateGeneration("Implementation", "linux-x64", producer.PackageProjectExports);
        Assert.False(staleExport.Execute());
        Assert.Contains(((BuildEngine)staleExport.BuildEngine).Errors, message => message.Contains("exact-RID contract lock", StringComparison.Ordinal));
        Assert.Equal(before, Snapshot(first.IntermediateOutputPath));
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task Msbuild_FirstBuildUsesConfiguredCacheAndNeverRestoresContractsImplicitly()
    {
        var project = CreateMsbuildImportProject(_cache);
        var emptyFeed = Path.Combine(_root, "empty-feed");
        Directory.CreateDirectory(emptyFeed);
        await RunDotnet("restore", project, "--source", emptyFeed, "--disable-parallel", "--nologo");
        var cacheBefore = Snapshot(_cache);
        await RunDotnet("build", project, "--no-restore", "--disable-build-servers", "-m:1", "--nologo");
        var intermediate = Path.Combine(_root, "obj/Debug/net10.0", RuntimeInformation.RuntimeIdentifier);
        var leaf = Path.Combine(_root, "bin/Debug/net10.0", RuntimeInformation.RuntimeIdentifier, "sunder-dev");
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(leaf);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        Assert.Single(validation.Manifest!.PackageApiImports!);
        Assert.Equal(cacheBefore, Snapshot(_cache));
        var priorGenerated = Snapshot(Path.Combine(intermediate, "sunder-rpc"));
        var priorLeaf = Snapshot(leaf);
        File.Delete(SunderRpcContractCache.GetDescriptorPath(_cache, _descriptor.Sha256));
        var failure = await RunDotnet(false, "build", project, "--no-restore", "--disable-build-servers", "-m:1", "--nologo");
        Assert.Contains("contract-cache.entry-missing", failure, StringComparison.Ordinal);
        Assert.False(File.Exists(SunderRpcContractCache.GetDescriptorPath(_cache, _descriptor.Sha256)));
        Assert.Equal(priorGenerated, Snapshot(Path.Combine(intermediate, "sunder-rpc")));
        Assert.Equal(priorLeaf, Snapshot(leaf));
    }

    private string CreateMsbuildImportProject(string cacheRoot)
    {
        var targets = Path.Combine(RepositoryRoot(), "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets");
        var project = Path.Combine(_root, "ImportedFixture.csproj");
        var references = new[] { typeof(SunderWorker).Assembly.Location, typeof(SunderRpcContractDescriptor).Assembly.Location,
            typeof(IServiceCollection).Assembly.Location, typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location };
        var document = new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", "net10.0"), new XElement("OutputType", "Exe"), new XElement("Nullable", "enable"),
                new XElement("RuntimeIdentifier", RuntimeInformation.RuntimeIdentifier), new XElement("UseAppHost", "false"),
                new XElement("SelfContained", "false"), new XElement("Version", "1.0.0"), new XElement("NuGetAudit", "false"),
                new XElement("SunderPackageBuildTaskAssembly", typeof(GenerateSunderRpcBindingsTask).Assembly.Location),
                new XElement("SunderPackageBuildTaskSourceIsImmutable", "true"), new XElement("SunderPackageBuildProject", Path.Combine(_root, "no-task-source.csproj")),
                new XElement("SunderContractCacheRoot", cacheRoot), new XElement("SunderSdkPackageVersion", "1.1.0")),
            new XElement("ItemGroup", references.Select(path => new XElement("Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(path)), new XAttribute("HintPath", path))),
                new XElement("SunderPackageTarget", new XAttribute("Include", "runtime/$(RuntimeIdentifier)"), new XAttribute("Role", "runtime"),
                    new XAttribute("Rid", "$(RuntimeIdentifier)"), new XAttribute("Kind", "worker"))),
            new XElement("Import", new XAttribute("Project", targets))));
        document.Save(project);
        return project;
    }

    private string LockPath => Path.Combine(_root, SunderRpcAuthoringDocuments.ContractLockFileName);

    [Fact]
    public void ProjectExportSnapshot_SurvivesProducerSupersessionAndCleanWithoutWeakeningHashChecks()
    {
        UseOwnedFixture();
        var first = Generate("ProjectExport", "neutral");
        var exported = Assert.Single(first.PackageProjectExports);
        var bytes = File.ReadAllBytes(exported.ItemSpec);
        File.AppendAllText(Path.Combine(_root, "Program.cs"), "\n// A later evaluation has different producer inputs.\n");
        var second = Generate("ProjectExport", "neutral");
        Assert.NotEqual(exported.ItemSpec, Assert.Single(second.PackageProjectExports).ItemSpec);
        Assert.Equal(bytes, File.ReadAllBytes(exported.ItemSpec));
        Assert.DoesNotContain(first.FileWrites, item => item.ItemSpec == exported.ItemSpec);
        var mutable = new TaskItem(Path.Combine(first.IntermediateOutputPath, GenerateSunderRpcBindingsTask.GeneratedDirectoryName));
        mutable.SetMetadata("DiscoveryRoot", first.IntermediateOutputPath);
        AssertSuccess(new CleanSunderRpcGeneratedOutputTask
        { BuildEngine = new BuildEngine(), ProjectDirectory = _root, GeneratedDirectories = [mutable] });
        Assert.False(Directory.Exists(mutable.ItemSpec));
        Assert.Equal(bytes, File.ReadAllBytes(exported.ItemSpec));
        var imported = Generate("ProjectImport", "app", first.PackageProjectExports);
        Assert.Single(imported.RpcUses);
        var previous = Snapshot(imported.IntermediateOutputPath);
        File.AppendAllText(exported.ItemSpec, "\n");
        var corrupt = CreateGeneration("ProjectImport", "app", first.PackageProjectExports);
        Assert.False(corrupt.Execute());
        Assert.Equal(previous, Snapshot(imported.IntermediateOutputPath));
    }

    private GenerateSunderRpcBindingsTask CreateGeneration(string mode = "Combined", string leaf = "linux-x64", ITaskItem[]? exports = null)
    {
        var intermediate = Path.Combine(_root, "obj", leaf);
        Directory.CreateDirectory(intermediate);
        return new()
        {
            BuildEngine = new BuildEngine(), ProjectDirectory = _root, IntermediateOutputPath = intermediate,
            PackageVersion = "1.0.0", ProjectIdentity = "ImportedFixture", Configuration = "Debug", TargetFramework = "net10.0",
            OutputType = "Exe", ContractCacheRoot = _cache, GenerationMode = mode, ExpectedPackageProjectExports = exports ?? [],
            SourceFiles = Directory.GetFiles(_root, "*.cs").Order(StringComparer.Ordinal).Select(static path => (ITaskItem)new TaskItem(path)).ToArray(),
            ReferencePaths = ReferencePaths().Select(static path => (ITaskItem)new TaskItem(path)).ToArray(),
        };
    }

    private GenerateSunderRpcBindingsTask Generate(string mode = "Combined", string leaf = "linux-x64", ITaskItem[]? exports = null)
    {
        var task = CreateGeneration(mode, leaf, exports);
        AssertSuccess(task);
        return task;
    }

    private static string Compile(GenerateSunderRpcBindingsTask generation, bool runtime = true)
    {
        var output = Path.Combine(generation.IntermediateOutputPath, "compiled");
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, runtime ? "ImportedFixture.dll" : "ImportedFixture.App.dll");
        var compilation = CSharpCompilation.Create(Path.GetFileNameWithoutExtension(path),
            generation.SourceFiles.Concat(generation.CompileItems).Select(item => CSharpSyntaxTree.ParseText(File.ReadAllText(item.ItemSpec),
                new CSharpParseOptions(LanguageVersion.CSharp14), item.ItemSpec)),
            generation.ReferencePaths.Select(item => MetadataReference.CreateFromFile(item.ItemSpec)),
            new CSharpCompilationOptions(runtime ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable, generalDiagnosticOption: ReportDiagnostic.Error));
        using var stream = File.Create(path);
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }

    private static GenerateSunderPackageManifestTask CreateManifest(GenerateSunderRpcBindingsTask generation, string assembly, string role = "runtime", string rid = "linux-x64")
    {
        var target = new TaskItem(role + "/" + rid);
        target.SetMetadata("Role", role);
        target.SetMetadata("Rid", rid);
        target.SetMetadata("Kind", role == "runtime" ? "worker" : "avalonia");
        return new()
        {
            BuildEngine = new BuildEngine(), TargetAssemblyPath = assembly, EntryAssembly = Path.GetFileName(assembly),
            ManifestOutputPath = Path.Combine(generation.IntermediateOutputPath, "sunder-package.json"), ProjectDirectory = generation.ProjectDirectory,
            PackageVersion = "1.0.0", TargetFramework = "net10.0", SdkPackageVersion = "1.1.0", ReferencePaths = generation.ReferencePaths,
            AuthoredAssemblyPaths = [new TaskItem(assembly)], PackageTargets = [target], OutputType = role == "runtime" ? "Exe" : "Library",
            RuntimeIdentifier = role == "runtime" ? rid : null,
            ContractBundles = generation.ContractBundles, BindingSnapshots = generation.BindingSnapshots, UsesContracts = generation.RpcUses,
            RpcProviders = generation.RpcProviders, AuthoringReceipts = generation.AuthoringReceipts,
        };
    }

    private static GenerateSunderPackageManifestTask BuildManifest(GenerateSunderRpcBindingsTask generation, string role = "runtime", string rid = "linux-x64")
    {
        var task = CreateManifest(generation, Compile(generation, role == "runtime"), role, rid);
        AssertSuccess(task);
        return task;
    }

    private static string EmitLeaf(GenerateSunderPackageManifestTask manifestTask, params string[] additionalRuntimeFiles)
    {
        var manifest = ReadManifest(manifestTask.ManifestOutputPath);
        var target = Assert.Single(manifest.Targets!)!;
        var directory = Path.GetDirectoryName(manifestTask.TargetAssemblyPath)!;
        var output = Path.Combine(directory, "sunder-dev");
        var workerFiles = new List<ITaskItem>();
        if (target.Role == "runtime")
        {
            var paths = new[] { manifestTask.TargetAssemblyPath, typeof(SunderWorker).Assembly.Location, typeof(SunderRpcContractDescriptor).Assembly.Location,
                typeof(IServiceCollection).Assembly.Location, typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location }.Concat(additionalRuntimeFiles).ToArray();
            var runtimeConfig = Path.ChangeExtension(manifestTask.TargetAssemblyPath, ".runtimeconfig.json");
            var deps = Path.ChangeExtension(manifestTask.TargetAssemblyPath, ".deps.json");
            File.WriteAllText(runtimeConfig, """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
            var runtimeTarget = ".NETCoreApp,Version=v10.0/" + target.Rid;
            var assets = new JsonObject();
            foreach (var path in paths) assets[Path.GetFileName(path)] = new JsonObject();
            WriteJson(deps, new JsonObject
            {
                ["runtimeTarget"] = new JsonObject { ["name"] = runtimeTarget },
                ["targets"] = new JsonObject { [runtimeTarget] = new JsonObject { ["ImportedFixture/1.0.0"] = new JsonObject { ["runtime"] = assets } } },
                ["libraries"] = new JsonObject { ["ImportedFixture/1.0.0"] = new JsonObject { ["type"] = "project", ["serviceable"] = false, ["sha512"] = "" } },
            });
            foreach (var path in paths.Append(runtimeConfig).Append(deps))
            {
                var item = new TaskItem(path);
                item.SetMetadata("RelativePath", Path.GetFileName(path));
                workerFiles.Add(item);
            }
        }
        var emit = new EmitSunderDevOutputTask
        {
            BuildEngine = new BuildEngine(), ManifestPath = manifestTask.ManifestOutputPath, DevPackagePath = output,
            TargetDirectory = directory, DiscoveryRoot = directory, ContractFiles = manifestTask.ResolvedContractFiles,
            WorkerFiles = workerFiles.ToArray(), ManagedFiles = target.Role == "app" ? [new TaskItem(manifestTask.TargetAssemblyPath)] : [],
        };
        AssertSuccess(emit);
        return output;
    }

    private AggregateSunderPackageTask CreateAggregate(params string[] leaves)
    {
        var target = Path.Combine(_root, "aggregate/bin");
        Directory.CreateDirectory(target);
        return new()
        {
            BuildEngine = new BuildEngine(), ExpectedPackageVersion = "1.0.0", ProjectDirectory = Path.Combine(_root, "aggregate"),
            TargetDirectory = target, OutputDiscoveryRoot = target, OutputPath = Path.Combine(target, "sunder-dev"),
            TargetLeaves = leaves.Select(path =>
            {
                var item = new TaskItem(path);
                item.SetMetadata("DiscoveryRoot", Path.GetDirectoryName(path)!);
                return (ITaskItem)item;
            }).ToArray(),
        };
    }

    private void UseOwnedFixture()
    {
        var fixture = Path.Combine(RepositoryRoot(), "languages/csharp/dotnet/tests/Sunder.Package.Build.TestFixtures/ImplementationFirstRpc");
        File.Delete(Path.Combine(_root, SunderRpcAuthoringDocuments.OwnedContractsFileName));
        File.Copy(Path.Combine(fixture, "Program.cs"), Path.Combine(_root, "Program.cs"), overwrite: true);
        File.WriteAllText(Path.Combine(_root, "GlobalUsings.cs"), "global using System;\nglobal using System.Threading;\nglobal using System.Threading.Tasks;\n");
    }

    private static string[] ReferencePaths(bool worker = true)
        => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(SunderRpcContractDescriptor).Assembly.Location).Append(typeof(SunderWorker).Assembly.Location)
            .Where(path => worker || Path.GetFileName(path) != "Sunder.Sdk.Worker.dll").Distinct(StringComparer.Ordinal).ToArray();

    private static string InventorySha256(string packageId, JsonNode artifact)
    {
        var contracts = new JsonArray();
        foreach (var entry in artifact["contracts"]!.AsObject().OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            var contract = new JsonObject { ["contractId"] = entry.Key, ["contractVersion"] = entry.Value!["resolvedContractVersion"]!.DeepClone() };
            foreach (var field in new[] { "descriptorBytes", "descriptorSha256", "abiSha256", "artifactSource", "authorityKind", "authorityId",
                         "selectedAuthorityRelease", "selectedAuthorityContentIdentity" })
                if (entry.Value![field] is { } value) contract[field] = value.DeepClone();
            contracts.Add(contract);
        }
        var inventory = new JsonObject
        {
            ["schemaVersion"] = 1, ["packageId"] = packageId, ["packageVersion"] = artifact["selectedPackageVersion"]!.DeepClone(),
            ["packageContentIdentity"] = artifact["selectedPackageContentIdentity"]!.DeepClone(), ["publicContractCount"] = artifact["publicContractCount"]!.DeepClone(),
            ["contracts"] = contracts,
        };
        return Sha256(Encoding.UTF8.GetBytes("sunder.package-public-api-inventory.v1\0" + inventory.ToJsonString(
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })));
    }

    private Task<string> RunDotnet(params string[] arguments) => RunDotnet(true, arguments);

    private async Task<string> RunDotnet(bool success, params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var text = await stdout + await stderr;
        Assert.True(success ? process.ExitCode == 0 : process.ExitCode != 0, text);
        return text;
    }

    private static void AssertSuccess(Microsoft.Build.Utilities.Task task)
        => Assert.True(task.Execute(), string.Join(Environment.NewLine, ((BuildEngine)task.BuildEngine).Errors));

    private static SunderPackageManifest ReadManifest(string path) => JsonSerializer.Deserialize<SunderPackageManifest>(File.ReadAllBytes(path))!;
    private static JsonObject ReadJson(string path) => JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
    private static void WriteJson(string path, JsonObject value) => File.WriteAllText(path, value.ToJsonString(JsonOptions) + "\n");
    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string[] Snapshot(string path) => Directory.GetFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(file => Path.GetRelativePath(path, file) + ":" + Sha256(File.ReadAllBytes(file)) + ":" + File.GetLastWriteTimeUtc(file).Ticks).ToArray();
    private static string Fixture(string path) => Path.Combine(RepositoryRoot(), "specs/rpc/v1", path);
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        return directory!.FullName;
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
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
            => throw new InvalidOperationException("Generation must not restore contracts or invoke other projects.");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
