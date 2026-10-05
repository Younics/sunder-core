using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed partial class SunderPackageProjectionArchiveTests
{
    [Fact]
    public async Task InstalledProjectionSetRetainsSourceMetadataAndDoesNotRelaxFullArchiveValidation()
    {
        var fixture = await CreateDefaultPackageAsync();
        try
        {
            var manifestBytes = await File.ReadAllBytesAsync(ToFilePath(fixture.ExtractedPath, SunderPackageFormat.ManifestPath));
            KeepOnlyWindowsAppPayload(fixture.ExtractedPath);
            var projection = await SunderPackageArchiveInspector.ValidateExtractedProjectionSetAsync(fixture.ExtractedPath, [new("app", "win-x64")]);
            Assert.True(projection.Success, string.Join(" | ", projection.Errors));
            Assert.Equal(4, projection.Manifest!.Targets!.Count);
            Assert.Equal(manifestBytes, await File.ReadAllBytesAsync(ToFilePath(fixture.ExtractedPath, SunderPackageFormat.ManifestPath)));
            Assert.False((await SunderPackageArchiveInspector.ValidateExtractedPackageAsync(fixture.ExtractedPath)).Success);
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("missing")]
    [InlineData("unacquired-rid")]
    [InlineData("unacquired-role")]
    [InlineData("malformed-unacquired-target")]
    [InlineData("orphan-import")]
    public async Task InstalledProjectionSetStillValidatesPayloadAndTheWholeSourceGraph(string mutation)
    {
        var fixture = await CreateDefaultPackageAsync();
        try
        {
            var root = fixture.ExtractedPath;
            KeepOnlyWindowsAppPayload(root);
            switch (mutation)
            {
                case "hash": WriteFile(root, "payload/app/win-x64/bin/app.dll", [9, 9, 9]); break;
                case "missing": File.Delete(ToFilePath(root, "payload/app/win-x64/bin/app.dll")); break;
                case "unacquired-rid": WriteFile(root, "payload/app/linux-x64/bin/extra.dll", [1]); RewriteInstalledIndex(root); break;
                case "unacquired-role": WriteFile(root, "payload/runtime/shared/extra.txt", [1]); RewriteInstalledIndex(root); break;
                case "malformed-unacquired-target":
                    MutateJson(root, SunderPackageFormat.ManifestPath, manifest => manifest["targets"]![2]!["kind"] = "invalid");
                    RewriteInstalledIndex(root);
                    break;
                default:
                    MutateJson(root, SunderPackageFormat.ManifestPath, manifest => manifest["packageApiImports"] = new JsonArray(new JsonObject()));
                    RewriteInstalledIndex(root);
                    break;
            }
            Assert.False((await SunderPackageArchiveInspector.ValidateExtractedProjectionSetAsync(root, [new("app", "win-x64")])).Success);
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("invalid")]
    [InlineData("overflow")]
    public async Task InstalledProjectionSetRequiresExplicitBoundedDeclaredAcquisition(string mismatch)
    {
        var fixture = await CreateDefaultPackageAsync();
        try
        {
            KeepOnlyWindowsAppPayload(fixture.ExtractedPath);
            SunderPackageTargetKey[] scope = mismatch switch
            {
                "empty" => [],
                "unknown" => [new("app", "osx-arm64")],
                "duplicate" => [new("app", "win-x64"), new("app", "win-x64")],
                "invalid" => [default],
                _ => Enumerable.Repeat(new SunderPackageTargetKey("app", "win-x64"), SunderPackageFormat.MaxTargets + 1).ToArray(),
            };
            Assert.False((await SunderPackageArchiveInspector.ValidateExtractedProjectionSetAsync(fixture.ExtractedPath, scope)).Success);
        }
        finally { Directory.Delete(fixture.Root, recursive: true); }
    }

    private static void KeepOnlyWindowsAppPayload(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (path.StartsWith("payload/", StringComparison.Ordinal)
                && !path.StartsWith("payload/shared/", StringComparison.Ordinal)
                && !path.StartsWith("payload/app/shared/", StringComparison.Ordinal)
                && !path.StartsWith("payload/app/win-x64/", StringComparison.Ordinal)) File.Delete(file);
        }
        RewriteInstalledIndex(root);
    }

    private static void RewriteInstalledIndex(string root)
    {
        var entries = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => path != ToFilePath(root, SunderPackageFormat.ContentIndexPath))
            .Select(path => CreateIndexEntry(root, path)).OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();
        WriteFile(root, SunderPackageFormat.ContentIndexPath, JsonSerializer.SerializeToUtf8Bytes(new SunderPackageContentIndex(1, entries)));
    }
}
