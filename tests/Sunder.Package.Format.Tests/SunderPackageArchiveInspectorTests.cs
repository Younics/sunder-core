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
    public async Task ValidateExtractedPackageAsync_WhenHostRolesDisagreeWithEntryMetadata_ReturnsErrorWithoutLoadingAssembly()
    {
        var root = CreateTempDirectory();
        var staging = Path.Combine(root, "staging");
        SunderPackageArchiveInspector.ExtractArchive(CreatePackageArchive(root, "test.package", "1.0.0"), staging);
        var manifestPath = Path.Combine(staging, "manifest", "sunder-package.json");
        var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(await File.ReadAllTextAsync(manifestPath))!;
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new SunderPackageManifest
        {
            ManifestVersion = manifest.ManifestVersion,
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            EntryAssembly = manifest.EntryAssembly,
            HostRoles = [SunderPackageFormat.AppHostRole],
            SdkApiVersion = manifest.SdkApiVersion,
            SdkPackageVersion = manifest.SdkPackageVersion,
            RequiredSdkCapabilities = manifest.RequiredSdkCapabilities,
        }));
        var loadedAssemblyCount = AppDomain.CurrentDomain.GetAssemblies().Length;

        var result = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(staging);

        Assert.Contains(result.Errors, error => error.Contains("entry assembly metadata requires [contract-only]", StringComparison.Ordinal));
        Assert.Equal(loadedAssemblyCount, AppDomain.CurrentDomain.GetAssemblies().Length);
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

    [Fact]
    public async Task ExtractAndValidateAsync_IndexesNestedContentIndexFileByExactCanonicalPath()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0", includeNestedContentIndex: true);

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenIndexRoleDoesNotMatchCanonicalPath_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            "test.package",
            "1.0.0",
            indexTransform: index => index with
            {
                Files = index.Files!.Select(entry => entry.Path == "payload/lib/Test.Package.dll"
                    ? entry with { Role = "asset" }
                    : entry).ToArray(),
            });

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("must declare role 'assembly'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenArchiveUsesUnknownRoot_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0", extraPath: "unexpected/file.txt");

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("outside canonical", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenIconBytesAreNotAnImage_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0", iconBytes: "not an image"u8.ToArray());

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("file signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenIconExtensionDoesNotMatchSignature_ReturnsError()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            "test.package",
            "1.0.0",
            iconBytes: TinyPng(),
            iconFileName: "icon.jpg");

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("extension", StringComparison.OrdinalIgnoreCase)
                                                && error.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenIconExceedsLimit_StopsBeforeSignatureInspection()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            "test.package",
            "1.0.0",
            iconBytes: RandomNumberGenerator.GetBytes(checked((int)SunderPackageFormat.MaxIconBytes + 1)));

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("1 MiB", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Contains("signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractAndValidateAsync_WhenBmpHeightOverflows_ReturnsErrorInsteadOfThrowing()
    {
        var bytes = new byte[26];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BitConverter.GetBytes(1).CopyTo(bytes, 18);
        BitConverter.GetBytes(int.MinValue).CopyTo(bytes, 22);
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0", iconBytes: bytes, iconFileName: "icon.bmp");

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, Path.Combine(root, "staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("invalid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateExtractedPackageAsync_WhenCollectionsContainNullElements_ReturnsErrors()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(
            root,
            "test.package",
            "1.0.0",
            indexTransform: index => index with { Files = [null!, .. index.Files!] });
        var staging = Path.Combine(root, "staging");
        SunderPackageArchiveInspector.ExtractArchive(archivePath, staging);
        var manifestPath = Path.Combine(staging, "manifest", "sunder-package.json");
        var manifest = JsonSerializer.Deserialize<SunderPackageManifest>(await File.ReadAllTextAsync(manifestPath))!;
        var malformed = new SunderPackageManifest
        {
            ManifestVersion = manifest.ManifestVersion,
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            EntryAssembly = manifest.EntryAssembly,
            HostRoles = manifest.HostRoles,
            SdkApiVersion = manifest.SdkApiVersion,
            SdkPackageVersion = manifest.SdkPackageVersion,
            RequiredSdkCapabilities = manifest.RequiredSdkCapabilities,
            DependsOn = [null!],
        };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(malformed));

        var result = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(staging);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("dependency entry is null", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("null file entry", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("unknownMember")]
    [InlineData("ManifestVersion")]
    public async Task ValidateExtractedPackageAsync_WhenSchemaIsOpenOrWrongCase_ReturnsParseError(string propertyName)
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0");
        var staging = Path.Combine(root, "staging");
        SunderPackageArchiveInspector.ExtractArchive(archivePath, staging);
        var manifestPath = Path.Combine(staging, "manifest", "sunder-package.json");
        await File.WriteAllTextAsync(manifestPath, $$"""{"{{propertyName}}":1}""");

        var result = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(staging);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Failed to parse package metadata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateExtractedPackageAsync_PreservesCrossValidatorErrorOrder()
    {
        var root = CreateTempDirectory();
        var archivePath = CreatePackageArchive(root, "test.package", "1.0.0");
        var stagingPath = Path.Combine(root, "staging");
        SunderPackageArchiveInspector.ExtractArchive(archivePath, stagingPath);
        File.Delete(Path.Combine(stagingPath, "payload", "lib", "Test.Package.dll"));

        var result = await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(stagingPath);

        Assert.Equal(
            [
                "Package 'test.package' is missing entry assembly 'Test.Package.dll' under payload/lib/.",
                "Package content index references missing file 'payload/lib/Test.Package.dll'.",
            ],
            result.Errors);
    }

    [Fact]
    public void PackageArchiveValidation_UsesFocusedCollaboratorsAndSizeRatchets()
    {
        var sourceDirectory = FindFormatDirectory();
        var expectedFiles = new[]
        {
            "PackageArchiveManifestValidator.cs",
            "PackageDependencyValidator.cs",
            "PackageAssetValidator.cs",
            "PackageContentIndexValidator.cs",
            "PackageContentSignatureValidator.cs",
            "PackageArchivePathValidator.cs",
        };

        Assert.True(File.ReadLines(Path.Combine(sourceDirectory, "SunderPackageArchiveInspector.cs")).Count() < 120);
        foreach (var file in expectedFiles)
        {
            Assert.True(File.Exists(Path.Combine(sourceDirectory, file)), $"Missing package archive validator {file}.");
            Assert.True(File.ReadLines(Path.Combine(sourceDirectory, file)).Count() < 150, $"{file} exceeded its size ratchet.");
        }
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
        string? dependencyRange = null,
        bool includeNestedContentIndex = false,
        string? extraPath = null,
        byte[]? iconBytes = null,
        string iconFileName = "icon.png",
        Func<SunderPackageContentIndex, SunderPackageContentIndex>? indexTransform = null)
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
            HostRoles = [SunderPackageFormat.ContractOnlyHostRole],
            Icon = iconBytes is null ? null : "assets/" + iconFileName,
            SdkApiVersion = sdkApiVersion,
            SdkPackageVersion = sdkPackageVersion,
            RequiredSdkCapabilities = [capability],
            DependsOn = dependencyRange is null
                ? null
                : [new SunderPackageDependencyManifest { PackageId = "other.package", VersionRange = dependencyRange }],
        }));

        var entryAssemblyPath = Path.Combine(sourceRoot, "payload", "lib", "Test.Package.dll");
        File.Copy(typeof(SunderPackageArchiveInspectorTests).Assembly.Location, entryAssemblyPath);
        if (includeNestedContentIndex)
        {
            Directory.CreateDirectory(Path.Combine(sourceRoot, "payload", "assets"));
            File.WriteAllText(Path.Combine(sourceRoot, "payload", "assets", "content-index.json"), "asset content");
        }
        if (iconBytes is not null)
        {
            Directory.CreateDirectory(Path.Combine(sourceRoot, "payload", "assets"));
            File.WriteAllBytes(Path.Combine(sourceRoot, "payload", "assets", iconFileName), iconBytes);
        }
        if (!string.IsNullOrWhiteSpace(extraPath))
        {
            var fullExtraPath = Path.Combine(sourceRoot, extraPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullExtraPath)!);
            File.WriteAllText(fullExtraPath, "unexpected content");
        }

        var contentIndex = new SunderPackageContentIndex(
            1,
            Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(path => !SunderPackageFormat.IsContentIndexPath(Path.GetRelativePath(sourceRoot, path).Replace('\\', '/')))
                .Select(path => CreateIndexEntry(sourceRoot, path, corruptHash && path == entryAssemblyPath, uppercaseHash))
                .ToArray());
        File.WriteAllText(Path.Combine(sourceRoot, "manifest", "content-index.json"), JsonSerializer.Serialize(indexTransform?.Invoke(contentIndex) ?? contentIndex));

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

        var relativePath = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/');
        return new SunderPackageContentIndexEntry(
            relativePath,
            hash,
            new FileInfo(path).Length,
            SunderPackageFormat.GetContentRole(relativePath) ?? "file");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-package-management-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] TinyPng()
        => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    private static string FindFormatDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Sunder.Package.Format");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Sunder.Package.Format.");
    }
}
