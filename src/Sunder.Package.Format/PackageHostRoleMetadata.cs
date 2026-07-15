using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Sunder.Package.Format;

[Flags]
internal enum PackageHostRoleMetadataValue
{
    ContractOnly = 0,
    App = 1,
    Runtime = 2,
}

internal static class PackageHostRoleMetadata
{
    private const string ModuleNamespace = "Sunder.Sdk.Abstractions";
    private const string AppModuleName = "ISunderAppPackageModule";
    private const string RuntimeModuleName = "ISunderRuntimePackageModule";

    public static IReadOnlyList<string> ReadManifestRoles(string assemblyPath)
    {
        var roles = ReadAssemblyRoles(assemblyPath);
        if (roles == PackageHostRoleMetadataValue.ContractOnly)
        {
            return [SunderPackageFormat.ContractOnlyHostRole];
        }

        var values = new List<string>(2);
        if ((roles & PackageHostRoleMetadataValue.App) != 0) values.Add(SunderPackageFormat.AppHostRole);
        if ((roles & PackageHostRoleMetadataValue.Runtime) != 0) values.Add(SunderPackageFormat.RuntimeHostRole);
        return values;
    }

    public static PackageHostRoleMetadataValue ReadAssemblyRoles(string assemblyPath)
    {
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            throw new BadImageFormatException($"Assembly '{assemblyPath}' does not contain managed metadata.");
        }

        var metadata = peReader.GetMetadataReader();
        var roles = PackageHostRoleMetadataValue.ContractOnly;
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            var visibility = type.Attributes & TypeAttributes.VisibilityMask;
            if (visibility is not TypeAttributes.Public and not TypeAttributes.NestedPublic
                || (type.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) != 0)
            {
                continue;
            }

            roles |= ReadTypeRoles(metadata, handle, new HashSet<TypeDefinitionHandle>());
        }
        return roles;
    }

    public static bool TryParseManifestRoles(
        IReadOnlyList<string>? values,
        out PackageHostRoleMetadataValue roles,
        out string? error)
    {
        roles = PackageHostRoleMetadataValue.ContractOnly;
        error = null;
        if (values is null || values.Count == 0)
        {
            error = "must declare hostRoles";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value is null || !seen.Add(value))
            {
                error = "must declare distinct hostRoles";
                return false;
            }
            roles |= value switch
            {
                SunderPackageFormat.AppHostRole => PackageHostRoleMetadataValue.App,
                SunderPackageFormat.RuntimeHostRole => PackageHostRoleMetadataValue.Runtime,
                SunderPackageFormat.ContractOnlyHostRole => PackageHostRoleMetadataValue.ContractOnly,
                _ => (PackageHostRoleMetadataValue)(-1),
            };
            if (roles == (PackageHostRoleMetadataValue)(-1))
            {
                error = $"declares unknown host role '{value}'";
                return false;
            }
        }

        var contractOnly = seen.Contains(SunderPackageFormat.ContractOnlyHostRole);
        if (contractOnly && seen.Count != 1)
        {
            error = "cannot combine contract-only with app or runtime host roles";
            return false;
        }
        if (seen.Count == 2
            && (values[0] != SunderPackageFormat.AppHostRole || values[1] != SunderPackageFormat.RuntimeHostRole))
        {
            error = "must declare app before runtime in hostRoles";
            return false;
        }
        return true;
    }

    private static PackageHostRoleMetadataValue ReadTypeRoles(
        MetadataReader metadata,
        TypeDefinitionHandle handle,
        HashSet<TypeDefinitionHandle> visited)
    {
        if (!visited.Add(handle)) return PackageHostRoleMetadataValue.ContractOnly;
        var type = metadata.GetTypeDefinition(handle);
        var roles = PackageHostRoleMetadataValue.ContractOnly;
        foreach (var interfaceHandle in type.GetInterfaceImplementations())
        {
            var implementation = metadata.GetInterfaceImplementation(interfaceHandle);
            roles |= ReadInterfaceRole(metadata, implementation.Interface);
        }
        if (type.BaseType.Kind == HandleKind.TypeDefinition)
        {
            roles |= ReadTypeRoles(metadata, (TypeDefinitionHandle)type.BaseType, visited);
        }
        return roles;
    }

    private static PackageHostRoleMetadataValue ReadInterfaceRole(MetadataReader metadata, EntityHandle handle)
    {
        string typeNamespace;
        string typeName;
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
                typeNamespace = metadata.GetString(reference.Namespace);
                typeName = metadata.GetString(reference.Name);
                break;
            case HandleKind.TypeDefinition:
                var definition = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                typeNamespace = metadata.GetString(definition.Namespace);
                typeName = metadata.GetString(definition.Name);
                break;
            default:
                return PackageHostRoleMetadataValue.ContractOnly;
        }

        if (!string.Equals(typeNamespace, ModuleNamespace, StringComparison.Ordinal))
        {
            return PackageHostRoleMetadataValue.ContractOnly;
        }
        return typeName switch
        {
            AppModuleName => PackageHostRoleMetadataValue.App,
            RuntimeModuleName => PackageHostRoleMetadataValue.Runtime,
            _ => PackageHostRoleMetadataValue.ContractOnly,
        };
    }
}
