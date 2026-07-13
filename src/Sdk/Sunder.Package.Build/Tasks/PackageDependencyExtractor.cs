using System.Reflection;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Tasks;

internal sealed class PackageDependencyExtractor(string packageVersion)
{
    public IReadOnlyList<SunderPackageDependencyManifest> Extract(IEnumerable<CustomAttributeData> attributes)
        => attributes
            .Where(attribute => attribute.AttributeType.FullName == typeof(SunderPackageDependencyAttribute).FullName)
            .Select(attribute => new SunderPackageDependencyManifest
            {
                PackageId = PackageMetadataDecoder.GetNamedString(attribute, nameof(SunderPackageDependencyAttribute.PackageId)) ?? string.Empty,
                VersionRange = ExpandVersionProperties(
                    PackageMetadataDecoder.GetNamedString(attribute, nameof(SunderPackageDependencyAttribute.VersionRange)) ?? string.Empty),
            })
            .ToArray();

    private string ExpandVersionProperties(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var version = packageVersion.Trim();
        return value
            .Replace("$(PackageVersion)", version, StringComparison.OrdinalIgnoreCase)
            .Replace("$(Version)", version, StringComparison.OrdinalIgnoreCase)
            .Replace("$(NextPackageMajorVersion)", ResolveNextMajorVersion(), StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveNextMajorVersion()
        => SemanticVersion.TryParse(packageVersion, out var version) ? version.NextMajor().Major : string.Empty;
}
