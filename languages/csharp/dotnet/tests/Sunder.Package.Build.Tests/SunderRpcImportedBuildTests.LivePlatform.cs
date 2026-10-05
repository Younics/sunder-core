using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Build.Rpc.Authoring;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcImportedBuildTests
{
    [ExternalArtifactFact("SUNDER_REQUIRE_LIVE_PLATFORM_TOOLING")]
    public async Task LivePlatformRegistry_SelectRestoreAndBuildRealStackFromSelectedRegistry()
    {
        var inputPath = Environment.GetEnvironmentVariable("SUNDER_LIVE_PLATFORM_INPUT")!;
        Assert.InRange(new FileInfo(inputPath).Length, 1, 32_768);
        using var input = JsonDocument.Parse(await File.ReadAllBytesAsync(inputPath));
        var origin = input.RootElement.GetProperty("registryOrigin").GetString()!;
        var fingerprint = input.RootElement.GetProperty("certificateSha256").GetString()!;
        var output = input.RootElement.GetProperty("csharpOutput").GetString()!;
        Assert.Equal("localhost", new Uri(origin).Host); Assert.Equal("https", new Uri(origin).Scheme);
        File.Copy(Fixture("authoring/conformance/valid/minimal-contract-lock.json"), LockPath, overwrite: true);
        File.Delete(Path.Combine(_root, "Consumer.cs"));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate is not null
                && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0
                && Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)) == fingerprint };
        using var http = new HttpClient(handler);
        var restore = new SunderRpcContractRestore(http); var commands = new SunderRpcPackageApiCommands(restore);
        if (Environment.GetEnvironmentVariable("SUNDER_LIVE_PLATFORM_PHASE") == "revoked")
        {
            var retainedLock = Path.Combine(output, "sunder-contracts.lock.json"); var before = File.ReadAllBytes(retainedLock);
            var failure = await Assert.ThrowsAsync<InvalidDataException>(() => commands.ExecuteAsync(retainedLock, _cache, new("PlatformUpdate")));
            Assert.Contains("platform-api.import-not-found", failure.Message);
            Assert.Equal(1, await restore.RestoreAsync(retainedLock, _cache, []));
            var retainedCatalog = Assert.Single(SunderRpcPlatformCatalogCache.Resolve(SunderRpcAuthoringDocuments.ReadContractLock(retainedLock), _cache)).Value;
            Assert.Equal(File.ReadAllBytes(Path.Combine(output, "catalog.json")), retainedCatalog.GetCanonicalUtf8());
            Assert.Equal(before, File.ReadAllBytes(retainedLock)); return;
        }
        var request = new SunderRpcPackageApiRequest("PlatformImplement", ContractId: "sunder.stack.contributor", RegistryOrigin: origin);
        var added = await commands.ExecuteAsync(LockPath, _cache, request,
            (before, _, stage) => CreatePackageApiTask(request.Operation).ValidateSource(before, stage, _cache, request));
        Assert.True(added.Changed); Assert.Empty(added.ContractLock.PackageArtifacts);
        var selected = Assert.Single(added.ContractLock.PlatformImports).Value;
        Assert.Equal("38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898", selected.DescriptorSha256);
        var locked = File.ReadAllBytes(LockPath); var modified = File.GetLastWriteTimeUtc(LockPath);
        Assert.False((await commands.ExecuteAsync(LockPath, _cache, request)).Changed);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(LockPath));
        Assert.Equal(1, await restore.RestoreAsync(LockPath, _cache, []));
        var catalog = Assert.Single(SunderRpcPlatformCatalogCache.Resolve(added.ContractLock, _cache)).Value;
        Assert.Equal(input.RootElement.GetProperty("catalogSha256").GetString(), catalog.ContentIdentity);
        // The production generator consumes retained authority offline. Network is
        // deliberately disposed before source generation and release packing.
        http.Dispose();
        File.WriteAllText(Path.Combine(_root, "Provider.cs"), StackDomainProviderSource);
        File.WriteAllText(Path.Combine(_root, "Program.cs"), WorkerSource.Replace("new SunderWorkerOptions([])",
            "new SunderWorkerOptions([new LocalStack().CreateSunderRpcProviderRegistration()])", StringComparison.Ordinal));
        var generation = CreateStackGeneration(); AssertSuccess(generation);
        var freeze = CreateStackGeneration(); freeze.ContractOperation = "Freeze"; AssertSuccess(freeze);
        generation = CreateStackGeneration(); AssertSuccess(generation);
        var manifestTask = BuildManifest(generation, rid: RuntimeInformation.RuntimeIdentifier);
        var leaf = EmitLeaf(manifestTask, typeof(IPackageStackContributor).Assembly.Location);
        var aggregate = CreateAggregate(leaf); AssertSuccess(aggregate);
        Directory.CreateDirectory(output);
        var archive = Path.Combine(output, "stack.sunderpkg");
        AssertSuccess(new PackSunderPackageTask
        {
            BuildEngine = new BuildEngine(), ProjectDirectory = _root, DevPackagePath = aggregate.OutputPath, DevPackageDiscoveryRoot = aggregate.TargetDirectory,
            PackageOutputPath = archive, SourceFiles = generation.SourceFiles, ReferencePaths = generation.ReferencePaths, AuthoringReceipts = generation.AuthoringReceipts,
            IntermediateOutputPath = generation.IntermediateOutputPath, ContractCacheRoot = _cache, OutputType = "Exe",
            ProjectIdentity = generation.ProjectIdentity, Configuration = generation.Configuration, TargetFramework = generation.TargetFramework,
        });
        var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archive, Path.Combine(output, "sunder-dev"));
        Assert.True(validation.Success, string.Join(" | ", validation.Errors));
        Assert.Single(validation.Manifest!.PlatformImports!); Assert.Empty(validation.Manifest.UsesContracts!); Assert.Empty(validation.Manifest.DependsOn!);
        Assert.Equal(locked, File.ReadAllBytes(LockPath));
        File.Copy(LockPath, Path.Combine(output, "sunder-contracts.lock.json"));
        File.WriteAllBytes(Path.Combine(output, "catalog.json"), catalog.GetCanonicalUtf8());
    }
}
