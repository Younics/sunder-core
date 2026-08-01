using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Package.Format;
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
    public void Valid_ProjectsIdentityTargetsAndDeclaredRpcAccess()
    {
        var manifest = new SunderPackageManifest
        {
            Id = "sample.package",
            Name = "Sample Package",
            Version = "1.2.3",
            Summary = "A sample package.",
            Targets =
            [
                new SunderPackageTargetManifest { Role = "runtime" },
                new SunderPackageTargetManifest { Role = "app" },
            ],
            UsesContracts =
            [
                new SunderPackageContractUseManifest
                {
                    ContractId = "example.messages",
                    VersionRange = ">=1.0.0 <2.0.0",
                    Required = true,
                    Actions = ["invoke", "discover"],
                },
            ],
        };
        var validation = new SunderPackageArchiveValidationResult(manifest, [], [])
        {
            ContentIndex = new SunderPackageContentIndex(1, []),
        };

        var viewModel = LocalPackageInstallConsentViewModel.Valid("sample.sunderpkg", validation);

        Assert.True(viewModel.IsValid);
        Assert.Equal("sample.package  1.2.3", viewModel.PackageIdentity);
        Assert.Equal("App + Runtime", viewModel.TargetText);
        var access = Assert.Single(viewModel.RpcContractUses);
        Assert.Equal("example.messages", access.ContractId);
        Assert.Equal("discover | invoke", access.ActionsText);
        Assert.Equal("1 contract | 2 actions", viewModel.RpcAccessSummary);
    }

    [Fact]
    public void Invalid_DisablesInstallPresentationAndIncludesValidationErrors()
    {
        var viewModel = LocalPackageInstallConsentViewModel.Invalid(
            "broken.sunderpkg",
            ["Manifest is missing.", "Content index is invalid."]);

        Assert.True(viewModel.IsInvalid);
        Assert.Empty(viewModel.RpcContractUses);
        Assert.Contains("Manifest is missing.", viewModel.ValidationMessage, StringComparison.Ordinal);
        Assert.Contains("Content index is invalid.", viewModel.ValidationMessage, StringComparison.Ordinal);
    }
}
