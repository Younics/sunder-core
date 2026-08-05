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
    SunderPackageManifest Manifest);

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
