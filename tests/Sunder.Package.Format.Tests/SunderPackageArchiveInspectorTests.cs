using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderPackageArchiveInspectorTests
{
    [Fact]
    public async Task ExtractAndValidateAsync_WhenArchiveIsValid_ReturnsManifest()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0");
        var stagingPath = Path.Combine(root, "staging");

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, stagingPath);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("test.package", result.Manifest?.Id);
        Assert.True(File.Exists(Path.Combine(stagingPath, "payload", "lib", "Test.Package.dll")));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenArchiveContainsUnsafePath_Throws()
    {
        var root = CreateTempDirectory();
        var archivePath = Path.Combine(root, "unsafe.sunderpkg");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../outside.txt");
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream);
            await writer.WriteAsync("unsafe");
        }

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging")));
        Assert.Contains("unsafe", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenContentHashDoesNotMatch_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0", corruptHash: true);

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("SHA-256 mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(2, "1.0.0", "core.v1", null)]
    [InlineData(1, "1.0", "core.v1", null)]
    [InlineData(1, "1.0.0", "", null)]
    [InlineData(1, "1.0.0", "core.v1", "^1.0.0")]
    public async Task ExtractAndValidateAsync_WhenV1ManifestMetadataIsInvalid_ReturnsError(
        int sdkApiVersion,
        string sdkPackageVersion,
        string capability,
        string? dependencyRange)
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            "test.package",
            "1.0.0",
            sdkApiVersion: sdkApiVersion,
            sdkPackageVersion: sdkPackageVersion,
            capability: capability,
            dependencyRange: dependencyRange);

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenContentIndexHashIsNotCanonical_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0", uppercaseHash: true);

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("lowercase SHA-256", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreatePackageArchive(
        string root,
        string packageId,
        string version,
        bool corruptHash = false,
        bool uppercaseHash = false,
        int sdkApiVersion = 1,
        string sdkPackageVersion = "1.0.0",
        string capability = "core.v1",
        string? dependencyRange = null)
    {
        var sourceRoot = Path.Combine(root, "package-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "manifest"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "payload", "lib"));

        var manifestPath = Path.Combine(sourceRoot, "manifest", "sunder-package.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(new SunderPackageManifest
        {
            ManifestVersion = 1,
            Id = packageId,
            Name = "Test Package",
            Version = version,
            EntryAssembly = "Test.Package.dll",
            SdkApiVersion = sdkApiVersion,
            SdkPackageVersion = sdkPackageVersion,
            RequiredSdkCapabilities = [capability],
            DependsOn = dependencyRange is null
                ? null
                : [new SunderPackageDependencyManifest { PackageId = "other.package", VersionRange = dependencyRange }],
        }));

        var entryAssemblyPath = Path.Combine(sourceRoot, "payload", "lib", "Test.Package.dll");
        File.WriteAllText(entryAssemblyPath, "not a real assembly");

        var contentIndex = new SunderPackageContentIndex(
            1,
            Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith("content-index.json", StringComparison.OrdinalIgnoreCase))
                .Select(path => CreateIndexEntry(sourceRoot, path, corruptHash && path == entryAssemblyPath, uppercaseHash))
                .ToArray());
        File.WriteAllText(Path.Combine(sourceRoot, "manifest", "content-index.json"), JsonSerializer.Serialize(contentIndex));

        var archivePath = Path.Combine(root, $"{packageId}.{version}.{Guid.NewGuid():N}.sunderpkg");
        ZipFile.CreateFromDirectory(sourceRoot, archivePath);
        return archivePath;
    }

    private static SunderPackageContentIndexEntry CreateIndexEntry(string sourceRoot, string path, bool corruptHash, bool uppercaseHash)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (corruptHash)
        {
            hash = new string('0', hash.Length);
        }
        else if (uppercaseHash)
        {
            hash = hash.ToUpperInvariant();
        }

        return new SunderPackageContentIndexEntry(
            Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'),
            hash,
            new FileInfo(path).Length,
            Role: "runtime");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-package-management-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
