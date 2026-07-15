using Sunder.App.Services;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.App.Tests;

public sealed class LocalStackLibraryServiceTests
{
    private static readonly byte[] MinimalPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

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

    [Fact]
    public async Task ReplaceAsync_WhenStackWasPublished_PreservesPublishMetadata()
    {
        var root = CreateTempDirectory();
        var source = await CreateStackArchiveAsync(root);
        var service = new LocalStackLibraryService(Path.Combine(root, "library"));
        var imported = await service.ImportAsync(source);
        var publishedAt = DateTimeOffset.UtcNow.AddDays(-2);
        var publishedUpdatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        await service.UpdatePublishStateAsync(
            imported.StackId,
            "https://registry.example/",
            "published-stack",
            publishedAt,
            publishedUpdatedAt);
        var replacement = await CreateStackArchiveAsync(root, name: "Updated Fullstack Agent Dev");

        var replaced = await service.ReplaceAsync(replacement, imported.StackId);
        var listed = Assert.Single(await service.ListAsync());

        Assert.Equal("Updated Fullstack Agent Dev", replaced.Name);
        Assert.Equal("https://registry.example/", replaced.RegistryUrl);
        Assert.Equal("published-stack", replaced.PublishedStackId);
        Assert.Equal(publishedAt, replaced.PublishedAtUtc);
        Assert.Equal(publishedUpdatedAt, replaced.PublishedUpdatedAtUtc);
        Assert.Equal(replaced.RegistryUrl, listed.RegistryUrl);
        Assert.Equal(replaced.PublishedStackId, listed.PublishedStackId);
    }

    [Fact]
    public async Task ListAndDelete_WhenIndexPathEscapesRoot_DoNotAccessOutsideFile()
    {
        var root = CreateTempDirectory();
        var libraryRoot = Path.Combine(root, "library");
        Directory.CreateDirectory(libraryRoot);
        var outside = Path.Combine(root, "outside.sunderstack");
        await File.WriteAllTextAsync(outside, "outside");
        var malicious = new LocalStackLibraryItem(
            "test.stack",
            "Test",
            null,
            outside,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0,
            0,
            null,
            null,
            null,
            null);
        await File.WriteAllTextAsync(
            Path.Combine(libraryRoot, "local-stacks.json"),
            System.Text.Json.JsonSerializer.Serialize(new LocalStackLibraryIndex([malicious])));
        var service = new LocalStackLibraryService(libraryRoot);

        Assert.Empty(await service.ListAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DeleteAsync(malicious));
        Assert.True(File.Exists(outside));
    }

    private static async Task<string> CreateStackArchiveAsync(string root, string name = "Fullstack Agent Dev")
    {
        var payloadPath = Path.Combine(root, "payload.json");
        var mediaPath = Path.Combine(root, "hero.png");
        await File.WriteAllTextAsync(payloadPath, "{\"profileId\":\"fullstack\"}");
        await File.WriteAllBytesAsync(mediaPath, MinimalPng);

        var archivePath = Path.Combine(root, "agent-fullstack.sunderstack");
        await SunderStackArchiveWriter.WriteAsync(
            CreateManifest(name),
            archivePath,
            new Dictionary<string, string>
            {
                ["payload/fragments/agent-profile.fullstack.json"] = payloadPath,
                ["payload/media/hero.png"] = mediaPath,
            });
        return archivePath;
    }

    private static SunderStackManifest CreateManifest(string name)
        => new()
        {
            SchemaVersion = SunderStackFormat.CurrentSchemaVersion,
            MinReaderVersion = SunderStackFormat.CurrentReaderVersion,
            StackId = "sunder.stack.agent.fullstack-dev",
            Name = name,
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
                    Size = MinimalPng.LongLength,
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
