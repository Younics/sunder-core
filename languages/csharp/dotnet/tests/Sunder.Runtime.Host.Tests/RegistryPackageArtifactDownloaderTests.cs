using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RegistryPackageArtifactDownloaderTests
{
    private const string Origin = "https://registry.example/";
    private const string PackageId = "test.projection.composition";
    private const string Version = "1.0.0";

    [Fact]
    public async Task DownloadAsync_FiltersContractUsesToSelectedRoles()
    {
        var root = CreateTempDirectory();
        try
        {
            var fixture = await CreateProjectionFixtureAsync(
                root,
                "role-filtering",
                sourceArchiveSha256: new string('a', 64),
                summary: "same source",
                includeContractUses: true);
            await using var provider = CreateProvider(root, fixture.ContentByUrl);

            var upload = await provider.GetRequiredService<RegistryPackageArtifactDownloader>().DownloadAsync(
                new Uri(Origin),
                CreatePlanItem(fixture.SharedArtifact, fixture.RuntimeArtifact, fixture.Targets),
                RuntimeSelection(),
                [],
                CancellationToken.None);

            var transfers = provider.GetRequiredService<RuntimeContentTransferStore>();
            var sessions = provider.GetRequiredService<PackageSessionLifecycleService>();
            var lease = transfers.AcquireUpload(
                upload.UploadId,
                RuntimeUploadKind.Package,
                sessions.Generation,
                consume: false);
            Assert.NotNull(lease);
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
                lease!.FilePath,
                Path.Combine(root, "composed"));

            Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
            Assert.Equal(2, fixture.Targets.Count);
            var target = Assert.IsType<SunderPackageTargetManifest>(Assert.Single(validation.Manifest!.Targets!));
            var use = Assert.IsType<SunderPackageContractUseManifest>(Assert.Single(validation.Manifest.UsesContracts!));
            Assert.Equal(SunderPackageFormat.RuntimeHostRole, target.Role);
            Assert.Equal(SunderPackageFormat.RuntimeHostRole, use.Role);
            transfers.DiscardUpload(upload.UploadId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_AcceptsCompleteSixRidInventoryAndSelectsOnlyCurrentRidProjection()
    {
        var root = CreateTempDirectory();
        try
        {
            var fixture = await CreateProjectionFixtureAsync(
                root,
                "universal",
                sourceArchiveSha256: new string('a', 64),
                summary: "universal",
                roles: [SunderPackageFormat.RuntimeHostRole],
                runtimeIdentifiers: SunderPackageFormat.SupportedRuntimeIdentifiers);
            await using var provider = CreateProvider(root, fixture.ContentByUrl);

            var upload = await provider.GetRequiredService<RegistryPackageArtifactDownloader>().DownloadAsync(
                new Uri(Origin),
                CreatePlanItem(fixture.SharedArtifact, fixture.RuntimeArtifact, fixture.Targets),
                RuntimeSelection(),
                [],
                CancellationToken.None);

            Assert.Equal(SunderPackageFormat.SupportedRuntimeIdentifiers.Count, fixture.Targets.Count);
            var transfers = provider.GetRequiredService<RuntimeContentTransferStore>();
            var sessions = provider.GetRequiredService<PackageSessionLifecycleService>();
            var lease = transfers.AcquireUpload(
                upload.UploadId,
                RuntimeUploadKind.Package,
                sessions.Generation,
                consume: false);
            Assert.NotNull(lease);
            var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
                lease!.FilePath,
                Path.Combine(root, "universal-composed"));

            Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
            var target = Assert.IsType<SunderPackageTargetManifest>(Assert.Single(validation.Manifest!.Targets!));
            Assert.Equal(SunderPackageFormat.RuntimeHostRole, target.Role);
            Assert.Equal(RuntimeInformation.RuntimeIdentifier, target.Rid);
            transfers.DiscardUpload(upload.UploadId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "sourceArchiveSha256")]
    [InlineData(true, "manifestSha256")]
    public async Task DownloadAsync_RejectsMixedProjectionSourceIdentity(
        bool mixManifest,
        string expectedIdentity)
    {
        var root = CreateTempDirectory();
        try
        {
            var first = await CreateProjectionFixtureAsync(
                root,
                "first",
                sourceArchiveSha256: new string('a', 64),
                summary: mixManifest ? "first manifest" : "same manifest");
            var second = await CreateProjectionFixtureAsync(
                root,
                "second",
                sourceArchiveSha256: mixManifest ? new string('a', 64) : new string('b', 64),
                summary: mixManifest ? "second manifest" : "same manifest");
            var contentByUrl = first.ContentByUrl
                .Concat(second.ContentByUrl)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            await using var provider = CreateProvider(root, contentByUrl);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                provider.GetRequiredService<RegistryPackageArtifactDownloader>().DownloadAsync(
                    new Uri(Origin),
                    CreatePlanItem(first.SharedArtifact, second.RuntimeArtifact, second.Targets),
                    RuntimeSelection(),
                    [],
                    CancellationToken.None));

            Assert.Contains(expectedIdentity, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("wrong-rid")]
    public async Task DownloadAsync_RejectsArtifactInventoryThatDoesNotExactlyMatchCurrentRidTargets(
        string mismatch)
    {
        var root = CreateTempDirectory();
        try
        {
            var fixture = await CreateProjectionFixtureAsync(
                root,
                "inventory",
                sourceArchiveSha256: new string('a', 64),
                summary: "inventory");
            var item = CreatePlanItem(fixture.SharedArtifact, fixture.RuntimeArtifact, fixture.Targets);
            item = mismatch switch
            {
                "missing" => item with { Artifacts = [fixture.SharedArtifact] },
                "extra" => item with { Artifacts = [fixture.SharedArtifact, fixture.RuntimeArtifact, fixture.RuntimeArtifact] },
                _ => item with
                {
                    Artifacts =
                    [
                        fixture.SharedArtifact,
                        fixture.RuntimeArtifact with { Rid = AlternateRid() },
                    ],
                },
            };
            await using var provider = CreateProvider(root, fixture.ContentByUrl);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                provider.GetRequiredService<RegistryPackageArtifactDownloader>().DownloadAsync(
                    new Uri(Origin),
                    item,
                    RuntimeSelection(),
                    [],
                    CancellationToken.None));

            Assert.Contains("install-plan", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsPlanTargetMetadataThatDiffersFromProjectionManifest()
    {
        var root = CreateTempDirectory();
        try
        {
            var fixture = await CreateProjectionFixtureAsync(
                root,
                "target-metadata",
                sourceArchiveSha256: new string('a', 64),
                summary: "target metadata");
            var item = CreatePlanItem(fixture.SharedArtifact, fixture.RuntimeArtifact, fixture.Targets) with
            {
                Targets = fixture.Targets.Select(target =>
                    target.Role == SunderPackageFormat.RuntimeHostRole
                    && target.Rid == RuntimeInformation.RuntimeIdentifier
                        ? target with { EntryPoint = "lib/not-the-worker" }
                        : target).ToArray(),
            };
            await using var provider = CreateProvider(root, fixture.ContentByUrl);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                provider.GetRequiredService<RegistryPackageArtifactDownloader>().DownloadAsync(
                    new Uri(Origin),
                    item,
                    RuntimeSelection(),
                    [],
                    CancellationToken.None));

            Assert.Contains("does not exactly match", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_RejectsIncompletePlanTargetMetadataInventory()
    {
        var root = CreateTempDirectory();
        try
        {
            var fixture = await CreateProjectionFixtureAsync(
                root,
                "incomplete-target-inventory",
                sourceArchiveSha256: new string('a', 64),
                summary: "incomplete target inventory");
            var item = CreatePlanItem(fixture.SharedArtifact, fixture.RuntimeArtifact, fixture.Targets) with
            {
                Targets = fixture.Targets
                    .Where(target => target.Role == SunderPackageFormat.RuntimeHostRole)
                    .ToArray(),
            };
            await using var provider = CreateProvider(root, fixture.ContentByUrl);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                provider.GetRequiredService<RegistryPackageArtifactDownloader>().DownloadAsync(
                    new Uri(Origin),
                    item,
                    RuntimeSelection(),
                    [],
                    CancellationToken.None));

            Assert.Contains("target inventory", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ProjectionFixture> CreateProjectionFixtureAsync(
        string root,
        string name,
        string sourceArchiveSha256,
        string summary,
        bool includeContractUses = false,
        IReadOnlyList<string>? roles = null,
        IReadOnlyList<string>? runtimeIdentifiers = null)
    {
        var artifactRoot = Path.Combine(root, "artifacts", name);
        var sourceRoot = Path.Combine(artifactRoot, "source");
        var descriptorBytes = CreateContractDescriptor();
        var contract = SunderRpcContractDescriptor.Parse(descriptorBytes);
        var contractBundle = new SunderPackageContractBundleManifest
        {
            ContractId = contract.ContractId,
            Version = contract.Version,
            DescriptorPath = "contracts/example.json",
            DescriptorSha256 = contract.Sha256,
            AbiSha256 = contract.AbiSha256,
            Exposure = SunderPackageFormat.PublicContractExposure,
        };
        var uses = includeContractUses
            ? new SunderPackageContractUseManifest?[]
            {
                CreateContractUse(SunderPackageFormat.AppHostRole, contract),
                CreateContractUse(SunderPackageFormat.RuntimeHostRole, contract),
            }
            : [];
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            sourceRoot,
            PackageId,
            Version,
            typeof(RegistryPackageArtifactDownloaderTests).Assembly.Location,
            roles: roles,
            runtimeIdentifiers: runtimeIdentifiers,
            contractBundles: [contractBundle],
            usesContracts: uses);

        var manifestPath = Path.Combine(sourceRoot, "manifest", "sunder-package.json");
        var manifestJson = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifestJson["summary"] = summary;
        await File.WriteAllTextAsync(
            manifestPath,
            manifestJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var contractPath = Path.Combine(sourceRoot, "payload", "shared", "contracts", "example.json");
        Directory.CreateDirectory(Path.GetDirectoryName(contractPath)!);
        await File.WriteAllBytesAsync(contractPath, descriptorBytes);
        CanonicalPackageTestBuilder.WriteContentIndex(sourceRoot);

        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(sourceRoot);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        var shared = await WriteProjectionAsync(
            artifactRoot,
            name,
            sourceRoot,
            validation,
            sourceArchiveSha256,
            SunderPackageProjectionKey.Shared);
        var runtime = await WriteProjectionAsync(
            artifactRoot,
            name,
            sourceRoot,
            validation,
            sourceArchiveSha256,
            SunderPackageProjectionKey.Runtime(RuntimeInformation.RuntimeIdentifier));
        var targets = validation.Manifest!.Targets!
            .Where(static target => target is not null)
            .Select(static target => ToRegistryTarget(target!))
            .OrderBy(static target => ProjectionOrder(target.Role))
            .ThenBy(static target => target.Rid, StringComparer.Ordinal)
            .ToArray();
        return new ProjectionFixture(
            shared.Artifact,
            runtime.Artifact,
            targets,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [shared.Artifact.DownloadUrl] = shared.Content,
                [runtime.Artifact.DownloadUrl] = runtime.Content,
            });
    }

    private static async Task<(RegistryPackageProjectionArtifact Artifact, byte[] Content)> WriteProjectionAsync(
        string artifactRoot,
        string name,
        string sourceRoot,
        SunderPackageArchiveValidationResult validation,
        string sourceArchiveSha256,
        SunderPackageProjectionKey key)
    {
        var path = Path.Combine(artifactRoot, $"{key.Kind}-{key.Rid ?? "none"}.sunderprojection");
        var descriptor = await SunderPackageProjectionArchiveWriter.WriteAsync(
            sourceRoot,
            validation.Manifest!,
            validation.ContentIndex!,
            sourceArchiveSha256,
            key,
            path);
        var content = await File.ReadAllBytesAsync(path);
        var downloadUrl = new Uri(
            new Uri(Origin),
            $"artifacts/{name}-{key.Kind}-{key.Rid ?? "none"}.sunderprojection").AbsoluteUri;
        return (
            new RegistryPackageProjectionArtifact(
                key.Kind,
                key.Rid,
                Hash(content),
                content.LongLength,
                downloadUrl,
                descriptor.SourceArchiveSha256!,
                descriptor.ManifestSha256!,
                descriptor.ProjectionSha256!,
                descriptor.ProjectionFormatVersion),
            content);
    }

    private static SunderPackageContractUseManifest CreateContractUse(
        string role,
        SunderRpcContractDescriptor contract)
        => new()
        {
            Role = role,
            ContractId = contract.ContractId,
            ContractVersion = contract.Version,
            AbiSha256 = contract.AbiSha256,
            Required = true,
            Actions = [SunderPackageFormat.InvokeContractAction],
        };

    private static RegistryPackageInstallPlanItem CreatePlanItem(
        RegistryPackageProjectionArtifact shared,
        RegistryPackageProjectionArtifact runtime,
        IReadOnlyList<RegistryPackageTarget> targets)
        => new(
            PackageId,
            CurrentVersion: null,
            Version,
            IsUpdate: false,
            DeprecatedMessage: null,
            DependsOn: [],
            Targets: targets,
            Artifacts: [shared, runtime]);

    private static IReadOnlyList<RegistryPackageTargetRequest> RuntimeSelection()
        => [new(SunderPackageFormat.RuntimeHostRole, RuntimeInformation.RuntimeIdentifier)];

    private static RegistryPackageTarget ToRegistryTarget(SunderPackageTargetManifest target)
        => new(
            target.Role!,
            target.Rid!,
            target.Kind!,
            target.EntryPoint!,
            target.TargetFramework,
            target.SdkVersion,
            (target.RequiredHostCapabilities ?? []).Select(static capability => capability!).ToArray(),
            (target.Views ?? []).Where(static view => view is not null).Select(static view => new RegistryPackageWebView(
                view!.ViewId!,
                view.DisplayName!,
                view.Route!,
                view.Icon,
                view.DefaultPlacement!,
                view.ShowInHotbar!.Value)).ToArray());

    private static string AlternateRid()
        => SunderPackageFormat.SupportedRuntimeIdentifiers.First(rid =>
            !string.Equals(rid, RuntimeInformation.RuntimeIdentifier, StringComparison.Ordinal));

    private static int ProjectionOrder(string role)
        => role == SunderPackageFormat.RuntimeHostRole ? 0 : 1;

    private static ServiceProvider CreateProvider(
        string root,
        IReadOnlyDictionary<string, byte[]> contentByUrl)
    {
        var services = new ServiceCollection();
        services.AddRuntimeHostServices(
            new RuntimePackagePaths(root),
            new RuntimeBearerTokenValidator("test-runtime-token"));
        services.AddHttpClient("registry").ConfigurePrimaryHttpMessageHandler(
            () => new ArtifactHandler(contentByUrl));
        return services.BuildServiceProvider();
    }

    private static byte[] CreateContractDescriptor()
        => Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion": 2,
              "schemaProfile": "sunder.rpc.values.v1",
              "contractId": "example.projection",
              "version": "1.0.0",
              "errors": {},
              "services": {
                "service": {
                  "methods": {
                    "call": {
                      "kind": "unary",
                      "idempotency": "none",
                      "errors": [],
                      "requestSchema": { "$ref": "#/$defs/Request" },
                      "responseSchema": { "$ref": "#/$defs/Response" }
                    }
                  }
                }
              },
              "$defs": {
                "Request": {
                  "type": "object",
                  "properties": {},
                  "required": [],
                  "additionalProperties": false
                },
                "Response": {
                  "type": "object",
                  "properties": {},
                  "required": [],
                  "additionalProperties": false
                }
              }
            }
            """);

    private static string Hash(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-projection-composition-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ArtifactHandler(IReadOnlyDictionary<string, byte[]> contentByUrl) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is null
                || !contentByUrl.TryGetValue(request.RequestUri.AbsoluteUri, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
                RequestMessage = request,
            });
        }
    }

    private sealed record ProjectionFixture(
        RegistryPackageProjectionArtifact SharedArtifact,
        RegistryPackageProjectionArtifact RuntimeArtifact,
        IReadOnlyList<RegistryPackageTarget> Targets,
        IReadOnlyDictionary<string, byte[]> ContentByUrl);
}
