using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Package.Format;
using Sunder.Sdk.Compatibility;
using Xunit;

namespace Sunder.App.Tests;

public sealed class LocalPackageInstallConsentViewModelTests
{
    [Fact]
    public async Task ReviewSnapshot_RemainsBoundToCopiedBytesWhenSourceChanges()
    {
        var sourceDirectory = TestSupport.TestPaths.CreateTempDirectory();
        var sourcePath = Path.Combine(sourceDirectory, "sample.sunderpkg");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
        var snapshotPath = await PackageArchivePicker.CreateReviewSnapshotAsync(
            sourcePath,
            CancellationToken.None);
        try
        {
            await File.WriteAllBytesAsync(sourcePath, [9, 9, 9]);

            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(snapshotPath));
        }
        finally
        {
            PackageArchivePicker.DeleteReviewSnapshot(snapshotPath);
        }
    }

    [Fact]
    public void Valid_ProjectsIdentityFeaturesAndDeclaredPublicServiceAccess()
    {
        var manifest = new SunderPackageManifest
        {
            ArchiveFormatVersion = SunderPackageFormat.CurrentArchiveFormatVersion,
            ManifestVersion = SunderPackageFormat.CurrentManifestVersion,
            Id = "sample.package",
            Name = "Sample Package",
            Version = "1.2.3",
            Summary = "A sample package.",
            DependsOn = [],
            Targets =
            [
                new SunderPackageTargetManifest
                {
                    Role = "runtime",
                    Rid = AppPackageTargetEnvironment.CurrentRid,
                    RequiredHostCapabilities = [SunderSdkCapabilities.DockerEnvironmentV1],
                },
                new SunderPackageTargetManifest { Role = "app", Rid = AppPackageTargetEnvironment.CurrentRid, RequiredHostCapabilities = ["core.v1"] },
            ],
            UsesContracts =
            [
                new SunderPackageContractUseManifest
                {
                    Role = SunderPackageFormat.RuntimeHostRole,
                    ContractId = "example.messages",
                    ContractVersion = "1.4.0+local",
                    AbiSha256 = new string('a', 64),
                    Required = true,
                    Actions = ["invoke", "discover"],
                },
            ],
            ContractBundles = [new SunderPackageContractBundleManifest { Source = "imported", ContractId = "example.messages", Version = "1.4.0+local", Exposure = "public" }],
            Provides = [],
        };
        var validation = new SunderPackageArchiveValidationResult(manifest, [], [])
        {
            ContentIndex = new SunderPackageContentIndex(1,
            [
                new SunderPackageContentIndexEntry(
                    SunderPackageFormat.ManifestPath,
                    new string('b', 64),
                    42),
            ]),
        };

        var viewModel = LocalPackageInstallConsentViewModel.Valid("sample.sunderpkg", validation);

        Assert.True(viewModel.IsValid);
        Assert.Equal("sample.package  1.2.3", viewModel.PackageIdentity);
        Assert.Equal("App + Runtime", viewModel.TargetText);
        Assert.True(viewModel.HasDockerEnvironmentAccess);
        Assert.Equal(new string('b', 64), viewModel.SensitiveCapabilityManifestSha256);
        var access = Assert.Single(viewModel.Information.ServiceAccess);
        Assert.Equal("Background worker", access.Scope);
        Assert.Equal("example.messages", access.ContractId);
        Assert.Equal("1.4.0+local", access.Version);
        Assert.Equal("Find services · Call services", access.Actions);
        Assert.True(Assert.Single(viewModel.Information.Features).IsSensitive);
    }

    [Fact]
    public void Invalid_DisablesInstallPresentationAndIncludesValidationErrors()
    {
        var viewModel = LocalPackageInstallConsentViewModel.Invalid(
            "broken.sunderpkg",
            ["Manifest is missing.", "Content index is invalid."]);

        Assert.True(viewModel.IsInvalid);
        Assert.False(viewModel.Information.IsLoaded);
        Assert.Contains("Manifest is missing.", viewModel.ValidationMessage, StringComparison.Ordinal);
        Assert.Contains("Content index is invalid.", viewModel.ValidationMessage, StringComparison.Ordinal);
    }
}
