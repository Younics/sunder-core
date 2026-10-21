using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    private static readonly string[] EditorWorkspaceSdkPackages =
        ["Sunder.Sdk", "Sunder.Sdk.Worker", "Sunder.Sdk.Avalonia", "Sunder.Package.Build"];

    private sealed record EditorWorkspacePackage(string ContentHash, string ArchiveHash, string Feed);

    private static Dictionary<string, EditorWorkspacePackage> PrepareEditorWorkspacePackages(
        string feed, string thirdPartyFeed, string version)
    {
        var prepared = new Dictionary<string, EditorWorkspacePackage>(StringComparer.OrdinalIgnoreCase);
        // Like PackageTemplateTests.PackLocalSunderFeedAsync, reuse only the exact
        // restored third-party closure. Never enumerate/adopt the whole user cache.
        // Package.Build's private tooling dependencies are embedded in its fresh nupkg.
        foreach (var owner in EditorWorkspaceSdkPackages.Where(name => name != "Sunder.Package.Build"))
        {
            var project = Path.Combine(FindRepositoryRoot(), "languages/csharp/dotnet/packages", owner);
            using var assets = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(project, "obj/project.assets.json")));
            using var locked = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(project, "packages.lock.json")));
            var dependencies = locked.RootElement.GetProperty("dependencies").GetProperty("net10.0");
            foreach (var library in assets.RootElement.GetProperty("libraries").EnumerateObject())
            {
                if (library.Value.GetProperty("type").GetString() != "package"
                    || library.Name.StartsWith("Sunder.", StringComparison.OrdinalIgnoreCase)) continue;
                var identity = library.Name.Split('/');
                var pin = Assert.Single(dependencies.EnumerateObject(), entry =>
                    string.Equals(entry.Name, identity[0], StringComparison.OrdinalIgnoreCase)).Value;
                Assert.Equal(identity[1], pin.GetProperty("resolved").GetString());
                var contentHash = pin.GetProperty("contentHash").GetString()!;
                Assert.Equal(contentHash, library.Value.GetProperty("sha512").GetString());
                if (prepared.TryGetValue(library.Name, out var existing))
                {
                    Assert.Equal(existing.ContentHash, contentHash);
                    continue;
                }
                var filename = library.Name.Replace('/', '.').ToLowerInvariant() + ".nupkg";
                var archive = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
                    .Select(folder => Path.Combine(folder.Name, library.Value.GetProperty("path").GetString()!, filename))
                    .FirstOrDefault(File.Exists);
                Assert.True(archive is not null, $"Restore {owner} before the workspace fixture: missing {library.Name} nupkg.");
                using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(archive!)!, ".nupkg.metadata")));
                Assert.Equal(contentHash, metadata.RootElement.GetProperty("contentHash").GetString());
                var archiveHash = EditorWorkspaceArchiveHash(archive!);
                // NuGet contentHash excludes signatures; .nupkg.sha512 covers the
                // entire signed archive. Check both without conflating the two hashes.
                Assert.Equal(File.ReadAllText(archive + ".sha512").Trim(), archiveHash);
                var destination = Path.Combine(thirdPartyFeed, filename);
                File.Copy(archive!, destination);
                Assert.Equal(archiveHash, EditorWorkspaceArchiveHash(destination));
                prepared.Add(library.Name, new(contentHash, archiveHash, thirdPartyFeed));
            }
        }
        Assert.NotEmpty(prepared);
        Assert.Equal(EditorWorkspaceSdkPackages.Select(name => name + "." + version + ".nupkg").Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(feed, "*.nupkg").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var name in EditorWorkspaceSdkPackages)
        {
            // These unsigned GUID-version packages were packed by this invocation,
            // not read from an ambient Sunder cache or a published developer feed.
            var hash = EditorWorkspaceArchiveHash(Path.Combine(feed, name + "." + version + ".nupkg"));
            prepared.Add(name + "/" + version, new(hash, hash, feed));
        }
        return prepared;
    }

    private static void AssertEditorWorkspaceRestore(string project, string packages,
        IReadOnlyDictionary<string, EditorWorkspacePackage> prepared)
    {
        using var assets = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(project)!, "obj/project.assets.json")));
        Assert.Equal(Normalize(packages), Normalize(Assert.Single(assets.RootElement.GetProperty("packageFolders").EnumerateObject()).Name));
        var libraries = assets.RootElement.GetProperty("libraries").EnumerateObject()
            .Where(library => library.Value.GetProperty("type").GetString() == "package").ToArray();
        Assert.Equal(prepared.Keys.Order(StringComparer.OrdinalIgnoreCase), libraries.Select(library => library.Name).Order(StringComparer.OrdinalIgnoreCase));
        foreach (var library in libraries)
        {
            var expected = prepared[library.Name];
            // The fresh restore computes NuGet's signed-package content hash itself;
            // it must still equal the repository lock, not just copied cache metadata.
            Assert.Equal(expected.ContentHash, library.Value.GetProperty("sha512").GetString());
            var directory = Path.Combine(packages, library.Value.GetProperty("path").GetString()!);
            using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, ".nupkg.metadata")));
            Assert.Equal(expected.ContentHash, metadata.RootElement.GetProperty("contentHash").GetString());
            Assert.Equal(Normalize(expected.Feed), Normalize(metadata.RootElement.GetProperty("source").GetString()!));
            Assert.Equal(expected.ArchiveHash,
                EditorWorkspaceArchiveHash(Path.Combine(directory, library.Name.Replace('/', '.').ToLowerInvariant() + ".nupkg")));
        }
        foreach (var framework in assets.RootElement.GetProperty("project").GetProperty("frameworks").EnumerateObject())
            if (framework.Value.TryGetProperty("downloadDependencies", out var downloads))
                Assert.Empty(downloads.EnumerateArray()); // Framework-dependent roles need no runtime/apphost pack prefetch.

        static string Normalize(string path) => ResolvePhysicalTemporaryPath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
    }

    private static string EditorWorkspaceArchiveHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToBase64String(SHA512.HashData(stream));
    }
}
