using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeStackContractMapperTests
{
    [Fact]
    public void ContributorCatalog_RejectsDuplicateExporterItemIds()
    {
        var errors = new List<string>();
        var registration = new StackExporterRegistration("test.package", new DuplicateItemExporter());

        var valid = RuntimeStackContributorCatalog.ValidateExportItemIds(
            [
                new StackExportItemDescriptor("same", "One", "test"),
                new StackExportItemDescriptor("SAME", "Two", "test"),
            ],
            registration,
            errors);

        Assert.False(valid);
        Assert.Contains(errors, error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExportItemMapping_PreservesOnlyExplicitDetailSensitivity()
    {
        var mapped = RuntimeStackContractMapper.ToExportItem(
            "test.package",
            "test.contributor",
            new StackExportItemDescriptor(
                "profile",
                "Profile",
                "test",
                Details:
                [
                    new StackExportItemDetail("Public value", "value", StackValueSensitivity.Public),
                    new StackExportItemDetail("Secret value", "secret", StackValueSensitivity.Secret),
                    new StackExportItemDetail("Unclassified value", "other"),
                ]));

        Assert.Collection(
            mapped.Details!,
            detail => Assert.Equal("Public", detail.Sensitivity),
            detail => Assert.Equal("Secret", detail.Sensitivity),
            detail => Assert.Null(detail.Sensitivity));
    }

    [Fact]
    public void PackageRequirements_UseStrongestSemanticMinimum()
    {
        var mapped = RuntimeStackContractMapper.ToPackageRequirements(
        [
            new StackPackageRequirement("test.package", "latest", MinimumVersion: "1.10.0", Required: false),
            new StackPackageRequirement("test.package", "LATEST", MinimumVersion: "2.0.0-beta.1"),
            new StackPackageRequirement("test.package", "latest", MinimumVersion: "2.0.0"),
        ]);

        var requirement = Assert.Single(mapped);
        Assert.Equal("2.0.0", requirement.MinimumVersion);
        Assert.True(requirement.Required);
    }

    [Fact]
    public void PackageRequirements_RejectConflictingInstallTags()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            RuntimeStackContractMapper.ToPackageRequirements(
            [
                new StackPackageRequirement("test.package", "latest"),
                new StackPackageRequirement("test.package", "preview"),
            ]));

        Assert.Contains("conflicting install tags", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryInstallPlanMapping_PreservesRequirementPolicyAndFailureCode()
    {
        var registryRequest = Assert.Single(RuntimeRegistryContractMapper.ToRegistry(
        [
            new RuntimeRegistryPackageChangeRequest(
                "test.package",
                Version: null,
                Tag: "preview",
                VersionRange: ">=2.3.0",
                Required: false),
        ]));

        Assert.Equal("preview", registryRequest.Tag);
        Assert.Equal(">=2.3.0", registryRequest.VersionRange);
        Assert.False(registryRequest.Required);

        var runtimeResponse = RuntimeRegistryContractMapper.ToRuntime(new RegistryResolveInstallPlanResponse(
            false,
            [],
            [],
            [],
            [new RegistryPackageInstallPlanConflict(
                "test.package",
                "1.0.0",
                ">=2.3.0",
                null,
                RegistryV1ErrorCodes.PackageRequirementUnsatisfied,
                "The selected version is too old.")]));

        var conflict = Assert.Single(runtimeResponse.Conflicts);
        Assert.Equal(RegistryV1ErrorCodes.PackageRequirementUnsatisfied, conflict.ErrorCode);
        Assert.Equal(">=2.3.0", conflict.RequestedVersionRange);
    }

    [Fact]
    public void FragmentOwnership_IsStampedFromHostRegistration()
    {
        var exported = RuntimeStackContractMapper.OwnExportFragment(
            "host.package",
            "host.contributor",
            new StackFragmentExport(
                "fragment",
                "schema",
                1,
                "Fragment",
                "{}"));
        var imported = RuntimeStackContractMapper.OwnImportFragment(
            "host.package",
            "host.contributor",
            new StackFragmentImport(
                "fragment",
                "spoofed.package",
                "spoofed.contributor",
                "schema",
                1,
                "Fragment",
                "{}"));

        Assert.Equal("host.package", exported.OwnerPackageId);
        Assert.Equal("host.contributor", exported.ContributorId);
        Assert.Equal("host.package", imported.OwnerPackageId);
        Assert.Equal("host.contributor", imported.ContributorId);
    }

    [Fact]
    public async Task ExportArchiveBuilder_ConsumesStreamPayloadHandles()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(root);
        using var transfers = new RuntimeContentTransferStore(paths);
        var builder = new StackExportArchiveBuilder(transfers, paths, TimeProvider.System);
        var payload = new byte[] { 1, 2, 3, 4 };
        var fragment = new StackFragmentExport(
            "fragment",
            "schema",
            1,
            "Fragment",
            "{}",
            Files:
            [
                new StackExportPayloadHandle(
                    "nested/value.bin",
                    _ => ValueTask.FromResult<Stream>(new MemoryStream(payload, writable: false)),
                    payload.Length),
            ]);

        var response = await builder.BuildAsync(
            new RuntimeStackExportRequest("test.stack", "Test Stack", null, []),
            [],
            [RuntimeStackContractMapper.OwnExportFragment("host.package", "host.contributor", fragment)],
            new Dictionary<string, SunderStackFragmentPreview>(),
            generation: 4,
            [],
            CancellationToken.None);

        Assert.True(response.Success, string.Join(Environment.NewLine, response.Errors));
        Assert.NotNull(response.Download);
        var download = response.Download!;
        var lease = transfers.AcquireDownload(download.DownloadId, generation: 4);
        Assert.NotNull(lease);
        var extractionPath = Path.Combine(root, "extracted");
        var inspection = await SunderStackArchiveInspector.ExtractAndValidateAsync(
            lease!.FilePath,
            extractionPath);
        Assert.True(inspection.Success, string.Join(Environment.NewLine, inspection.Errors));
        var manifestFragment = Assert.Single(inspection.Manifest!.Fragments!);
        Assert.Equal("host.package", manifestFragment.OwnerPackageId);
        Assert.Equal("host.contributor", manifestFragment.ContributorId);
        Assert.Equal(
            payload,
            await File.ReadAllBytesAsync(Path.Combine(
                extractionPath,
                "payload",
                "files",
                "fragment",
                "nested",
                "value.bin")));
        transfers.ReleaseUpload(lease);
    }

    [Fact]
    public async Task ImportArchiveReader_ProvidesReadOnlyPayloadHandles()
    {
        var root = CreateTempDirectory();
        var stackPath = Path.Combine(root, "input.sunderstack");
        var fragmentPayload = Path.Combine(root, "fragment.json");
        var filePayload = Path.Combine(root, "value.bin");
        await File.WriteAllTextAsync(fragmentPayload, "{}");
        await File.WriteAllBytesAsync(filePayload, [1, 2, 3]);
        await SunderStackArchiveWriter.WriteAsync(
            new SunderStackManifest
            {
                SchemaVersion = SunderStackFormat.CurrentSchemaVersion,
                MinReaderVersion = SunderStackFormat.CurrentReaderVersion,
                StackId = "test.stack",
                Name = "Test Stack",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Packages = [],
                Fragments =
                [
                    new SunderStackFragmentManifest
                    {
                        FragmentId = "fragment",
                        OwnerPackageId = "host.package",
                        ContributorId = "host.contributor",
                        SchemaId = "schema",
                        SchemaVersion = 1,
                        DisplayName = "Fragment",
                        DefaultSelected = true,
                        PayloadPath = "payload/fragments/fragment.json",
                    },
                ],
            },
            stackPath,
            new Dictionary<string, string>
            {
                ["payload/fragments/fragment.json"] = fragmentPayload,
                ["payload/files/fragment/value.bin"] = filePayload,
            });

        var loaded = await new StackImportArchiveReader().LoadAsync(
            stackPath,
            ["fragment"],
            Path.Combine(root, "extracted"),
            CancellationToken.None);

        Assert.True(loaded.Success, string.Join(Environment.NewLine, loaded.Errors));
        var file = Assert.Single(Assert.Single(loaded.Fragments).Files!);
        Assert.Equal("value.bin", file.RelativePath);
        Assert.Null(file.GetType().GetProperty("ExtractedPath"));
        await using var stream = await file.OpenReadAsync(CancellationToken.None);
        using var content = new MemoryStream();
        await stream.CopyToAsync(content);
        Assert.Equal(new byte[] { 1, 2, 3 }, content.ToArray());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("fragment/escape")]
    [InlineData("Fragment")]
    public async Task ExportArchiveBuilder_RejectsFragmentIdsBeforeMaterialization(string fragmentId)
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(root);
        using var transfers = new RuntimeContentTransferStore(paths);
        var builder = new StackExportArchiveBuilder(transfers, paths, TimeProvider.System);
        var fragment = new StackFragmentExport(fragmentId, "schema", 1, "Fragment", "{}");

        var response = await builder.BuildAsync(
            new RuntimeStackExportRequest("test.stack", "Test Stack", null, []),
            [],
            [RuntimeStackContractMapper.OwnExportFragment("host.package", "host.contributor", fragment)],
            new Dictionary<string, SunderStackFragmentPreview>(),
            generation: 1,
            [],
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.Contains(response.Errors, error => error.Contains("fragment id", StringComparison.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(Path.Combine(root, "escape")));
    }

    [Fact]
    public async Task ExportArchiveBuilder_RejectsUnsafePayloadPathBeforeOpeningStream()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(root);
        using var transfers = new RuntimeContentTransferStore(paths);
        var builder = new StackExportArchiveBuilder(transfers, paths, TimeProvider.System);
        var opened = false;
        var fragment = new StackFragmentExport(
            "fragment",
            "schema",
            1,
            "Fragment",
            "{}",
            Files: [new StackExportPayloadHandle("../escape", _ =>
            {
                opened = true;
                return ValueTask.FromResult<Stream>(new MemoryStream());
            })]);

        var response = await builder.BuildAsync(
            new RuntimeStackExportRequest("test.stack", "Test Stack", null, []),
            [],
            [RuntimeStackContractMapper.OwnExportFragment("host.package", "host.contributor", fragment)],
            new Dictionary<string, SunderStackFragmentPreview>(),
            generation: 1,
            [],
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.False(opened);
        Assert.Contains(response.Errors, error => error.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExportArchiveBuilder_RejectsDeclaredPayloadOverPerFileLimitBeforeOpeningStream()
    {
        var root = CreateTempDirectory();
        var paths = new RuntimePackagePaths(root);
        using var transfers = new RuntimeContentTransferStore(paths);
        var policy = new RuntimeStackPolicyOptions { MaxExportPayloadFileBytes = 4, MaxExportPayloadTotalBytes = 8 };
        var builder = new StackExportArchiveBuilder(transfers, paths, TimeProvider.System, policy);
        var opened = false;
        var fragment = new StackFragmentExport(
            "fragment",
            "schema",
            1,
            "Fragment",
            "{}",
            Files: [new StackExportPayloadHandle("value.bin", _ =>
            {
                opened = true;
                return ValueTask.FromResult<Stream>(new MemoryStream());
            }, length: 5)]);

        var response = await builder.BuildAsync(
            new RuntimeStackExportRequest("test.stack", "Test Stack", null, []),
            [],
            [RuntimeStackContractMapper.OwnExportFragment("host.package", "host.contributor", fragment)],
            new Dictionary<string, SunderStackFragmentPreview>(),
            generation: 1,
            [],
            CancellationToken.None);

        Assert.False(response.Success);
        Assert.False(opened);
        Assert.Contains(response.Errors, error => error.Contains("per-file limit", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "sunder-runtime-stack-mapper-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class DuplicateItemExporter : IPackageStackExporter
    {
        public string ContributorId => "test.exporter";
        public string DisplayName => "Test";

        public ValueTask<IReadOnlyList<StackExportItemDescriptor>> ListExportItemsAsync(
            StackExportDiscoveryContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<StackExportItemDescriptor>>([]);

        public ValueTask<StackExportContribution> ExportAsync(
            StackExportRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new StackExportContribution([], [], []));
    }
}
