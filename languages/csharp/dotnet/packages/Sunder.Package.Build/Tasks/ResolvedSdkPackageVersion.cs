using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Tasks;

internal static class ResolvedSdkPackageVersion
{
    private const string InformationalVersionAttributeName = "System.Reflection.AssemblyInformationalVersionAttribute";

    public static string? Resolve(
        IReadOnlyList<ITaskItem> referencePaths,
        string? configuredVersion,
        TaskLoggingHelper log)
    {
        var references = referencePaths
            .Where(static item => ManagedAssemblyPath.IsCandidate(item.ItemSpec))
            .Where(static item => string.Equals(
                Path.GetFileNameWithoutExtension(item.ItemSpec),
                "Sunder.Sdk",
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(static item => Path.GetFullPath(item.ItemSpec), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        if (references.Length != 1)
        {
            log.LogError(references.Length == 0
                ? "The resolved Sunder.Sdk reference was not supplied to package manifest generation."
                : "Package manifest generation resolved multiple Sunder.Sdk reference paths: "
                  + string.Join(", ", references.Select(static item => item.ItemSpec)) + ".");
            return null;
        }

        var reference = references[0];
        var resolvedVersion = reference.GetMetadata("NuGetPackageVersion");
        if (string.IsNullOrWhiteSpace(resolvedVersion))
        {
            resolvedVersion = ReadInformationalVersion(reference.ItemSpec);
        }
        if (!SemanticVersion.TryParse(resolvedVersion, out var resolvedSemanticVersion))
        {
            log.LogError(
                $"Resolved Sunder.Sdk reference '{reference.ItemSpec}' declares informational/package version "
                + $"'{resolvedVersion}', which is not strict SemVer 2.0.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(configuredVersion))
        {
            return resolvedVersion;
        }

        var normalizedConfiguredVersion = configuredVersion.Trim();
        if (!SemanticVersion.TryParse(normalizedConfiguredVersion, out var configuredSemanticVersion))
        {
            log.LogError($"SunderSdkPackageVersion '{configuredVersion}' is not strict SemVer 2.0.");
            return null;
        }

        if (!configuredSemanticVersion.HasSamePrecedence(resolvedSemanticVersion))
        {
            log.LogError(
                $"SunderSdkPackageVersion '{configuredVersion}' does not match resolved Sunder.Sdk reference "
                + $"version '{resolvedVersion}'. Remove the override or resolve the intended SDK package.");
            return null;
        }

        return normalizedConfiguredVersion;
    }

    private static string? ReadInformationalVersion(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return null;
        }

        var metadata = peReader.GetMetadataReader();
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (!string.Equals(GetAttributeTypeName(metadata, attribute.Constructor), InformationalVersionAttributeName, StringComparison.Ordinal))
            {
                continue;
            }

            var reader = metadata.GetBlobReader(attribute.Value);
            return reader.ReadUInt16() == 1 ? reader.ReadSerializedString() : null;
        }
        return null;
    }

    private static string? GetAttributeTypeName(MetadataReader metadata, EntityHandle constructor)
    {
        var typeHandle = constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default,
        };
        if (typeHandle.Kind == HandleKind.TypeReference)
        {
            var type = metadata.GetTypeReference((TypeReferenceHandle)typeHandle);
            var @namespace = metadata.GetString(type.Namespace);
            return string.IsNullOrEmpty(@namespace)
                ? metadata.GetString(type.Name)
                : @namespace + "." + metadata.GetString(type.Name);
        }
        if (typeHandle.Kind == HandleKind.TypeDefinition)
        {
            var type = metadata.GetTypeDefinition((TypeDefinitionHandle)typeHandle);
            var @namespace = metadata.GetString(type.Namespace);
            return string.IsNullOrEmpty(@namespace)
                ? metadata.GetString(type.Name)
                : @namespace + "." + metadata.GetString(type.Name);
        }
        return null;
    }
}
