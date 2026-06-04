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
        Assert.Equal("# Fullstack Agent Dev\n\n![Hero](payload/media/hero.png)", imported.ReadmeMarkdown);
        var media = Assert.Single(imported.Media ?? []);
        Assert.Equal("payload/media/hero.png", media.ArchivePath);
        Assert.Equal("Hero image", media.AltText);
        Assert.StartsWith(libraryRoot, media.LocalPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(media.LocalPath));
        var listedMedia = Assert.Single(stacks[0].Media ?? []);
        Assert.True(File.Exists(listedMedia.LocalPath));
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
        Assert.Equal(imported.ReadmeMarkdown, manifest.ReadmeMarkdown);
        Assert.Single(manifest.Media ?? []);
    }

    private static async Task<string> CreateStackArchiveAsync(string root)
    {
        var payloadPath = Path.Combine(root, "payload.json");
        var mediaPath = Path.Combine(root, "hero.png");
        await File.WriteAllTextAsync(payloadPath, "{\"profileId\":\"fullstack\"}");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3, 4]);

        var archivePath = Path.Combine(root, "agent-fullstack.sunderstack");
        await SunderStackArchiveWriter.WriteAsync(
            CreateManifest(),
            archivePath,
            new Dictionary<string, string>
            {
                ["payload/fragments/agent-profile.fullstack.json"] = payloadPath,
                ["payload/media/hero.png"] = mediaPath,
            });
        return archivePath;
    }

    private static SunderStackManifest CreateManifest()
        => new()
        {
            SchemaVersion = SunderStackFormat.CurrentSchemaVersion,
            MinReaderVersion = SunderStackFormat.CurrentReaderVersion,
            StackId = "sunder.stack.agent.fullstack-dev",
            Name = "Fullstack Agent Dev",
            Summary = "Agent setup for coding.",
            ReadmeMarkdown = "# Fullstack Agent Dev\n\n![Hero](payload/media/hero.png)",
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
                },
            ],
            Media =
            [
                new SunderStackMediaManifest
                {
                    Path = "payload/media/hero.png",
                    FileName = "hero.png",
                    ContentType = "image/png",
                    Size = 4,
                    AltText = "Hero image",
                    SortOrder = 0,
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
