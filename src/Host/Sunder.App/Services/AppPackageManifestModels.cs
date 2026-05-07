using System.Text.Json;
using Sunder.Protocol;

namespace Sunder.App.Services;

internal sealed record AppLoadedPackageInfo(ActivePackageDescriptor Package, string Folder, AppPackageManifest? Manifest)
{
    public string LibraryFolder => Path.Combine(Folder, "lib");

    public string EntryAssemblyPath => Path.Combine(Folder, "lib", Manifest!.EntryAssembly!);
}

internal sealed record AppPreparedPackageSource(string PackageId, string Folder);

internal sealed record AppLoadedPackageHandle(
    ActivePackageDescriptor Package,
    PackageSourceDescriptor Source,
    string Folder,
    IServiceProvider ServiceProvider,
    AppPackageLoadContext LoadContext)
{
    public string PackageId => Package.PackageId;
}

internal sealed class AppPackageManifest
{
    public string? Id { get; init; }

    public string? EntryAssembly { get; init; }

    public static AppPackageManifest? Load(string manifestPath)
    {
        return JsonSerializer.Deserialize<AppPackageManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
    }
}
