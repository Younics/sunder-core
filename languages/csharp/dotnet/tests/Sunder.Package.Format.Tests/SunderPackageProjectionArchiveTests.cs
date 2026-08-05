using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class SunderPackageProjectionArchiveTests
{
    [Fact]
    public async Task Writer_ProducesByteIdenticalArchivesAndPreservesManifestBytes()
    {
        var fixture = await CreateDefaultPackageAsync();
        var first = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.App("win-x64"), "first");
        File.SetLastWriteTimeUtc(
            Path.Combine(fixture.ExtractedPath, "payload", "app", "shared", "common.txt"),
            DateTime.UtcNow.AddDays(1));
        var second = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.App("win-x64"), "second");

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        using var archive = ZipFile.OpenRead(first);
        Assert.Equal(
            archive.Entries.Select(static entry => entry.FullName).Order(StringComparer.Ordinal),
            archive.Entries.Select(static entry => entry.FullName));

        var staging = Path.Combine(fixture.Root, "projection-deterministic");
        var result = await SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(first, staging);
        AssertSuccess(result);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(fixture.ExtractedPath, "manifest", "sunder-package.json")),
            await File.ReadAllBytesAsync(Path.Combine(staging, "manifest", "sunder-package.json")));
        Assert.Equal(fixture.SourceArchiveSha256, result.Descriptor!.SourceArchiveSha256);
        Assert.Equal(Hash(await File.ReadAllBytesAsync(Path.Combine(staging, "manifest", "sunder-package.json"))), result.Descriptor.ManifestSha256);
    }

    [Theory]
    [InlineData("shared", null)]
    [InlineData("app", "win-x64")]
    [InlineData("runtime", "linux-x64")]
    public async Task WriterAndInspector_SelectExactlyDeclaredProjectionLayers(string kind, string? rid)
    {
        var fixture = await CreateDefaultPackageAsync();
        var key = new SunderPackageProjectionKey(kind, rid);
        var archivePath = await WriteProjectionAsync(fixture, key, kind);
        var staging = Path.Combine(fixture.Root, "inspect-" + kind);

        var result = await SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(archivePath, staging);

        AssertSuccess(result);
        Assert.Equal(key, result.ExtractedProjection!.Key);
        Assert.Equal(kind, result.Descriptor!.Kind);
        Assert.Equal(rid, result.Descriptor.Rid);
        var expected = kind switch
        {
            "shared" => new[] { "payload/shared/global.txt" },
            "app" => ["payload/app/shared/common.txt", "payload/app/win-x64/bin/app.dll"],
            _ => ["payload/runtime/linux-x64/bin/runtime.dll", "payload/runtime/shared/common.txt"],
        };
        Assert.Equal(expected, result.ContentIndex!.Files!.Select(static entry => entry.Path));
        Assert.Equal(expected, result.ExtractedProjection.PayloadPaths.Select(static path => path.ToString()));
        Assert.DoesNotContain(result.ContentIndex.Files!, static entry => entry.Path == SunderPackageProjectionFormat.ManifestPath);
        Assert.All(result.ContentIndex.Files!, entry => Assert.True(File.Exists(ToFilePath(staging, entry.Path!))));
        if (kind != "shared")
        {
            Assert.DoesNotContain(result.ContentIndex.Files!, static entry => entry.Path!.StartsWith("payload/shared/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Writer_AllowsEmptyDeclaredRoleProjection()
    {
        var manifest = CreateManifest(
            [CreateTarget("app", "win-x64", "avalonia", "app.dll")]);
        var fixture = await CreatePackageAsync(
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/app.dll"] = [1, 2, 3] });
        var archivePath = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.App("win-x64"), "empty-app");

        var result = await SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(fixture.Root, "empty-app-staging"));

        AssertSuccess(result);
        Assert.Empty(result.ContentIndex!.Files!);
        Assert.Empty(result.ExtractedProjection!.PayloadPaths);
    }

    [Fact]
    public async Task Inspector_AllowsWebTargetReferencesSatisfiedBySeparateSharedProjection()
    {
        var manifest = CreateManifest(
        [
            new SunderPackageTargetManifest
            {
                Role = SunderPackageFormat.AppHostRole,
                Rid = "win-x64",
                Kind = SunderPackageFormat.WebTargetKind,
                EntryPoint = "web/index.html",
                SdkVersion = "1.0.0",
                RequiredHostCapabilities = ["core.v1"],
                Views =
                [
                    new SunderPackageWebViewManifest
                    {
                        ViewId = "test.projection.workspace",
                        DisplayName = "Workspace",
                        Route = "/workspace",
                        Icon = "assets/view.png",
                        DefaultPlacement = "rightTop",
                        ShowInHotbar = true,
                    },
                ],
            },
        ]);
        var fixture = await CreatePackageAsync(
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/web/index.html"] = "<!doctype html>"u8.ToArray(),
                ["payload/shared/assets/view.png"] = [1, 2, 3],
            });
        var appArchive = await WriteProjectionAsync(
            fixture,
            SunderPackageProjectionKey.App("win-x64"),
            "web-app");

        var result = await SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(
            appArchive,
            Path.Combine(fixture.Root, "web-app-staging"));

        AssertSuccess(result);
        Assert.Empty(result.ContentIndex!.Files!);
    }

    [Fact]
    public async Task WriterAndInspector_AcceptSharedOnlyContractPackage()
    {
        var contract = CreateContractDescriptor("example.contract", "1.0.0");
        var manifest = CreateManifest(
            [],
            [
                new SunderPackageContractBundleManifest
                {
                    ContractId = "example.contract",
                    Version = "1.0.0",
                    DescriptorPath = "contracts/example.json",
                    Sha256 = SunderRpcContractDescriptor.Parse(contract).Sha256,
                },
            ]);
        var fixture = await CreatePackageAsync(
            manifest,
            new Dictionary<string, byte[]> { ["payload/shared/contracts/example.json"] = contract });
        var archivePath = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.Shared, "contract");

        var result = await SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(fixture.Root, "contract-projection"));

        AssertSuccess(result);
        Assert.Empty(result.Manifest!.Targets!);
        Assert.Equal("payload/shared/contracts/example.json", Assert.Single(result.ContentIndex!.Files!).Path);
    }

    [Fact]
    public async Task Writer_RequiresDeclaredExactRoleTarget()
    {
        var fixture = await CreateDefaultPackageAsync();

        var error = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            SunderPackageProjectionArchiveWriter.WriteAsync(
                fixture.ExtractedPath,
                fixture.Validation.Manifest!,
                fixture.Validation.ContentIndex!,
                fixture.SourceArchiveSha256,
                SunderPackageProjectionKey.App("osx-arm64"),
                Path.Combine(fixture.Root, "undeclared.projection")));

        Assert.Contains("app/osx-arm64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inspector_RejectsSourceArchiveHashTampering()
    {
        var fixture = await CreateDefaultPackageAsync();
        var original = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.App("win-x64"), "source-hash");
        var tampered = await MutateProjectionAsync(fixture.Root, original, "source-hash-tampered", staging =>
        {
            MutateJson(staging, SunderPackageProjectionFormat.DescriptorPath, json =>
            {
                var current = json["sourceArchiveSha256"]!.GetValue<string>();
                json["sourceArchiveSha256"] = (current[0] == '0' ? "1" : "0") + current[1..];
            });
        });

        var result = await InspectAsync(fixture.Root, tampered, "source-hash-result");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("projectionSha256", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspector_RejectsManifestHashAndIdentityTampering()
    {
        var fixture = await CreateDefaultPackageAsync();
        var original = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.App("win-x64"), "manifest-hash");
        var tampered = await MutateProjectionAsync(fixture.Root, original, "manifest-hash-tampered", staging =>
        {
            MutateJson(staging, SunderPackageProjectionFormat.ManifestPath, json => json["name"] = "Tampered Package");
        });

        var result = await InspectAsync(fixture.Root, tampered, "manifest-hash-result");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("manifest SHA-256", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("runtime", "win-x64")]
    [InlineData("app", "linux-x64")]
    [InlineData("shared", "win-x64")]
    public async Task Inspector_RejectsWrongKindOrRid(string kind, string? rid)
    {
        var fixture = await CreateDefaultPackageAsync();
        var original = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.App("win-x64"), "wrong-key");
        var tampered = await MutateProjectionAsync(fixture.Root, original, "wrong-key-" + kind + "-" + rid, staging =>
        {
            MutateJson(staging, SunderPackageProjectionFormat.DescriptorPath, json =>
            {
                json["kind"] = kind;
                json["rid"] = rid;
            });
        });

        var result = await InspectAsync(fixture.Root, tampered, "wrong-key-result-" + kind + "-" + rid);

        Assert.False(result.Success);
        Assert.Contains(
            result.Errors,
            error => error.Contains("kind/RID", StringComparison.Ordinal)
                     || error.Contains("declared payload layers", StringComparison.Ordinal)
                     || error.Contains("projectionSha256", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("payload/shared/injected.txt")]
    [InlineData("payload/runtime/shared/injected.txt")]
    [InlineData("payload/app/linux-x64/injected.txt")]
    [InlineData("payload/lib/legacy.dll")]
    public async Task Inspector_RejectsGlobalSharedForeignRidRoleAndLegacyInjection(string injectedPath)
    {
        var fixture = await CreateDefaultPackageAsync();
        var original = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.App("win-x64"), "foreign");
        var tampered = await MutateProjectionAsync(fixture.Root, original, "foreign-" + Guid.NewGuid().ToString("N"), staging =>
        {
            WriteFile(staging, injectedPath, "injected"u8.ToArray());
        });

        var result = await InspectAsync(fixture.Root, tampered, "foreign-result-" + Guid.NewGuid().ToString("N"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains(injectedPath, StringComparison.Ordinal)
                                                && error.Contains("declared payload layers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspector_RejectsExtraMissingAndMismatchedPayloadCoverage()
    {
        var fixture = await CreateDefaultPackageAsync();
        var original = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.App("win-x64"), "coverage");

        var extra = await MutateProjectionAsync(fixture.Root, original, "coverage-extra", staging =>
            WriteFile(staging, "payload/app/win-x64/extra.txt", [9]));
        var extraResult = await InspectAsync(fixture.Root, extra, "coverage-extra-result");
        Assert.Contains(extraResult.Errors, error => error.Contains("unindexed payload file 'payload/app/win-x64/extra.txt'", StringComparison.Ordinal));

        var missing = await MutateProjectionAsync(fixture.Root, original, "coverage-missing", staging =>
            File.Delete(ToFilePath(staging, "payload/app/win-x64/bin/app.dll")));
        var missingResult = await InspectAsync(fixture.Root, missing, "coverage-missing-result");
        Assert.Contains(missingResult.Errors, error => error.Contains("missing payload file 'payload/app/win-x64/bin/app.dll'", StringComparison.Ordinal));

        var mismatched = await MutateProjectionAsync(fixture.Root, original, "coverage-mismatch", staging =>
        {
            MutateJson(staging, SunderPackageProjectionFormat.ContentIndexPath, json =>
            {
                var entry = json["files"]!.AsArray()
                    .Single(node => node!["path"]!.GetValue<string>() == "payload/app/win-x64/bin/app.dll")!;
                entry["sha256"] = new string('0', 64);
                entry["size"] = 999;
            });
        });
        var mismatchResult = await InspectAsync(fixture.Root, mismatched, "coverage-mismatch-result");
        Assert.Contains(mismatchResult.Errors, error => error.Contains("size mismatch", StringComparison.Ordinal));
        Assert.Contains(mismatchResult.Errors, error => error.Contains("SHA-256 mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspector_RejectsMetadataExtrasAndMetadataInContentIndex()
    {
        var fixture = await CreateDefaultPackageAsync();
        var original = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.Shared, "metadata-extra");
        var tampered = await MutateProjectionAsync(fixture.Root, original, "metadata-extra-tampered", staging =>
        {
            WriteFile(staging, "manifest/extra.json", "{}"u8.ToArray());
            MutateJson(staging, SunderPackageProjectionFormat.ContentIndexPath, json =>
            {
                json["files"]!.AsArray().Add(new JsonObject
                {
                    ["path"] = SunderPackageProjectionFormat.ManifestPath,
                    ["sha256"] = new string('0', 64),
                    ["size"] = 0,
                });
            });
        });

        var result = await InspectAsync(fixture.Root, tampered, "metadata-extra-result");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("manifest/extra.json", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains(SunderPackageProjectionFormat.ManifestPath, StringComparison.Ordinal)
                                                && error.Contains("does not belong", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("manifest/sunder-projection.json", "unexpected")]
    [InlineData("manifest/sunder-package.json", "legacyMember")]
    [InlineData("manifest/content-index.json", "role")]
    public async Task Inspector_RejectsUnknownJsonMembers(string metadataPath, string member)
    {
        var fixture = await CreateDefaultPackageAsync();
        var original = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.Shared, "unknown-json");
        var tampered = await MutateProjectionAsync(fixture.Root, original, "unknown-json-" + member, staging =>
            MutateJson(staging, metadataPath, json => json[member] = "unexpected"));

        var result = await InspectAsync(fixture.Root, tampered, "unknown-json-result-" + member);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Failed to parse projection metadata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspector_RejectsDuplicateAndWrongCaseJsonProperties()
    {
        var fixture = await CreateDefaultPackageAsync();
        var original = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.Shared, "strict-json");
        var duplicate = await MutateProjectionAsync(fixture.Root, original, "strict-json-duplicate", staging =>
        {
            var path = ToFilePath(staging, SunderPackageProjectionFormat.DescriptorPath);
            var json = File.ReadAllText(path);
            File.WriteAllText(path, json.Insert(1, "\"kind\":\"shared\","));
        });
        var duplicateResult = await InspectAsync(fixture.Root, duplicate, "strict-json-duplicate-result");
        Assert.Contains(duplicateResult.Errors, error => error.Contains("Duplicate JSON property 'kind'", StringComparison.Ordinal));

        var wrongCase = await MutateProjectionAsync(fixture.Root, original, "strict-json-case", staging =>
        {
            MutateJson(staging, SunderPackageProjectionFormat.DescriptorPath, json =>
            {
                var value = json["kind"]!.DeepClone();
                json.Remove("kind");
                json["Kind"] = value;
            });
        });
        var wrongCaseResult = await InspectAsync(fixture.Root, wrongCase, "strict-json-case-result");
        Assert.Contains(wrongCaseResult.Errors, error => error.Contains("Failed to parse projection metadata", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Inspector_EnforcesArchiveEntryLimitAtomically()
    {
        var fixture = await CreateDefaultPackageAsync();
        var archivePath = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.App("win-x64"), "entry-limit");
        var staging = Path.Combine(fixture.Root, "entry-limit-staging");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(
                archivePath,
                staging,
                SunderArchiveExtractionOptions.Default with { MaxEntries = 2 }));

        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task Inspector_RejectsLinkTraversalAndCompressionBombEntriesAtomically()
    {
        var fixture = await CreateDefaultPackageAsync();
        var original = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.App("win-x64"), "security");

        var linkArchive = Path.Combine(fixture.Root, "security-link.zip");
        RewriteZip(original, linkArchive, (path, entry) =>
        {
            if (path == "payload/app/win-x64/bin/app.dll")
            {
                entry.ExternalAttributes = (0xa000 | 0x1ff) << 16;
            }
        });
        var linkStaging = Path.Combine(fixture.Root, "security-link-staging");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(linkArchive, linkStaging));
        Assert.False(Directory.Exists(linkStaging));

        var traversalArchive = Path.Combine(fixture.Root, "security-traversal.zip");
        RewriteZip(original, traversalArchive, addEntry: ("../outside", [1]));
        var traversalStaging = Path.Combine(fixture.Root, "security-traversal-staging");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(traversalArchive, traversalStaging));
        Assert.False(Directory.Exists(traversalStaging));

        var bombArchive = Path.Combine(fixture.Root, "security-bomb.zip");
        RewriteZip(original, bombArchive, addEntry: ("payload/app/win-x64/bomb.bin", new byte[100_000]));
        var bombStaging = Path.Combine(fixture.Root, "security-bomb-staging");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(
                bombArchive,
                bombStaging,
                SunderArchiveExtractionOptions.Default with { MaxCompressionRatio = 20 }));
        Assert.False(Directory.Exists(bombStaging));
    }

    [Fact]
    public async Task SharedAndExactTargetRoundTrip_ReconstructsLogicalTargetUnion()
    {
        var fixture = await CreateDefaultPackageAsync();
        var sharedArchive = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.Shared, "roundtrip-shared");
        var appArchive = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.App("win-x64"), "roundtrip-app");
        var shared = await SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(
            sharedArchive,
            Path.Combine(fixture.Root, "roundtrip-shared-staging"));
        var app = await SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(
            appArchive,
            Path.Combine(fixture.Root, "roundtrip-app-staging"));
        AssertSuccess(shared);
        AssertSuccess(app);

        var physicalPaths = shared.ExtractedProjection!.PayloadPaths
            .Concat(app.ExtractedProjection!.PayloadPaths)
            .ToArray();
        var target = new SunderPackageTargetKey("app", "win-x64");
        var plan = SunderPackageTargetResolver.CreateProjectionPlan(target, physicalPaths);

        Assert.Equal(
            ["global.txt", "common.txt", "bin/app.dll"],
            plan.Files.Select(static file => file.LogicalPath.ToString()));
        Assert.True(plan.TryResolveLogicalPath(ArchiveRelativePath.Parse("bin/app.dll"), out _));
        Assert.Equal(
            [SunderPackagePayloadLayer.Shared, SunderPackagePayloadLayer.RoleShared, SunderPackagePayloadLayer.Target],
            plan.Files.Select(static file => file.Layer));

        var logicalRoot = Path.Combine(fixture.Root, "roundtrip-logical-union");
        foreach (var file in plan.Files)
        {
            var sourceRoot = file.Layer == SunderPackagePayloadLayer.Shared
                ? shared.ExtractedProjection.RootPath
                : app.ExtractedProjection.RootPath;
            var destination = file.LogicalPath.ToPlatformPath(logicalRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.PhysicalPath.ToPlatformPath(sourceRoot), destination);
        }
        Assert.Equal(
            ["bin/app.dll", "common.txt", "global.txt"],
            Directory.EnumerateFiles(logicalRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(logicalRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(logicalRoot, "bin", "app.dll")));
    }

    [Fact]
    public async Task ProjectionArchive_IsNotAcceptedAsUniversalPackage()
    {
        var fixture = await CreateDefaultPackageAsync();
        var projection = await WriteProjectionAsync(fixture, SunderPackageProjectionKey.Shared, "not-universal");

        var result = await SunderPackageArchiveInspector.ExtractAndValidateAsync(
            projection,
            Path.Combine(fixture.Root, "not-universal-staging"));

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains(SunderPackageProjectionFormat.DescriptorPath, StringComparison.Ordinal));
    }

    private static async Task<PackageFixture> CreateDefaultPackageAsync()
    {
        var manifest = CreateManifest(
            [
                CreateTarget("app", "win-x64", "avalonia", "bin/app.dll"),
                CreateTarget("app", "linux-x64", "avalonia", "bin/app.dll"),
                CreateTarget("runtime", "linux-x64", "dotnet", "bin/runtime.dll"),
                CreateTarget("runtime", "win-x64", "dotnet", "bin/runtime.dll"),
            ]);
        return await CreatePackageAsync(
            manifest,
            new Dictionary<string, byte[]>
            {
                ["payload/shared/global.txt"] = "global"u8.ToArray(),
                ["payload/app/shared/common.txt"] = "app-common"u8.ToArray(),
                ["payload/app/win-x64/bin/app.dll"] = [1, 2, 3],
                ["payload/app/linux-x64/bin/app.dll"] = [4, 5, 6],
                ["payload/runtime/shared/common.txt"] = "runtime-common"u8.ToArray(),
                ["payload/runtime/linux-x64/bin/runtime.dll"] = [7, 8, 9],
                ["payload/runtime/win-x64/bin/runtime.dll"] = [10, 11, 12],
            });
    }

    private static async Task<PackageFixture> CreatePackageAsync(
        SunderPackageManifest manifest,
        IReadOnlyDictionary<string, byte[]> payloadFiles)
    {
        var root = CreateTempDirectory();
        var source = Path.Combine(root, "universal-source");
        Directory.CreateDirectory(source);
        foreach (var file in payloadFiles)
        {
            WriteFile(source, file.Key, file.Value);
        }

        var manifestOptions = new JsonSerializerOptions { WriteIndented = true };
        WriteFile(
            source,
            SunderPackageFormat.ManifestPath,
            JsonSerializer.SerializeToUtf8Bytes(manifest, manifestOptions));
        var entries = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => CreateIndexEntry(source, path))
            .OrderBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        WriteFile(
            source,
            SunderPackageFormat.ContentIndexPath,
            JsonSerializer.SerializeToUtf8Bytes(new SunderPackageContentIndex(1, entries)));

        var archivePath = Path.Combine(root, "universal.sunderpkg");
        await SunderArchive.WriteDeterministicAsync(source, archivePath);
        var extracted = Path.Combine(root, "universal-extracted");
        var validation = await SunderPackageArchiveInspector.ExtractAndValidateAsync(archivePath, extracted);
        Assert.True(validation.Success, string.Join(Environment.NewLine, validation.Errors));
        return new PackageFixture(root, archivePath, extracted, Hash(await File.ReadAllBytesAsync(archivePath)), validation);
    }

    private static async Task<string> WriteProjectionAsync(
        PackageFixture fixture,
        SunderPackageProjectionKey key,
        string name)
    {
        var path = Path.Combine(fixture.Root, name + ".sunderprojection");
        await SunderPackageProjectionArchiveWriter.WriteAsync(
            fixture.ExtractedPath,
            fixture.Validation.Manifest!,
            fixture.Validation.ContentIndex!,
            fixture.SourceArchiveSha256,
            key,
            path);
        return path;
    }

    private static async Task<SunderPackageProjectionArchiveValidationResult> InspectAsync(
        string root,
        string archivePath,
        string name)
        => await SunderPackageProjectionArchiveInspector.ExtractAndValidateAsync(
            archivePath,
            Path.Combine(root, name));

    private static async Task<string> MutateProjectionAsync(
        string root,
        string archivePath,
        string name,
        Action<string> mutation)
    {
        var staging = Path.Combine(root, name + "-source");
        SunderArchive.ExtractAtomic(archivePath, staging);
        mutation(staging);
        var output = Path.Combine(root, name + ".zip");
        await SunderArchive.WriteDeterministicAsync(staging, output);
        return output;
    }

    private static void MutateJson(string stagingPath, string archivePath, Action<JsonObject> mutation)
    {
        var path = ToFilePath(stagingPath, archivePath);
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        mutation(json);
        File.WriteAllText(path, json.ToJsonString());
    }

    private static void RewriteZip(
        string sourcePath,
        string destinationPath,
        Action<string, ZipArchiveEntry>? mutateEntry = null,
        (string Path, byte[] Bytes)? addEntry = null)
    {
        using var source = ZipFile.OpenRead(sourcePath);
        using var destination = ZipFile.Open(destinationPath, ZipArchiveMode.Create);
        foreach (var sourceEntry in source.Entries)
        {
            var destinationEntry = destination.CreateEntry(sourceEntry.FullName, CompressionLevel.Optimal);
            mutateEntry?.Invoke(sourceEntry.FullName, destinationEntry);
            using var input = sourceEntry.Open();
            using var output = destinationEntry.Open();
            input.CopyTo(output);
        }
        if (addEntry is { } item)
        {
            var entry = destination.CreateEntry(item.Path, CompressionLevel.Optimal);
            using var output = entry.Open();
            output.Write(item.Bytes);
        }
    }

    private static SunderPackageManifest CreateManifest(
        IReadOnlyList<SunderPackageTargetManifest?> targets,
        IReadOnlyList<SunderPackageContractBundleManifest?>? contractBundles = null)
        => new()
        {
            ArchiveFormatVersion = 1,
            ManifestVersion = 1,
            Id = "test.projection",
            Name = "Projection Test Package",
            Summary = "Canonical projection archive test package.",
            Version = "1.2.3",
            Targets = targets,
            ContractBundles = contractBundles,
        };

    private static SunderPackageTargetManifest CreateTarget(
        string role,
        string rid,
        string kind,
        string entryPoint)
        => new()
        {
            Role = role,
            Rid = rid,
            Kind = kind,
            EntryPoint = entryPoint,
            TargetFramework = "net10.0",
            SdkVersion = "1.0.0",
            RequiredHostCapabilities = ["core.v1"],
        };

    private static SunderPackageContentIndexEntry CreateIndexEntry(string root, string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new SunderPackageContentIndexEntry(
            Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
            Hash(bytes),
            bytes.LongLength);
    }

    private static void WriteFile(string root, string archivePath, byte[] bytes)
    {
        var path = ToFilePath(root, archivePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static string ToFilePath(string root, string archivePath)
        => Path.Combine([root, .. archivePath.Split('/')]);

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] CreateContractDescriptor(string contractId, string version)
        => System.Text.Encoding.UTF8.GetBytes($$"""
            {
              "descriptorVersion": 1,
              "contractId": "{{contractId}}",
              "version": "{{version}}",
              "services": [{
                "serviceId": "service",
                "methods": [{
                  "methodId": "call",
                  "kind": "unary",
                  "requestSchema": { "$ref": "#/$defs/Request" },
                  "responseSchema": { "$ref": "#/$defs/Response" }
                }]
              }],
              "$defs": {
                "Request": {
                  "type": "object",
                  "properties": {},
                  "required": [],
                  "additionalProperties": false
                },
                "Response": {
                  "type": "object",
                  "properties": {},
                  "required": [],
                  "additionalProperties": false
                }
              }
            }
            """);

    private static void AssertSuccess(SunderPackageProjectionArchiveValidationResult result)
        => Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sunder-projection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record PackageFixture(
        string Root,
        string ArchivePath,
        string ExtractedPath,
        string SourceArchiveSha256,
        SunderPackageArchiveValidationResult Validation);
}
