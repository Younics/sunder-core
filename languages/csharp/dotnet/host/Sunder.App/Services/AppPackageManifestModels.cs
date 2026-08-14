using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.App.Services;

internal sealed record AppLoadedPackageInfo(
    ActivePackageDescriptor Package,
    string Folder,
    SunderPackageManifest Manifest,
    PackageUiSnapshotDescriptor Source)
{
    public string LibraryFolder => Path.Combine(Folder, "lib");

    public string EntryAssemblyPath => ArchiveRelativePath.Parse(
            Source.Target.EntryPoint,
            SunderPackageFormat.MaxLogicalPathLength,
            SunderPackageFormat.MaxLogicalPathDepth)
        .ToPlatformPath(Folder);
}

internal sealed record AppPreparedPackageSource(
    string PackageId,
    string Folder,
    SunderPackageManifest Manifest,
    SunderPackageContentIndex ContentIndex);

internal sealed record AppPackageFileMetadata(string Path, long Length, string Sha256);

internal sealed record AppPreparedPackageActivation(
    ActivePackageDescriptor Package,
    PackageUiSnapshotDescriptor Source,
    AppPreparedPackageSource PreparedSource)
{
    public string LibraryFolder => Path.Combine(PreparedSource.Folder, "lib");
}

internal sealed record AppLoadedPackageHandle(
    ActivePackageDescriptor Package,
    PackageUiSnapshotDescriptor Source,
    string Folder,
    IServiceProvider? ServiceProvider,
    AppPackageLoadContext? LoadContext,
    IAsyncDisposable? TargetLifetime = null)
{
    public string PackageId => Package.PackageId;
}

internal static class AppPackageManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static SunderPackageManifest Read(string contentRoot)
    {
        var path = Path.Combine(contentRoot, "manifest", "sunder-package.json");
        try
        {
            return JsonSerializer.Deserialize<SunderPackageManifest>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("App package projection manifest is empty.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("App package projection manifest is invalid.", exception);
        }
    }
}

internal static class AppPackageContentIndexer
{
    public static async Task<IReadOnlyList<AppPackageFileMetadata>> IndexAsync(
        string contentRoot,
        CancellationToken cancellationToken)
    {
        var directories = Directory.EnumerateDirectories(contentRoot, "*", SearchOption.AllDirectories).ToArray();
        if (directories.Any(static path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException("Package content contains a linked directory.");
        }

        var paths = Directory.EnumerateFiles(contentRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(contentRoot, path), StringComparer.Ordinal)
            .ToArray();
        if (paths.Length == 0 || paths.Length > AppPackageSourcePreparer.MaxSnapshotFiles)
        {
            throw new InvalidDataException("Package content contains an invalid number of files.");
        }

        var files = new List<AppPackageFileMetadata>(paths.Length);
        long totalLength = 0;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Package content contains a linked file.");
            }
            var length = new FileInfo(path).Length;
            totalLength = checked(totalLength + length);
            if (totalLength > AppPackageSourcePreparer.MaxSnapshotBytes)
            {
                throw new InvalidDataException("Package content exceeds the snapshot byte limit.");
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            files.Add(new AppPackageFileMetadata(
                Path.GetRelativePath(contentRoot, path).Replace('\\', '/'),
                length,
                hash));
        }
        return files;
    }

    public static SunderPackageContentIndex ToContentIndex(IEnumerable<AppPackageFileMetadata> files)
        => new(
            SunderPackageFormat.CurrentContentIndexVersion,
            files.Select(static file => new SunderPackageContentIndexEntry(
                    file.Path,
                    file.Sha256,
                    file.Length))
                .ToArray());
}
