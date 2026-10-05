using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskItem = Microsoft.Build.Utilities.TaskItem;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    private const string StackDomainProviderSource = """
        using System;
        using System.IO;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Sunder.Sdk.Rpc;
        using Sunder.Sdk.Stacks;
        [SunderRpcProvider(typeof(IPackageStackContributor), ProviderId = "example.owner.stack")]
        public sealed partial class LocalStack : IPackageStackExporter, IPackageStackImporter, IPackageStackImportAppliedHandler
        {
            public string ContributorId => "local-stack";
            public string DisplayName => "Local Stack";
            public string? StampedOwner { get; private set; }
            public string? StampedContributor { get; private set; }
            public bool PayloadOpened { get; private set; }
            public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(StackExportDiscoveryContext request, CancellationToken token)
                => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>([new("item", "Item", "sample")]);
            public ValueTask<StackExportContribution> ExportAsync(StackExportRequest request, CancellationToken token)
                => ValueTask.FromResult(new StackExportContribution([new("fragment", "sample", 1, "Sample", "{}",
                    Files: [new StackExportPayloadHandle("data.bin", _ => ValueTask.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 })), 3)])], [], []));
            public async ValueTask<StackImportPreview> PreviewImportAsync(StackImportPreviewRequest request, CancellationToken token)
            {
                var fragment = request.Fragments[0]; StampedOwner = fragment.OwnerPackageId; StampedContributor = fragment.ContributorId;
                await using var stream = await fragment.Files![0].OpenReadAsync(token); PayloadOpened = stream.ReadByte() == 1;
                return new([], [], [], []);
            }
            public ValueTask<StackImportResult> ImportAsync(StackImportRequest request, CancellationToken token)
                => ValueTask.FromResult(new StackImportResult(StackImportOutcome.Completed, [], new Dictionary<string, string>(), [], []));
            public ValueTask OnStackImportAppliedAsync(StackImportAppliedContext request, CancellationToken token)
            { StampedOwner = request.OwnerPackageId; StampedContributor = request.ContributorId; return ValueTask.CompletedTask; }
        }
        """;

    [Fact]
    public async Task StackPlatform_GeneratesRealSixMethodProviderAndPreservesSemanticContentBehavior()
    {
        var fixture = await PrepareRealStackProvider(); var before = File.ReadAllBytes(LockPath);
        var generation = CreateStackGeneration(); AssertSuccess(generation);
        var manifestTask = BuildManifest(generation); var manifest = ReadManifest(manifestTask.ManifestOutputPath);
        Assert.Empty(manifest.DependsOn!); Assert.Empty(manifest.UsesContracts!); Assert.Empty(manifest.PackageArtifactImports!);
        var bundle = Assert.Single(manifest.ContractBundles!)!;
        Assert.Equal("platform", bundle.AuthorityKind); Assert.Equal("imported", bundle.Source);
        Assert.Equal("38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898", bundle.DescriptorSha256);
        Assert.Equal("6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8", bundle.AbiSha256);
        Assert.Equal(fixture.Requirement.ContentIdentity, bundle.SelectedAuthorityContentIdentity);
        Assert.Single(Assert.Single(manifest.PlatformImports!)!.ProviderRefs!);
        var context = new AssemblyLoadContext("stack-import-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var stream = File.OpenRead(manifestTask.TargetAssemblyPath); var assembly = context.LoadFromStream(stream);
            var provider = Activator.CreateInstance(assembly.GetType("LocalStack")!)!;
            var registration = Assert.IsType<SunderWorkerProviderRegistration>(provider.GetType().GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(provider, null));
            var declare = provider.GetType().GetMethod("DeclareSunderRpcProvider")!
                .CreateDelegate<Func<ISunderRpcServiceHandler, SunderWorkerProviderRegistration>>();
            var declaration = declare(registration.Handler);
            Assert.Equal(registration.ProviderId, declaration.ProviderId);
            Assert.Equal(registration.DescriptorSha256, declaration.DescriptorSha256);
            Assert.Equal(registration.AbiSha256, declaration.AbiSha256);
            Assert.Equal(registration.DeclaredErrors["stack-contributor"]["export"], declaration.DeclaredErrors["stack-contributor"]["export"]);
            Assert.Equal(bundle.DescriptorSha256, registration.DescriptorSha256); Assert.Equal(bundle.AbiSha256, registration.AbiSha256);
            var handler = registration.Handler; var host = new StackContributorSourceParityTests.ContentHost(); var call = host.Context();
            var metadata = await Invoke("metadata", new { });
            Assert.True(metadata.GetProperty("supportsExport").GetBoolean()); Assert.True(metadata.GetProperty("supportsImport").GetBoolean());
            Assert.True(metadata.GetProperty("supportsImportApplied").GetBoolean());
            var items = await Invoke("list-export-items", new { packageId = "owner.package" });
            Assert.Equal("item", items.GetProperty("items")[0].GetProperty("itemId").GetString());
            Assert.False(items.GetProperty("items")[0].TryGetProperty("description", out _));
            var exported = await Invoke("export", new { itemSelections = Array.Empty<object>() });
            Assert.Single(host.Registered); Assert.Equal(new byte[] { 1, 2, 3 }, host.RegisteredBytes);
            Assert.Equal(SunderRpcContentRepeatability.SingleUse, host.Registered[0].Options.Repeatability);
            var file = exported.GetProperty("fragments")[0].GetProperty("files")[0];
            var fragment = new { fragmentId = "fragment", ownerPackageId = "forged", contributorId = "forged", schemaId = "sample", schemaVersion = 1,
                displayName = "Sample", jsonPayload = "{}", files = new[] { file } };
            _ = await Invoke("preview-import", new { fragments = new[] { fragment }, inputValues = new { }, idRemaps = new { } });
            Assert.Single(host.Opened); Assert.Equal("owner.package", provider.GetType().GetProperty("StampedOwner")!.GetValue(provider));
            Assert.Equal("local-stack", provider.GetType().GetProperty("StampedContributor")!.GetValue(provider));
            Assert.Equal(true, provider.GetType().GetProperty("PayloadOpened")!.GetValue(provider));
            var result = await Invoke("import", new { fragments = new[] { fragment }, inputValues = new { }, idRemaps = new { }, selectedActionIds = Array.Empty<string>() });
            Assert.Equal((int)StackImportOutcome.Completed, result.GetProperty("outcome").GetInt32());
            var applied = await Invoke("import-applied", new { ownerPackageId = "forged", contributorId = "forged", fragmentIds = Array.Empty<string>(), importedItems = Array.Empty<object>() });
            Assert.Empty(applied.EnumerateObject()); Assert.Equal("owner.package", provider.GetType().GetProperty("StampedOwner")!.GetValue(provider));
            Assert.Equal("local-stack", provider.GetType().GetProperty("StampedContributor")!.GetValue(provider));
            await Assert.ThrowsAsync<JsonException>(() => Invoke("list-export-items", new { packageId = "" }));
            var declared = Assert.IsAssignableFrom<ISunderRpcDeclaredErrorMetadata>(handler).DeclaredErrors;
            Assert.Equal(6, declared["stack-contributor"].Count);
            Assert.Equal(["stack.method.unsupported"], declared["stack-contributor"]["export"]);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.InvokeUnaryAsync(call, "stack-contributor", "export",
                JsonSerializer.SerializeToElement(new { itemSelections = Array.Empty<object>() }), cancelled.Token).AsTask());
            Assert.Single(host.Registered);
            async Task<JsonElement> Invoke(string method, object request) => await handler.InvokeUnaryAsync(call, "stack-contributor", method, JsonSerializer.SerializeToElement(request));
        }
        finally { context.Unload(); }
        var leaf = EmitLeaf(manifestTask, typeof(IPackageStackContributor).Assembly.Location); var aggregate = CreateAggregate(leaf); AssertSuccess(aggregate);
        var pack = new PackSunderPackageTask
        {
            BuildEngine = new BuildEngine(), ProjectDirectory = _root, DevPackagePath = aggregate.OutputPath, DevPackageDiscoveryRoot = aggregate.TargetDirectory,
            PackageOutputPath = Path.Combine(aggregate.TargetDirectory, "stack.sunderpkg"), SourceFiles = generation.SourceFiles, ReferencePaths = generation.ReferencePaths,
            AuthoringReceipts = generation.AuthoringReceipts, IntermediateOutputPath = generation.IntermediateOutputPath, ContractCacheRoot = _cache,
            OutputType = "Exe", ProjectIdentity = generation.ProjectIdentity, Configuration = generation.Configuration, TargetFramework = generation.TargetFramework,
        };
        AssertSuccess(pack); Assert.Equal(before, File.ReadAllBytes(LockPath));
    }

    [Theory]
    [InlineData("missing-import")]
    [InlineData("wrong-descriptor")]
    public async Task StackPlatform_RejectsMissingOrIncompatibleAuthorityWithoutReplacingGeneratedOutput(string failure)
    {
        var fixture = await PrepareRealStackProvider(); var generation = CreateStackGeneration(); AssertSuccess(generation);
        var snapshot = Snapshot(generation.IntermediateOutputPath);
        if (failure == "missing-import") ClearCatalogGcLock();
        if (failure == "wrong-descriptor")
        {
            fixture = PlatformFixture(); var value = ReadJson(LockPath); value["platformImports"]!["stackContributor"]!["authorityReleaseRange"] = ">=1.1.0 <1.2.0"; WriteJson(LockPath, value);
            using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException()));
            await new SunderRpcContractRestore(http).RestoreAsync(LockPath, _cache, [], platformCatalog: fixture.Path);
        }
        Assert.False(CreateStackGeneration().Execute()); Assert.Equal(snapshot, Snapshot(generation.IntermediateOutputPath));
    }

    private GenerateSunderRpcBindingsTask CreateStackGeneration()
    {
        var generation = CreateGeneration();
        generation.ReferencePaths = generation.ReferencePaths.Append(new TaskItem(typeof(IPackageStackContributor).Assembly.Location)).ToArray();
        return generation;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StackPlatform_MultipleContributorsRequireDistinctProviderIdentity(bool collision)
    {
        var fixture = await PrepareRealStackProvider();
        File.WriteAllText(Path.Combine(_root, "SecondStack.cs"), StackDomainProviderSource.Replace("LocalStack", "SecondStack", StringComparison.Ordinal)
            .Replace("example.owner.stack", collision ? "example.owner.stack" : "example.owner.stack.second", StringComparison.Ordinal));
        var generation = CreateStackGeneration();
        if (collision)
        {
            Assert.False(generation.Execute());
            Assert.Contains(((BuildEngine)generation.BuildEngine).Errors, error => error.Contains("rpc-authoring.identity-collision", StringComparison.Ordinal));
            return;
        }
        AssertSuccess(generation); var manifest = ReadManifest(BuildManifest(generation).ManifestOutputPath);
        Assert.Single(manifest.ContractBundles!); Assert.Equal(2, manifest.Provides!.Count);
        Assert.Equal(2, Assert.Single(manifest.PlatformImports!)!.ProviderRefs!.Count);
        Assert.Empty(manifest.DependsOn!); Assert.Empty(manifest.UsesContracts!);
    }

    [Fact]
    public async Task StackPlatform_ImportMarkerCannotDeclareLocalOwnership()
    {
        var fixture = await PrepareRealStackProvider();
        File.AppendAllText(Path.Combine(_root, "Provider.cs"), """

            [SunderRpcService(SunderRpcExposure.Public)]
            [SunderRpcPlatformImport("sunder.stack.contributor", typeof(StackContributorServiceAdapter))]
            public interface InvalidOwner : IPackageStackContributor { }
            """);
        var generation = CreateStackGeneration(); Assert.False(generation.Execute());
        Assert.Contains(((BuildEngine)generation.BuildEngine).Errors, error => error.Contains("rpc-authoring.authority-mismatch", StringComparison.Ordinal));
        Assert.Empty(generation.ContractBundles);
    }

    [Theory]
    [InlineData("missing-method")]
    [InlineData("idempotency")]
    [InlineData("error-set")]
    [InlineData("dto-field")]
    [InlineData("dto-type")]
    [InlineData("enum")]
    public async Task StackPlatform_RejectsAuthenticatedButIncompatibleDomainAdapterShapes(string mutation)
    {
        var fixture = await PrepareRealStackProvider(value =>
        {
            var methods = value["services"]!["stack-contributor"]!["methods"]!;
            if (mutation == "missing-method") methods.AsObject().Remove("metadata");
            if (mutation == "idempotency") methods["export"]!["idempotency"] = "none";
            if (mutation == "error-set") methods["export"]!["errors"] = new JsonArray();
            if (mutation == "dto-field") value["$defs"]!["StackContributorMetadata"]!["properties"]!["extra"] = new JsonObject { ["type"] = "string" };
            if (mutation == "dto-type") value["$defs"]!["StackContributorMetadata"]!["properties"]!["supportsExport"] = new JsonObject { ["type"] = "string" };
            if (mutation == "enum") { value["$defs"]!["StackImportOutcome"]!["enum"]!.AsArray().Add(10); value["$defs"]!["StackImportOutcome"]!["maximum"] = 10; }
        });
        var generation = CreateStackGeneration(); Assert.False(generation.Execute());
        Assert.Contains(((BuildEngine)generation.BuildEngine).Errors, error => error.Contains("rpc-authoring.handler-incomplete", StringComparison.Ordinal));
        Assert.Empty(generation.RpcProviders); Assert.Empty(generation.AuthoringReceipts);
    }

    [Fact]
    public async Task StackPlatform_TemplateUsesOwnedBootstrapThenTheRegistryImportAndGeneratedRegistration()
    {
        var fixture = await PrepareRealStackProvider(); var selected = File.ReadAllBytes(LockPath);
        ClearCatalogGcLock(); File.Delete(Path.Combine(_root, "Provider.cs"));
        var template = Path.Combine(RepositoryRoot(), "languages/csharp/dotnet/packages/Sunder.Package.Templates/templates/sunder-package/Sunder.Package.Template");
        foreach (var name in new[] { "Services/SampleService.cs", "Stacks/StackSupport.cs" })
            File.WriteAllText(Path.Combine(_root, Path.GetFileName(name)), File.ReadAllText(Path.Combine(template, name))
                .Replace("SUNDER_PACKAGE_ID_CSHARP", "example.owner", StringComparison.Ordinal)
                .Replace("SUNDER_PACKAGE_ID_TEXT", "example.owner", StringComparison.Ordinal)
                .Replace("SUNDER_PACKAGE_NAME_CSHARP", "Stack sample", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(_root, "Globals.cs"), "global using System; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;");
        // The lower-level compiler fixture already owns package metadata and its
        // test entry point; only the actual template service sources are imported.
        var bootstrap = CreateStackGeneration(); bootstrap.ContractOperation = "Accept"; bootstrap.ContractKey = "sample"; bootstrap.ContractNextVersion = "2.0.0";
        bootstrap.SourceFiles = bootstrap.SourceFiles.Where(item => Path.GetFileName(item.ItemSpec) != "StackSupport.cs").ToArray();
        AssertSuccess(bootstrap);
        Assert.False(CreateStackGeneration().Execute()); // Ordinary generation cannot use an unselected domain adapter.
        File.WriteAllBytes(LockPath, selected);
        var generation = CreateStackGeneration(); AssertSuccess(generation);
        var freeze = CreateStackGeneration(); freeze.ContractOperation = "Freeze"; AssertSuccess(freeze);
        generation = CreateStackGeneration(); AssertSuccess(generation);
        var manifestTask = BuildManifest(generation); var manifest = ReadManifest(manifestTask.ManifestOutputPath);
        Assert.Equal(2, manifest.ContractBundles!.Count); Assert.Equal(2, manifest.Provides!.Count);
        Assert.Empty(manifest.DependsOn!); Assert.Empty(manifest.UsesContracts!);
        Assert.Equal("platform", Assert.Single(manifest.ContractBundles, bundle => bundle!.ContractId == "sunder.stack.contributor")!.AuthorityKind);
        var context = new AssemblyLoadContext("template-stack-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var stream = File.OpenRead(manifestTask.TargetAssemblyPath); var assembly = context.LoadFromStream(stream);
            var provider = Activator.CreateInstance(assembly.GetType("Sunder.Package.Template.PackageStackContributor")!)!;
            var registration = Assert.IsType<SunderWorkerProviderRegistration>(provider.GetType().GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(provider, null));
            var call = new StackContributorSourceParityTests.ContentHost().Context();
            var metadata = await registration.Handler.InvokeUnaryAsync(call, "stack-contributor", "metadata", JsonSerializer.SerializeToElement(new { }));
            Assert.True(metadata.GetProperty("supportsExport").GetBoolean()); Assert.True(metadata.GetProperty("supportsImport").GetBoolean());
            Assert.False(metadata.GetProperty("supportsImportApplied").GetBoolean());
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => registration.Handler.InvokeUnaryAsync(call, "stack-contributor", "import-applied",
                JsonSerializer.SerializeToElement(new { ownerPackageId = "forged", contributorId = "forged", fragmentIds = Array.Empty<string>(), importedItems = Array.Empty<object>() })).AsTask());
            Assert.Equal("stack.method.unsupported", error.Error.Code);
        }
        finally { context.Unload(); }
        Assert.Equal(selected, File.ReadAllBytes(LockPath));
    }

    private async Task<PlatformFixtureData> PrepareRealStackProvider(Action<JsonNode>? mutate = null)
    {
        var fixture = PlatformFixture();
        var descriptorJson = JsonNode.Parse(File.ReadAllBytes(Path.Combine(RepositoryRoot(),
            "languages/csharp/dotnet/packages/Sunder.Sdk.Stacks/Contracts/sunder.stack.contributor.rpc.json")))!;
        mutate?.Invoke(descriptorJson);
        var descriptor = SunderRpcContractDescriptor.Parse(JsonSerializer.SerializeToUtf8Bytes(descriptorJson));
        var catalog = SunderPlatformContractCatalog.Verify(SunderPlatformContractCatalog.Create("1.1.0", [descriptor]));
        var value = ReadJson(LockPath); var selected = value["platformImports"]!["stackContributor"]!;
        selected["authorityReleaseRange"] = ">=1.1.0 <1.2.0"; selected["selectedAuthorityContentIdentity"] = catalog.ContentIdentity;
        selected["descriptorBytes"] = descriptor.GetCanonicalUtf8().Length; selected["descriptorSha256"] = descriptor.Sha256;
        selected["abiSha256"] = descriptor.AbiSha256; selected["etag"] = "\"" + descriptor.Sha256 + "\"";
        WriteJson(LockPath, value); File.WriteAllBytes(fixture.Path, catalog.GetCanonicalUtf8());
        var locked = SunderRpcAuthoringDocuments.ReadContractLock(LockPath);
        fixture = fixture with { Lock = locked, Requirement = Assert.Single(SunderRpcPlatformCatalogCache.Requirements(locked)), Envelope = catalog.GetCanonicalUtf8() };
        File.Delete(Path.Combine(_root, "Consumer.cs")); File.WriteAllText(Path.Combine(_root, "Provider.cs"), StackDomainProviderSource);
        using var http = new HttpClient(new RestoreHandler(_ => throw new InvalidOperationException("Unexpected HTTP.")));
        await new SunderRpcContractRestore(http).RestoreAsync(LockPath, _cache, [], platformCatalog: fixture.Path);
        return fixture;
    }

}
