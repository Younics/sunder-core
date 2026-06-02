using Sunder.App.Services;
using Sunder.PackageManagement;
using Xunit;

namespace Sunder.App.Tests;

public sealed class LocalStackLibraryServiceTests
{
    [Fact]
    public async Task ImportAsync_WhenStackIsValid_CopiesArchiveAndAddsLocalIndexEntry()
    {
        var root = CreateTempDirectory();
        var source = await CreateStackArchiveAsync(root);
        var libraryRoot = Path.Combine(root, "library");
        var service = new LocalStackLibraryService(libraryRoot);

        var imported = await service.ImportAsync(source);
        var stacks = await service.ListAsync();

        Assert.Equal("sunder.stack.agent.fullstack-dev", imported.StackId);
        Assert.Single(stacks);
        Assert.Equal(imported.StackId, stacks[0].StackId);
        Assert.True(File.Exists(stacks[0].LocalPath));
        Assert.StartsWith(libraryRoot, stacks[0].LocalPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportAsync_WhenStackExists_WritesDestinationFile()
    {
        var root = CreateTempDirectory();
        var source = await CreateStackArchiveAsync(root);
        var service = new LocalStackLibraryService(Path.Combine(root, "library"));
        var imported = await service.ImportAsync(source);
        var destination = Path.Combine(root, "exported.sunderstack");

        await service.ExportAsync(imported, destination);

        Assert.True(File.Exists(destination));
        var manifest = await service.ReadManifestAsync(destination);
        Assert.Equal(imported.StackId, manifest.StackId);
    }

    private static async Task<string> CreateStackArchiveAsync(string root)
    {
        var payloadPath = Path.Combine(root, "payload.json");
        await File.WriteAllTextAsync(payloadPath, "{\"profileId\":\"fullstack\"}");

        var archivePath = Path.Combine(root, "agent-fullstack.sunderstack");
        await SunderStackArchiveWriter.WriteAsync(
            CreateManifest(),
            archivePath,
            new Dictionary<string, string>
            {
                ["payload/fragments/agent-profile.fullstack.json"] = payloadPath,
            });
        return archivePath;
    }

    private static SunderStackManifest CreateManifest()
        => new()
        {
            SchemaVersion = 1,
            StackId = "sunder.stack.agent.fullstack-dev",
            Name = "Fullstack Agent Dev",
            Summary = "Agent setup for coding.",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Packages =
            [
                new SunderStackPackageRequirement
                {
                    PackageId = "sunder.package.agent",
                    InstallTag = "latest",
                    CreatedWithVersion = "1.2.3",
                    MinimumVersion = "1.0.0",
                    Required = true,
                },
            ],
            Fragments =
            [
                new SunderStackFragmentManifest
                {
                    FragmentId = "agent-profile.fullstack",
                    OwnerPackageId = "sunder.package.agent",
                    ContributorId = "sunder.package.agent.profiles",
                    SchemaId = "sunder.package.agent/profile",
                    SchemaVersion = 1,
                    DisplayName = "Fullstack Agent Profile",
                    Description = "A coding profile.",
                    DefaultSelected = true,
                    PayloadPath = "payload/fragments/agent-profile.fullstack.json",
                    RequiresPackages = ["sunder.package.agent"],
                    Safety = new SunderStackSafetyManifest
                    {
                        ContainsSecrets = false,
                        ContainsSecretReferences = false,
                        ContainsLocalPaths = false,
                        ContainsPrivateText = true,
                        ContainsExecutableCommands = false,
                        ContainsNetworkEndpoints = false,
                        ContainsMachineSpecificValues = false,
                    },
                },
            ],
        };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-app-stack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
