using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderStackArchiveInspectorTests
{
    private const string RawApiKey = "sk-proj-abcdefghijklmnopqrstuvwxyz0123456789ABCDE";

    [Fact]
    public async Task ExtractAndValidateAsync_WhenArchiveIsValid_ReturnsManifest()
    {
        var root = CreateTempDirectory();
        var payloadPath = Path.Combine(root, "fragment.json");
        await File.WriteAllTextAsync(payloadPath, "{\"profileId\":\"fullstack\"}");

        var archivePath = Path.Combine(root, "valid.sunderstack");
        await SunderStackArchiveWriter.WriteAsync(
            CreateManifest(),
            archivePath,
            new Dictionary<string, string>
            {
                ["payload/fragments/agent-profile.fullstack.json"] = payloadPath,
            });

        var stagingPath = Path.Combine(root, "staging");
        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, stagingPath);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("sunder.stack.agent.fullstack-dev", result.Manifest?.StackId);
        Assert.True(File.Exists(Path.Combine(stagingPath, "payload", "fragments", "agent-profile.fullstack.json")));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenArchiveIncludesValidMedia_ReturnsManifestMedia()
    {
        var root = CreateTempDirectory();
        var payloadPath = Path.Combine(root, "fragment.json");
        var mediaPath = Path.Combine(root, "hero.png");
        var imageBytes = TinyPng();
        await File.WriteAllTextAsync(payloadPath, "{\"profileId\":\"fullstack\"}");
        await File.WriteAllBytesAsync(mediaPath, imageBytes);

        var archivePath = Path.Combine(root, "valid-media.sunderstack");
        await SunderStackArchiveWriter.WriteAsync(
            CreateManifest(
                readmeMarkdown: "# Fullstack Agent Dev\n\n![Hero](payload/media/hero.png)",
                media:
                [
                    new SunderStackMediaManifest
                    {
                        Path = "payload/media/hero.png",
                        FileName = "hero.png",
                        ContentType = "image/png",
                        Size = imageBytes.Length,
                        AltText = "Hero image",
                        SortOrder = 0,
                    },
                ]),
            archivePath,
            new Dictionary<string, string>
            {
                ["payload/fragments/agent-profile.fullstack.json"] = payloadPath,
                ["payload/media/hero.png"] = mediaPath,
            });

        var stagingPath = Path.Combine(root, "staging");
        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, stagingPath);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("# Fullstack Agent Dev\n\n![Hero](payload/media/hero.png)", result.Manifest?.ReadmeMarkdown);
        var media = Assert.Single(result.Manifest?.Media ?? []);
        Assert.Equal("hero.png", media.FileName);
        Assert.Equal("Hero image", media.AltText);
        Assert.True(File.Exists(Path.Combine(stagingPath, "payload", "media", "hero.png")));
    }

    [Fact]
    public async Task WriteAsync_WithIdenticalInputs_ProducesDeterministicArchive()
    {
        var root = CreateTempDirectory();
        var payloadPath = Path.Combine(root, "fragment.json");
        await File.WriteAllTextAsync(payloadPath, "{\"profileId\":\"fullstack\"}");
        var manifest = CreateManifest();
        var payloadFiles = new Dictionary<string, string>
        {
            ["payload/fragments/agent-profile.fullstack.json"] = payloadPath,
        };
        var first = Path.Combine(root, "first.sunderstack");
        var second = Path.Combine(root, "second.sunderstack");

        await SunderStackArchiveWriter.WriteAsync(manifest, first, payloadFiles);
        await SunderStackArchiveWriter.WriteAsync(manifest, second, payloadFiles);

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenArchiveContainsUnsafePath_Throws()
    {
        var root = CreateTempDirectory();
        var archivePath = Path.Combine(root, "unsafe.sunderstack");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../outside.txt");
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream);
            await writer.WriteAsync("unsafe");
        }

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging")));
        Assert.Contains("unsafe", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenContentHashDoesNotMatch_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(root, corruptHash: true);

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("SHA-256 mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenPayloadContainsPrivateKey_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(
            root,
            payloadContent: "{\"privateKey\":\"-----BEGIN PRIVATE KEY-----\\nabc123\\n-----END PRIVATE KEY-----\"}");

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("private key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenPayloadContainsRawApiKey_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(
            root,
            payloadContent: $"{{\"apiKey\":\"{RawApiKey}\"}}");

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("api key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenManifestContainsRawApiKey_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(root, CreateManifest(summary: RawApiKey));

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("manifest/sunder-stack.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("api key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenContentIndexContainsRawApiKey_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(root, extraIndexedPath: $"payload/files/{RawApiKey}/note.txt");

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("manifest/content-index.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("api key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_IndexesNestedContentIndexFileByExactCanonicalPath()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(root, extraIndexedPath: "payload/files/content-index.json");

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenFragmentPayloadIsNotStrictJson_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(root, payloadContent: "{\"profileId\":\"fullstack\",}");

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("not strict JSON", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenRequiredFeatureIsUnknown_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(root, CreateManifest(requiredFeatures: ["future-reader.v1"]));

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("unsupported feature 'future-reader.v1'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenOptionalFeatureIsUnknown_ReturnsWarning()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(root, CreateManifest(features: ["future-display.v1"]));

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("unknown optional feature", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenMediaContentTypeDoesNotMatchSignature_ReturnsError()
    {
        var root = CreateTempDirectory();
        var payloadPath = Path.Combine(root, "fragment.json");
        var mediaPath = Path.Combine(root, "hero.jpg");
        var imageBytes = TinyPng();
        await File.WriteAllTextAsync(payloadPath, "{}");
        await File.WriteAllBytesAsync(mediaPath, imageBytes);
        var archivePath = Path.Combine(root, "invalid-media.sunderstack");
        await SunderStackArchiveWriter.WriteAsync(
            CreateManifest(media:
            [
                new SunderStackMediaManifest
                {
                    Path = "payload/media/hero.jpg",
                    FileName = "hero.jpg",
                    ContentType = "image/jpeg",
                    Size = imageBytes.Length,
                },
            ]),
            archivePath,
            new Dictionary<string, string>
            {
                ["payload/fragments/agent-profile.fullstack.json"] = payloadPath,
                ["payload/media/hero.jpg"] = mediaPath,
            });

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("contains 'image/png'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenMediaExtensionDoesNotMatchSignature_ReturnsError()
    {
        var root = CreateTempDirectory();
        var payloadPath = Path.Combine(root, "fragment.json");
        var mediaPath = Path.Combine(root, "hero.jpg");
        var imageBytes = TinyPng();
        await File.WriteAllTextAsync(payloadPath, "{}");
        await File.WriteAllBytesAsync(mediaPath, imageBytes);
        var archivePath = Path.Combine(root, "invalid-media-extension.sunderstack");
        await SunderStackArchiveWriter.WriteAsync(
            CreateManifest(media:
            [
                new SunderStackMediaManifest
                {
                    Path = "payload/media/hero.jpg",
                    FileName = "hero.jpg",
                    ContentType = "image/png",
                    Size = imageBytes.Length,
                },
            ]),
            archivePath,
            new Dictionary<string, string>
            {
                ["payload/fragments/agent-profile.fullstack.json"] = payloadPath,
                ["payload/media/hero.jpg"] = mediaPath,
            });

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("extension", StringComparison.OrdinalIgnoreCase)
                                                && error.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateExtractedStackAsync_WhenCollectionsContainNullElements_ReturnsErrors()
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(root);
        var staging = Path.Combine(root, "staging");
        SunderStackArchiveInspector.ExtractArchive(archivePath, staging);
        var manifestPath = Path.Combine(staging, "manifest", "sunder-stack.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["features"] = new JsonArray((JsonNode?)null);
        manifest["packages"]!.AsArray().Insert(0, null);
        manifest["media"] = new JsonArray((JsonNode?)null);
        var fragment = manifest["fragments"]![0]!.AsObject();
        fragment["requiredInputs"] = new JsonArray((JsonNode?)null);
        fragment["preview"] = new JsonObject
        {
            ["displayDetails"] = new JsonArray((JsonNode?)null),
        };
        await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        var indexPath = Path.Combine(staging, "manifest", "content-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        index["files"]!.AsArray().Insert(0, null);
        await File.WriteAllTextAsync(indexPath, index.ToJsonString());

        var result = await SunderStackArchiveInspector.ValidateExtractedStackAsync(staging);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("feature entry is null", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("package requirement is null", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("required input entry is null", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("display detail is null", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("media entry is null", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("null file entry", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidateExtractedStackAsync_WhenContentIndexPathIsMissingOrNull_ReturnsRequiredError(
        bool useNull)
    {
        var root = CreateTempDirectory();
        var staging = Path.Combine(root, "staging");
        SunderStackArchiveInspector.ExtractArchive(CreateStackArchive(root), staging);
        var indexPath = Path.Combine(staging, "manifest", "content-index.json");
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath))!.AsObject();
        var entry = index["files"]![0]!.AsObject();
        if (useNull)
        {
            entry["path"] = null;
        }
        else
        {
            entry.Remove("path");
        }
        await File.WriteAllTextAsync(indexPath, index.ToJsonString());

        var result = await SunderStackArchiveInspector.ValidateExtractedStackAsync(staging);

        Assert.False(result.Success);
        Assert.Contains("Stack content-index path is required.", result.Errors);
    }

    [Theory]
    [InlineData("unknownMember")]
    [InlineData("SchemaVersion")]
    public async Task ValidateExtractedStackAsync_WhenSchemaIsOpenOrWrongCase_ReturnsParseError(string propertyName)
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(root);
        var staging = Path.Combine(root, "staging");
        SunderStackArchiveInspector.ExtractArchive(archivePath, staging);
        await File.WriteAllTextAsync(
            Path.Combine(staging, "manifest", "sunder-stack.json"),
            $$"""{"{{propertyName}}":1}""");

        var result = await SunderStackArchiveInspector.ValidateExtractedStackAsync(staging);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Failed to parse Stack metadata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_GeneratedMalformedIndexPathsReturnErrors()
    {
        var malformedPaths = new[]
        {
            "../payload.json",
            "/payload.json",
            "C:/payload.json",
            "payload\\fragment.json",
            "payload//fragment.json",
            "payload/./fragment.json",
            "payload/CON.json",
            "payload/naïve.json",
        };

        for (var index = 0; index < malformedPaths.Length; index++)
        {
            var root = CreateTempDirectory();
            var archivePath = CreateStackArchive(
                root,
                indexTransform: contentIndex => contentIndex with
                {
                    Files = contentIndex.Files!
                        .Select((entry, entryIndex) => entryIndex == 0 ? entry with { Path = malformedPaths[index] } : entry)
                        .ToArray(),
                });

            var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

            Assert.False(result.Success);
            Assert.Contains(result.Errors, error => error.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData(false, "duplicate")]
    [InlineData(true, "case-colliding")]
    public async Task ExtractAndValidateAsync_WhenIndexContainsPortableDuplicate_ReturnsError(bool changeCase, string expectedError)
    {
        var root = CreateTempDirectory();
        var archivePath = CreateStackArchive(
            root,
            indexTransform: contentIndex =>
            {
                var entries = contentIndex.Files!.ToList();
                var duplicate = entries[0] with
                {
                    Path = changeCase ? entries[0].Path!.ToUpperInvariant() : entries[0].Path,
                };
                entries.Add(duplicate);
                return contentIndex with { Files = entries };
            });

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_ReturnsDeepReadOnlyManifestProjection()
    {
        var root = CreateTempDirectory();
        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(
            CreateStackArchive(root),
            Path.Combine(root, "staging"));

        Assert.True(result.Success);
        Assert.True(Assert.IsAssignableFrom<ICollection<SunderStackPackageRequirement>>(result.Manifest!.Packages!).IsReadOnly);
        Assert.True(Assert.IsAssignableFrom<ICollection<SunderStackFragmentManifest>>(result.Manifest.Fragments!).IsReadOnly);
        Assert.True(Assert.IsAssignableFrom<ICollection<string>>(result.Errors).IsReadOnly);
    }

    private static SunderStackManifest CreateManifest(
        string summary = "Agent setup for coding.",
        string? readmeMarkdown = null,
        IReadOnlyList<SunderStackMediaManifest>? media = null,
        IReadOnlyList<string>? features = null,
        IReadOnlyList<string>? requiredFeatures = null)
        => new()
        {
            SchemaVersion = SunderStackFormat.CurrentSchemaVersion,
            MinReaderVersion = SunderStackFormat.CurrentReaderVersion,
            Features = features,
            RequiredFeatures = requiredFeatures,
            StackId = "sunder.stack.agent.fullstack-dev",
            Name = "Fullstack Agent Dev",
            Summary = summary,
            ReadmeMarkdown = readmeMarkdown,
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
            Media = media,
        };

    private static string CreateStackArchive(
        string root,
        SunderStackManifest? manifest = null,
        bool corruptHash = false,
        string payloadContent = "{\"profileId\":\"fullstack\"}",
        string? extraIndexedPath = null,
        Func<SunderStackContentIndex, SunderStackContentIndex>? indexTransform = null)
    {
        var sourceRoot = Path.Combine(root, "stack-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "manifest"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "payload", "fragments"));

        File.WriteAllText(
            Path.Combine(sourceRoot, "manifest", "sunder-stack.json"),
            JsonSerializer.Serialize(manifest ?? CreateManifest()));

        var payloadPath = Path.Combine(sourceRoot, "payload", "fragments", "agent-profile.fullstack.json");
        File.WriteAllText(payloadPath, payloadContent);

        if (!string.IsNullOrWhiteSpace(extraIndexedPath))
        {
            var extraPath = Path.Combine(sourceRoot, extraIndexedPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(extraPath)!);
            File.WriteAllText(extraPath, "harmless indexed content");
        }

        var contentIndex = new SunderStackContentIndex(
            1,
            Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(path => !SunderStackFormat.IsContentIndexPath(Path.GetRelativePath(sourceRoot, path).Replace('\\', '/')))
                .Select(path => CreateIndexEntry(sourceRoot, path, corruptHash && path == payloadPath))
                .ToArray());
        File.WriteAllText(
            Path.Combine(sourceRoot, "manifest", "content-index.json"),
            JsonSerializer.Serialize(indexTransform?.Invoke(contentIndex) ?? contentIndex));

        var archivePath = Path.Combine(root, $"stack.{Guid.NewGuid():N}.sunderstack");
        ZipFile.CreateFromDirectory(sourceRoot, archivePath);
        return archivePath;
    }

    private static SunderStackContentIndexEntry CreateIndexEntry(string sourceRoot, string path, bool corruptHash)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (corruptHash)
        {
            hash = new string('0', hash.Length);
        }

        return new SunderStackContentIndexEntry(
            Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'),
            hash,
            new FileInfo(path).Length);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-stack-management-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] TinyPng()
        => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
