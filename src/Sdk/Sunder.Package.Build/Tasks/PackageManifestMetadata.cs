using Sunder.Package.Format;

namespace Sunder.Package.Build.Tasks;

internal sealed record PackageManifestMetadata(
    string Id,
    string Name,
    string? Summary,
    string? Icon,
    IReadOnlyList<SunderPackageDependencyManifest> Dependencies,
    IReadOnlyList<string> RequiredSdkCapabilities);
