using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageManifestAdmissionPolicyTests
{
    public static TheoryData<string, string> UnsupportedMetadata => new()
    {
        { "\"packageArtifactImports\": [{}]", "Package artifact import" },
        { "\"packageApiImports\": [{}]", "Package API import" },
        { "\"providerImports\": [{}]", "Provider import" },
        { "\"platformImports\": [{}]", "Platform import" },
        { "\"contractBundles\": [{\"source\":\"imported\",\"authorityKind\":\"package\",\"authorityId\":\"other.package\"}]", "Contract bundle" },
        { "\"contractBundles\": [{\"authorityKind\":\"package\",\"authorityId\":\"test.package\"}]", "Contract bundle" },
        { "\"contractBundles\": [{\"source\":\"Owned\",\"authorityKind\":\"package\",\"authorityId\":\"test.package\"}]", "Contract bundle" },
        { "\"contractBundles\": [{\"source\":\"owned\",\"authorityId\":\"test.package\"}]", "package authority" },
        { "\"contractBundles\": [{\"source\":\"owned\",\"authorityKind\":\"package\"}]", "package authority" },
        { "\"contractBundles\": [{\"source\":\"owned\",\"authorityKind\":\"platform\",\"authorityId\":\"test.package\"}]", "package authority" },
        { "\"contractBundles\": [{\"source\":\"owned\",\"authorityKind\":\"package\",\"authorityId\":\"other.package\"}]", "package authority" },
        { "\"contractBundles\": [{\"source\":\"owned\",\"authorityKind\":\"package\",\"authorityId\":\"Test.Package\"}]", "package authority" },
        { "\"contractBundles\": [{\"source\":\"owned\",\"authorityKind\":\"package\",\"authorityId\":\"test.package\",\"selectedAuthorityRelease\":\"1.0.0\"}]", "forbids selected-authority" },
        { "\"contractBundles\": [{\"source\":\"owned\",\"authorityKind\":\"package\",\"authorityId\":\"test.package\",\"selectedAuthorityContentIdentity\":\"pin\"}]", "forbids selected-authority" },
        { "\"dependsOn\": [{\"packageId\":\"other.package\",\"versionRange\":\">=1.0.0\"}]", "explicitly declare" },
        { "\"dependsOn\": [{\"packageId\":\"other.package\",\"versionRange\":\">=1.0.0\",\"presence\":null}]", "explicitly declare" },
        { "\"dependsOn\": [{\"packageId\":\"other.package\",\"versionRange\":\">=1.0.0\",\"presence\":\"Required\"}]", "explicitly declare" },
    };

    [Fact]
    public void EnsureSupported_AcceptsExplicitOwnedAuthorityRequiredDependenciesAndEmptyGroups()
    {
        var manifest = new SunderPackageManifest
        {
            Id = "test.package",
            ContractBundles =
            [
                new SunderPackageContractBundleManifest
                {
                    Source = "owned",
                    AuthorityKind = "package",
                    AuthorityId = "test.package",
                },
            ],
            DependsOn =
            [
                new SunderPackageDependencyManifest
                {
                    PackageId = "other.package",
                    VersionRange = ">=1.0.0",
                    Presence = "required",
                },
            ],
            PackageArtifactImports = [],
            PackageApiImports = [],
            ProviderImports = [],
            PlatformImports = [],
        };

        PackageManifestAdmissionPolicy.EnsureSupported(manifest);
    }

    [Fact]
    public async Task OptionalDependencyNeedsAuthorityBeforeDevelopmentPreparationSucceeds()
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-optional-dependency-admission-tests");
        var source = temporary.GetPath("source");
        CanonicalPackageTestBuilder.WriteExplodedPackage(
            source, "test.package", "1.0.0", typeof(PackageManifestAdmissionPolicyTests).Assembly.Location,
            roles: [SunderPackageFormat.AppHostRole],
            dependencies: [new InstalledPackageDependencyRecord("other.package", ">=1.0.0")],
            runtimeIdentifiers: [PackageTargetSelection.GetCurrentRuntimeIdentifier()]);
        var manifestPath = Path.Combine(source, "manifest", "sunder-package.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["dependsOn"]![0]!["presence"] = "optional";
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        CanonicalPackageTestBuilder.WriteContentIndex(source);
        var validation = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(source);
        Assert.True(validation.Success, string.Join(" | ", validation.Errors));

        var errors = new List<string>();
        var session = temporary.GetPath("session");
        var prepared = await new PackageSessionPreparer(PackageTargetSelection.GetCurrentRuntimeIdentifier()).PrepareDevPackageAsync(
            0, source, session, errors, CancellationToken.None);
        Assert.Null(prepared);
        Assert.Contains(errors, error => error.Contains("package.policy-required", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("prepare-imports", StringComparison.Ordinal));

        var archivePath = temporary.GetPath("optional.sunderpkg");
        ZipFile.CreateFromDirectory(source, archivePath);
        var paths = new RuntimePackagePaths(temporary.GetPath("store"));
        var coordinator = new PackageStoreCoordinator(paths, new(paths), new(paths), packagePolicies: new(paths, new PolicyContinuousClock()));
        var result = await coordinator.PrepareStageAsync([new PackageStoreMutation(PackageStoreMutationKind.Install, ArchiveFilePath: archivePath)]);
        Assert.False(result.Success);
        Assert.Contains("package.policy-required", result.Failure!.Message!);
        Assert.False(Directory.Exists(paths.GetInstalledPackagePath("test.package", "1.0.0")));
    }

    [Theory]
    [MemberData(nameof(UnsupportedMetadata))]
    public async Task ProjectionAndRpcAdmission_RejectBeforeMaterializationOrContractLoading(
        string metadata,
        string expectedError)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-manifest-admission-tests");
        var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(ManifestJson(metadata))!;
        var contentIndex = new SunderPackageContentIndex(SunderPackageFormat.CurrentContentIndexVersion, []);
        var sourceRoot = temporary.GetPath("missing-source");
        var destination = temporary.GetPath("projection");
        var source = new RuntimePackageSource(
            "test.package", PackageSourceKind.Installed, sourceRoot,
            Manifest: manifest, ContentIndex: contentIndex);

        var projectionError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackageProjectionMaterializer.MaterializeCurrentRidInstallPackageAsync(
                sourceRoot, destination, manifest, contentIndex, "win-x64", CancellationToken.None));
        Assert.Contains(expectedError, projectionError.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(destination));

        var runtimeError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            PackageSessionPreparer.LoadRpcContractsAsync(sourceRoot, manifest, CancellationToken.None));
        Assert.Contains(expectedError, runtimeError.Message, StringComparison.Ordinal);
        var appError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RuntimeRpcPackageMetadata.LoadContractsAsync(source, CancellationToken.None));
        Assert.Contains(expectedError, appError.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnsupportedMetadata))]
    public async Task TrustedInstalledMetadata_RejectsBeforeActivationEvenWithMatchingReceipt(
        string metadata,
        string expectedError)
    {
        using var temporary = TemporaryDirectoryScope.Create("sunder-manifest-receipt-tests");
        var manifestPath = temporary.WriteAllText(SunderPackageFormat.ManifestPath, ManifestJson(metadata));
        CanonicalPackageTestBuilder.WriteContentIndex(temporary.RootPath);
        var contentIndex = JsonSerializer.Deserialize<SunderPackageContentIndex>(
            File.ReadAllText(temporary.GetPath(SunderPackageFormat.ContentIndexPath)))!;
        // Deliberately bypass Format to exercise the receipt fast path, without repairing missing fields.
        var installed = new InstalledPackageRecord(
            "test.package", "Test Package", null, "1.0.0", null,
            temporary.RootPath, manifestPath,
            PackageSessionPreparer.ComputeContentIdentity(temporary.RootPath),
            contentIndex.Files!.Select(entry => new InstalledPackageContentRecord(
                entry!.Path!, entry.Sha256!, entry.Size)).ToArray(),
            [], true, DateTimeOffset.UtcNow, InstalledPackageProvenanceRecord.Unknown, []);
        var errors = new List<string>();

        var activation = await new PackageSessionPreparer("win-x64").ReadInstalledActivationStateAsync(
            installed, temporary.RootPath, errors, CancellationToken.None);

        Assert.Null(activation);
        Assert.Contains(
            metadata.Contains("\"presence\":null", StringComparison.Ordinal) ? "Explicit JSON null" : expectedError,
            Assert.Single(errors), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnsupportedMetadata))]
    public void PermissionSubjects_RejectUnsupportedMetadataBeforeCreatingGrants(string metadata, string expectedError)
    {
        var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(ManifestJson(metadata))!;
        var source = new RuntimePackageSource(
            "test.package", PackageSourceKind.Installed, "unused",
            Manifest: manifest,
            ContentIndex: new SunderPackageContentIndex(
                SunderPackageFormat.CurrentContentIndexVersion,
                [new SunderPackageContentIndexEntry(SunderPackageFormat.ManifestPath, new string('a', 64), 1)]));
        var session = new ActivePackageSession(
            null,
            new Dictionary<string, ActiveLoadedPackage>(),
            new Dictionary<string, SessionPackageDescriptor>
            {
                ["test.package"] = PackageProtocolMapper.BuildSessionDescriptor(
                    new RuntimePackageActivationState(
                        "test.package", "Test Package", "1.0.0", PackageHostRoles.App, null),
                    true, PackageReadinessState.Ready),
            },
            readySources: [source]);

        var exception = Assert.Throws<InvalidDataException>(() => RuntimeRpcPermissionSubjects.FromSession(session));

        Assert.Contains(expectedError, exception.Message, StringComparison.Ordinal);
    }

    private static string ManifestJson(string metadata) => $$"""
        {
          "archiveFormatVersion": 1,
          "manifestVersion": 1,
          "id": "test.package",
          "name": "Test Package",
          "version": "1.0.0",
          "targets": [],
          "usesContracts": [{
            "role": "app",
            "contractId": "example.rpc",
            "contractVersion": "1.0.0",
            "abiSha256": "{{new string('a', 64)}}",
            "required": true,
            "actions": ["discover", "invoke"]
          }],
          {{metadata}}
        }
        """;
}
