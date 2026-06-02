using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.PackageManagement;
using Xunit;

namespace Sunder.PackageManagement.Tests;

public sealed class SunderStackArchiveInspectorTests
{
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging")));
        Assert.Contains("unsafe path", ex.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task ExtractAndValidateAsync_WhenFragmentOmitsSafety_ReturnsError()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(omitFragmentSafety: true);
        var archivePath = CreateStackArchive(root, manifest: manifest);

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("missing safety metadata", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenFragmentDeclaresSecrets_ReturnsWarning()
    {
        var root = CreateTempDirectory();
        var manifest = CreateManifest(new SunderStackSafetyManifest { ContainsSecrets = true });
        var archivePath = CreateStackArchive(root, manifest: manifest);

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("raw secrets", StringComparison.OrdinalIgnoreCase));
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
            payloadContent: "{\"apiKey\":\"sk-proj-abcdefghijklmnopqrstuvwxyz0123456789ABCDE\"}");

        var result = await SunderStackArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("api key", StringComparison.OrdinalIgnoreCase));
    }

    private static SunderStackManifest CreateManifest(
        SunderStackSafetyManifest? fragmentSafety = null,
        bool omitFragmentSafety = false)
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
                    Safety = omitFragmentSafety
                        ? null
                        : fragmentSafety ?? new SunderStackSafetyManifest
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

    private static string CreateStackArchive(
        string root,
        SunderStackManifest? manifest = null,
        bool corruptHash = false,
        string payloadContent = "{\"profileId\":\"fullstack\"}")
    {
        var sourceRoot = Path.Combine(root, "stack-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "manifest"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "payload", "fragments"));

        File.WriteAllText(
            Path.Combine(sourceRoot, "manifest", "sunder-stack.json"),
            JsonSerializer.Serialize(manifest ?? CreateManifest()));

        var payloadPath = Path.Combine(sourceRoot, "payload", "fragments", "agent-profile.fullstack.json");
        File.WriteAllText(payloadPath, payloadContent);

        var contentIndex = new SunderStackContentIndex(
            1,
            Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith("content-index.json", StringComparison.OrdinalIgnoreCase))
                .Select(path => CreateIndexEntry(sourceRoot, path, corruptHash && path == payloadPath))
                .ToArray());
        File.WriteAllText(Path.Combine(sourceRoot, "manifest", "content-index.json"), JsonSerializer.Serialize(contentIndex));

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
            new FileInfo(path).Length,
            Role: "stack");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-stack-management-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
