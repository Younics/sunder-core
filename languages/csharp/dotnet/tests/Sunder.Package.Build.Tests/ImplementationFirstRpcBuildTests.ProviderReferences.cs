using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Authoring;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    private const string ReferenceClosureDeclaration = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        using Api = Sunder.Generated.Packages.Example_Models_Services;
        [SunderService(SunderServiceExposure.Public)]
        public interface HolderService { ValueTask<bool> HandleAsync(HolderContext context); }
        [SunderService(SunderServiceExposure.Public)]
        public interface TargetService { ValueTask<bool> HandleAsync(TargetContext context); }
        [SunderService(SunderServiceExposure.Public)]
        public interface LeafService { ValueTask<bool> HandleAsync(LeafContext context); }
        [SunderService(SunderServiceExposure.Public)]
        public interface UnrelatedService { ValueTask<bool> ReadyAsync(); }
        public sealed record HolderContext(ServiceReference<Api.ITargetService> Target);
        public sealed record TargetContext(ServiceReference<Api.ILeafService> Leaf);
        public sealed record LeafContext(ServiceReference<Api.ITargetService> Target);
        """;

    [Fact]
    public async Task ProviderOnlyImport_MaterializesPinnedRecursiveReferenceClosureWithoutConsumerIntent()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-provider-reference-closure");
        var root = scope.CreateDirectory("consumer"); var cache = scope.CreateDirectory("cache");
        var candidate = await WriteProjectedCandidateAsync(scope.CreateDirectory("candidate"), "2.0.0", ReferenceClosureDeclaration);
        _ = await CreateProjectedConsumerAsync(root, cache, "", app: false);
        var lockPath = Path.Combine(root, SunderRpcAuthoringDocuments.ContractLockFileName);
        using var http = new HttpClient(new OfflineProjectionHandler());
        var selected = (await new SunderRpcPackageApiCommands(new(http)).ExecuteAsync(lockPath, cache,
            new("Implement", "example.models", "example.models.holder", ">=1.0.0 <3.0.0", ProviderKey: "holder", Candidate: candidate))).ContractLock;
        var lockBytes = await File.ReadAllBytesAsync(lockPath);
        var imports = SunderRpcCSharpImportPrepass.Prepare(selected, cache);
        Assert.Equal(["example.models.holder", "example.models.leaf", "example.models.target"], imports.Contracts.Select(contract => contract.ContractKey));
        Assert.Equal(4, selected.PackageArtifacts["example.models"].PublicContractCount);
        Assert.Equal("holder", Assert.Single(imports.ProviderSurfaces).Key);
        Assert.Empty(imports.PackageApis); Assert.Empty(selected.PackageImports);
        Assert.DoesNotContain(imports.Sources, source => source.Path.StartsWith("apis/", StringComparison.Ordinal)
            || source.Path.StartsWith("injected-apis/", StringComparison.Ordinal));
        Assert.Equal(lockBytes, await File.ReadAllBytesAsync(lockPath));

        // A selected/cached contract from another artifact must not fill a hole
        // in the originating artifact's pinned reference closure. Exercise this
        // guard directly; public cache resolution also verifies every sidecar.
        var artifact = selected.PackageArtifacts["example.models"];
        var projections = imports.Contracts.ToDictionary(contract => contract.ContractKey, contract => contract.ModelProjection, StringComparer.Ordinal);
        var missingTarget = artifact with { Contracts = artifact.Contracts.Where(pair => pair.Key != "example.models.target")
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) };
        var foreign = selected with
        {
            PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact>
            { [artifact.PackageId] = missingTarget, ["example.foreign"] = artifact with { PackageId = "example.foreign" } },
            ProviderImports = new Dictionary<string, SunderRpcLockedProviderImport>(selected.ProviderImports, StringComparer.Ordinal)
            { ["foreignTarget"] = new("foreignTarget", "example.foreign", "example.models.target") },
        };
        Assert.Contains("service-reference-target-unselected", Assert.Throws<InvalidDataException>(() =>
            SunderRpcContractCache.GetMaterializationRequirements(foreign, projections)).Message, StringComparison.Ordinal);
        var unreviewed = selected with
        {
            PackageArtifacts = new Dictionary<string, SunderRpcLockedPackageArtifact>
            {
                [artifact.PackageId] = artifact with { Contracts = artifact.Contracts.ToDictionary(pair => pair.Key,
                    pair => pair.Key == "example.models.target" ? pair.Value with { AuthorityId = "example.unreviewed" } : pair.Value, StringComparer.Ordinal) },
            },
        };
        Assert.Contains("authority-release-conflict", Assert.Throws<InvalidDataException>(() =>
            SunderRpcContractCache.GetMaterializationRequirements(unreviewed, projections)).Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task ProviderOnlyUpdate_RequiresCompletePinsAndExplicitTypedDiscovery()
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-provider-reference-update");
        var root = ResolvePhysicalTemporaryPath(scope.CreateDirectory("consumer"));
        var cache = ResolvePhysicalTemporaryPath(scope.CreateDirectory("cache"));
        var previous = ReferenceClosureDeclaration.Replace("HolderContext(ServiceReference<Api.ITargetService> Target)", "HolderContext(string Text)", StringComparison.Ordinal);
        var candidate = await WriteProjectedCandidateAsync(scope.CreateDirectory("candidate-v1"), "1.0.0", previous);
        var next = await WriteProjectedCandidateAsync(scope.CreateDirectory("candidate-v2"), "2.0.0", ReferenceClosureDeclaration);
        var project = await CreateProjectedConsumerAsync(root, cache, """
            using Sunder.Sdk.Packaging;
            using Sunder.Sdk.Services;
            using Sunder.Generated.Packages.Example_Models_Models;
            using Sunder.Generated.Packages.Example_Models_Services.CExample_Models_Holder;
            [assembly: SunderPackage(Id = "example.consumer", Name = "Provider-only consumer")]
            [SunderServiceProvider(typeof(IHolderService), ProviderId = "example.consumer.holder")]
            public sealed partial class HolderProvider : IHolderService
            {
                public ValueTask<bool> HandleAsync(HolderContext request, CancellationToken cancellationToken = default)
                    => ValueTask.FromResult(true);
            }
            """, app: false);
        AssertSuccess(await RunDotnetAsync(root, ["restore", project, "--nologo"]));
        AssertSuccess(await Select("Implement", candidate, "-p:SunderPackageVersionRange=>=1.0.0 <3.0.0", "-p:SunderContractId=example.models.holder", "-p:SunderPackageApiLocalName=holder"));
        AssertSuccess(await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet"]));
        var lockPath = Path.Combine(root, SunderRpcAuthoringDocuments.ContractLockFileName);
        var reviewedLock = await File.ReadAllBytesAsync(lockPath);
        var incomplete = JsonNode.Parse(reviewedLock)!;
        var artifact = incomplete["packageArtifacts"]!["example.models"]!.AsObject();
        artifact.Remove("modelProjectionBytes"); artifact.Remove("modelProjectionSha256");
        var incompleteLock = SunderRpcAuthoringDocuments.WriteDeterministicDocument(JsonSerializer.SerializeToElement(incomplete));
        await File.WriteAllBytesAsync(lockPath, incompleteLock);
        var source = await File.ReadAllBytesAsync(Path.Combine(root, "Consumer.cs"));
        var dev = Path.Combine(root, "bin/Debug/net10.0/sunder-dev"); var devHash = HashTree(dev);
        var rejected = await Select("Update", next);
        Assert.NotEqual(0, rejected.ExitCode);
        Assert.Contains("package-api.model-projection-invalid", rejected.StandardOutput + rejected.StandardError, StringComparison.Ordinal);
        Assert.Equal(incompleteLock, await File.ReadAllBytesAsync(lockPath));
        Assert.Equal(source, await File.ReadAllBytesAsync(Path.Combine(root, "Consumer.cs")));
        Assert.Equal(devHash, HashTree(dev));
        Assert.Empty(Directory.GetFiles(root, "sunder-contracts.lock.json.stage-*"));

        // Restore authentic reviewed inputs before exercising a normal candidate update.
        await File.WriteAllBytesAsync(lockPath, reviewedLock);
        var update = await Select("Update", next);
        Assert.NotEqual(0, update.ExitCode);
        var output = update.StandardOutput + update.StandardError;
        Assert.Contains("rpc-authoring.service-reference-use-missing", output, StringComparison.Ordinal);
        Assert.Contains("example.models.target", output, StringComparison.Ordinal);
        Assert.DoesNotContain("service target must resolve exactly once", output, StringComparison.Ordinal);
        Assert.DoesNotContain("CS0246", output, StringComparison.Ordinal);
        Assert.Equal(reviewedLock, await File.ReadAllBytesAsync(lockPath));
        Assert.Equal(source, await File.ReadAllBytesAsync(Path.Combine(root, "Consumer.cs")));
        Assert.Equal(devHash, HashTree(dev));
        Assert.Empty(Directory.GetFiles(root, "sunder-contracts.lock.json.stage-*"));

        // The provider explicitly declares discovery for the new reference target.
        // Target is newly materialized by the staged candidate;
        // the old provider-only lock deliberately cannot resolve it yet.
        var sourcePath = Path.Combine(root, "Consumer.cs");
        await File.WriteAllTextAsync(sourcePath, System.Text.Encoding.UTF8.GetString(source).Replace(
            "[SunderServiceProvider", "[SunderRuntimeUse(typeof(Sunder.Generated.Packages.Example_Models_Services.CExample_Models_Target.ITargetService), Actions = [\"discover\"])]\n[SunderServiceProvider", StringComparison.Ordinal));
        var intendedSource = await File.ReadAllBytesAsync(sourcePath);
        AssertSuccess(await Select("Update", next));
        var selected = SunderRpcAuthoringDocuments.ReadContractLock(lockPath);
        Assert.Empty(selected.PackageImports);
        Assert.Equal("holder", Assert.Single(selected.ProviderImports).Key);
        Assert.Equal("example.models.holder", selected.ProviderImports["holder"].ContractId);
        Assert.Equal("2.0.0", selected.PackageArtifacts["example.models"].SelectedPackageVersion);
        Assert.Equal(">=1.0.0 <3.0.0", selected.PackageArtifacts["example.models"].RequestedVersionRange);
        Assert.Equal(intendedSource, await File.ReadAllBytesAsync(sourcePath));
        AssertSuccess(await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet"]));
        var validated = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(dev);
        Assert.True(validated.Success, string.Join(Environment.NewLine, validated.Errors));
        var manifest = validated.Manifest!;
        Assert.Empty(manifest.PackageApiImports!);
        Assert.Single(manifest.ProviderImports!);
        Assert.Equal("required", Assert.Single(manifest.DependsOn!).Presence);
        Assert.Equal(["example.models.holder", "example.models.leaf", "example.models.target"], manifest.ContractBundles!.Select(bundle => bundle!.ContractId).Order(StringComparer.Ordinal));
        var use = Assert.Single(manifest.UsesContracts!)!;
        Assert.Equal("runtime", use.Role); Assert.Equal("example.models.target", use.ContractId);
        Assert.False(use.Required); Assert.Equal(["discover"], use.Actions);
        var receiptPath = Directory.GetFiles(Path.Combine(root, "obj"), SunderRpcAuthoringDocuments.AuthoringReceiptFileName, SearchOption.AllDirectories)
            .First(path => !path.Contains("sunder-package-role-plans", StringComparison.Ordinal));
        var receipt = JsonNode.Parse(await File.ReadAllBytesAsync(receiptPath))!;
        var intent = Assert.Single(receipt["roleUses"]!.AsArray())!;
        Assert.Equal("example.models.target", intent["contractId"]!.GetValue<string>());
        Assert.Equal(["discover"], intent["actions"]!.AsArray().Select(action => action!.GetValue<string>()));
        var exportPath = Path.Combine(Path.GetDirectoryName(receiptPath)!, SunderRpcAuthoringDocuments.PackageExportFileName);
        var export = JsonNode.Parse(await File.ReadAllBytesAsync(exportPath))!;
        Assert.True(JsonNode.DeepEquals(receipt["roleUses"], export["roleUses"]));
        Assert.Equal(receipt["inputFingerprint"]!.GetValue<string>(), export["inputFingerprint"]!.GetValue<string>());

        // Removal changes source evidence and must fail normally, preserving the
        // last published output rather than inheriting the preceding receipt.
        var qualifiedDev = HashTree(dev);
        await File.WriteAllBytesAsync(sourcePath, source);
        var removed = await RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet"]);
        Assert.NotEqual(0, removed.ExitCode);
        Assert.Contains("rpc-authoring.service-reference-use-missing", removed.StandardOutput + removed.StandardError, StringComparison.Ordinal);
        Assert.Equal(qualifiedDev, HashTree(dev));

        Task<ProcessResult> Select(string operation, string archive, params string[] options) => RunDotnetAsync(root,
            ["msbuild", project, "-t:" + operation + "SunderPackageApi", "-nodeReuse:false", "-nologo", "-v:minimal",
                "-p:SunderPackageId=example.models", "-p:SunderPackageCandidatePath=" + archive, .. options]);
    }

    private sealed class OfflineProjectionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Local pinned projection selection must not request network data.");
    }
}
