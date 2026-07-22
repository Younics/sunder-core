using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Format;

internal sealed record PackageModuleResolution(string? TypeName, string? Error);

internal sealed class PackageModuleShape
{
    private readonly IReadOnlyList<PackageModuleTypeShape> _implementations;
    private readonly IReadOnlyList<string> _metadataErrors;

    internal PackageModuleShape(
        IReadOnlyList<PackageModuleTypeShape> implementations,
        IReadOnlyList<string> metadataErrors)
    {
        _implementations = implementations;
        _metadataErrors = metadataErrors;
        Roles = implementations.Aggregate(
            PackageHostRoleMetadataValue.ContractOnly,
            static (roles, implementation) => roles | implementation.Roles);
    }

    public PackageHostRoleMetadataValue Roles { get; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>(_metadataErrors);
        AddRoleErrors(PackageHostRoleMetadataValue.App, errors);
        AddRoleErrors(PackageHostRoleMetadataValue.Runtime, errors);
        return errors.Distinct(StringComparer.Ordinal).ToArray();
    }

    public PackageModuleResolution Resolve(PackageHostRoleMetadataValue role)
    {
        if (role is not PackageHostRoleMetadataValue.App and not PackageHostRoleMetadataValue.Runtime)
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        var errors = new List<string>(_metadataErrors);
        AddRoleErrors(role, errors);
        if (errors.Count > 0)
        {
            return new PackageModuleResolution(null, string.Join(" ", errors.Distinct(StringComparer.Ordinal)));
        }

        var implementation = _implementations.SingleOrDefault(candidate => (candidate.Roles & role) != 0);
        return new PackageModuleResolution(implementation?.TypeName, null);
    }

    private void AddRoleErrors(PackageHostRoleMetadataValue role, ICollection<string> errors)
    {
        var roleName = role == PackageHostRoleMetadataValue.App
            ? nameof(ISunderAppPackageModule)
            : nameof(ISunderRuntimePackageModule);
        var implementations = _implementations
            .Where(candidate => (candidate.Roles & role) != 0)
            .ToArray();
        foreach (var implementation in implementations)
        {
            if (!implementation.IsTopLevelPublicClass)
            {
                errors.Add($"Module '{implementation.TypeName}' implementing {roleName} must be a top-level public class.");
            }
            else if (implementation.IsOpenGeneric)
            {
                errors.Add($"Module '{implementation.TypeName}' implementing {roleName} must be non-generic.");
            }
            else if (!implementation.HasPublicParameterlessConstructor)
            {
                errors.Add($"Module '{implementation.TypeName}' implementing {roleName} must declare a public parameterless constructor.");
            }
        }

        if (implementations.Length > 1)
        {
            errors.Add(
                $"Entry assembly contains multiple {roleName} implementations: "
                + string.Join(", ", implementations.Select(static implementation => implementation.TypeName))
                + ".");
        }
    }
}

internal static class PackageModuleShapeReader
{
    private const string ModuleNamespace = "Sunder.Sdk.Abstractions";
    private const string AppModuleName = nameof(ISunderAppPackageModule);
    private const string RuntimeModuleName = nameof(ISunderRuntimePackageModule);
    private static readonly AssemblyName SdkAssemblyName = typeof(ISunderRuntimePackageModule).Assembly.GetName();

    public static PackageModuleShape Read(string assemblyPath)
    {
        using var stream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            throw new BadImageFormatException($"Assembly '{assemblyPath}' does not contain managed metadata.");
        }

        var metadata = peReader.GetMetadataReader();
        var implementations = new List<PackageModuleTypeShape>();
        var metadataErrors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if ((type.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) != 0)
            {
                continue;
            }

            var roles = ReadTypeRoles(metadata, handle, [], metadataErrors);
            if (roles == PackageHostRoleMetadataValue.ContractOnly)
            {
                continue;
            }

            var visibility = type.Attributes & TypeAttributes.VisibilityMask;
            implementations.Add(new PackageModuleTypeShape(
                GetTypeDisplayName(metadata, handle),
                roles,
                visibility == TypeAttributes.Public && IsClass(metadata, type),
                type.GetGenericParameters().Count > 0,
                HasPublicParameterlessConstructor(metadata, type)));
        }

        return new PackageModuleShape(implementations, metadataErrors.ToArray());
    }

    private static PackageHostRoleMetadataValue ReadTypeRoles(
        MetadataReader metadata,
        TypeDefinitionHandle handle,
        HashSet<TypeDefinitionHandle> visited,
        ISet<string> metadataErrors)
    {
        var rowNumber = MetadataTokens.GetRowNumber(handle);
        if (rowNumber <= 0 || rowNumber > metadata.TypeDefinitions.Count)
        {
            throw new BadImageFormatException(
                $"Module metadata references type-definition row {rowNumber}, but the assembly contains {metadata.TypeDefinitions.Count} type definitions.");
        }
        if (!visited.Add(handle))
        {
            return PackageHostRoleMetadataValue.ContractOnly;
        }

        var type = metadata.GetTypeDefinition(handle);
        var roles = PackageHostRoleMetadataValue.ContractOnly;
        foreach (var interfaceHandle in type.GetInterfaceImplementations())
        {
            var implementation = metadata.GetInterfaceImplementation(interfaceHandle);
            roles |= ReadInterfaceRoles(metadata, implementation.Interface, visited, metadataErrors);
        }
        if (!type.BaseType.IsNil && type.BaseType.Kind == HandleKind.TypeDefinition)
        {
            roles |= ReadTypeRoles(
                metadata,
                AsTypeDefinitionHandle(type.BaseType),
                visited,
                metadataErrors);
        }
        return roles;
    }

    private static PackageHostRoleMetadataValue ReadInterfaceRoles(
        MetadataReader metadata,
        EntityHandle handle,
        HashSet<TypeDefinitionHandle> visited,
        ISet<string> metadataErrors)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                var definitionHandle = AsTypeDefinitionHandle(handle);
                var definition = metadata.GetTypeDefinition(definitionHandle);
                if (TryGetRole(metadata.GetString(definition.Namespace), metadata.GetString(definition.Name), out _))
                {
                    metadataErrors.Add(
                        $"Module contract '{ModuleNamespace}.{metadata.GetString(definition.Name)}' must be referenced from '{SdkAssemblyName.FullName}', not defined by the package entry assembly.");
                    return PackageHostRoleMetadataValue.ContractOnly;
                }
                return ReadTypeRoles(metadata, definitionHandle, visited, metadataErrors);

            case HandleKind.TypeReference:
                var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
                var typeNamespace = metadata.GetString(reference.Namespace);
                var typeName = metadata.GetString(reference.Name);
                if (!TryGetRole(typeNamespace, typeName, out var role))
                {
                    return PackageHostRoleMetadataValue.ContractOnly;
                }
                if (TryReadAssemblyName(metadata, reference.ResolutionScope, out var assemblyName)
                    && AssemblyIdentitiesMatch(SdkAssemblyName, assemblyName!))
                {
                    return role;
                }

                metadataErrors.Add(
                    $"Module contract '{typeNamespace}.{typeName}' must be referenced from '{SdkAssemblyName.FullName}', not '{assemblyName?.FullName ?? "the package entry assembly"}'.");
                return PackageHostRoleMetadataValue.ContractOnly;

            default:
                return PackageHostRoleMetadataValue.ContractOnly;
        }
    }

    private static bool TryGetRole(
        string typeNamespace,
        string typeName,
        out PackageHostRoleMetadataValue role)
    {
        role = PackageHostRoleMetadataValue.ContractOnly;
        if (!string.Equals(typeNamespace, ModuleNamespace, StringComparison.Ordinal))
        {
            return false;
        }

        role = typeName switch
        {
            AppModuleName => PackageHostRoleMetadataValue.App,
            RuntimeModuleName => PackageHostRoleMetadataValue.Runtime,
            _ => PackageHostRoleMetadataValue.ContractOnly,
        };
        return role != PackageHostRoleMetadataValue.ContractOnly;
    }

    private static TypeDefinitionHandle AsTypeDefinitionHandle(EntityHandle handle)
        => MetadataTokens.TypeDefinitionHandle(MetadataTokens.GetToken(handle) & 0x00ff_ffff);

    private static bool TryReadAssemblyName(
        MetadataReader metadata,
        EntityHandle scope,
        out AssemblyName? assemblyName)
    {
        if (scope.Kind == HandleKind.TypeReference)
        {
            return TryReadAssemblyName(
                metadata,
                metadata.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope,
                out assemblyName);
        }
        if (scope.Kind != HandleKind.AssemblyReference)
        {
            assemblyName = null;
            return false;
        }

        var reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)scope);
        assemblyName = new AssemblyName
        {
            Name = metadata.GetString(reference.Name),
            Version = reference.Version,
            CultureName = reference.Culture.IsNil ? null : metadata.GetString(reference.Culture),
        };
        var keyOrToken = reference.PublicKeyOrToken.IsNil
            ? []
            : metadata.GetBlobBytes(reference.PublicKeyOrToken);
        if ((reference.Flags & AssemblyFlags.PublicKey) != 0)
        {
            assemblyName.SetPublicKey(keyOrToken);
        }
        else
        {
            assemblyName.SetPublicKeyToken(keyOrToken);
        }
        return true;
    }

    private static bool AssemblyIdentitiesMatch(AssemblyName expected, AssemblyName candidate)
        => string.Equals(expected.Name, candidate.Name, StringComparison.OrdinalIgnoreCase)
           && NormalizeVersion(expected.Version).Major == NormalizeVersion(candidate.Version).Major
           && NormalizeVersion(expected.Version).Minor == NormalizeVersion(candidate.Version).Minor
           && string.Equals(expected.CultureName ?? string.Empty, candidate.CultureName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
           && (expected.GetPublicKeyToken() ?? []).SequenceEqual(candidate.GetPublicKeyToken() ?? []);

    private static Version NormalizeVersion(Version? version)
        => version is null
            ? new Version(0, 0)
            : new Version(Math.Max(version.Major, 0), Math.Max(version.Minor, 0));

    private static bool IsClass(MetadataReader metadata, TypeDefinition type)
    {
        if ((type.Attributes & TypeAttributes.Interface) != 0)
        {
            return false;
        }

        if (type.BaseType.Kind != HandleKind.TypeReference)
        {
            return true;
        }

        var baseType = metadata.GetTypeReference((TypeReferenceHandle)type.BaseType);
        if (!string.Equals(metadata.GetString(baseType.Namespace), "System", StringComparison.Ordinal))
        {
            return true;
        }

        return metadata.GetString(baseType.Name) is not "ValueType" and not "Enum" and not "Delegate" and not "MulticastDelegate";
    }

    private static bool HasPublicParameterlessConstructor(MetadataReader metadata, TypeDefinition type)
    {
        foreach (var methodHandle in type.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            if (metadata.GetString(method.Name) != ".ctor"
                || (method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public
                || (method.Attributes & MethodAttributes.Static) != 0)
            {
                continue;
            }

            var signature = metadata.GetBlobReader(method.Signature);
            var header = signature.ReadSignatureHeader();
            if (header.IsGeneric)
            {
                signature.ReadCompressedInteger();
            }
            if (signature.ReadCompressedInteger() == 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string GetTypeDisplayName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return GetTypeDisplayName(metadata, declaringType) + "+" + metadata.GetString(type.Name);
        }

        var typeNamespace = metadata.GetString(type.Namespace);
        var typeName = metadata.GetString(type.Name);
        return string.IsNullOrEmpty(typeNamespace) ? typeName : $"{typeNamespace}.{typeName}";
    }
}

internal sealed record PackageModuleTypeShape(
    string TypeName,
    PackageHostRoleMetadataValue Roles,
    bool IsTopLevelPublicClass,
    bool IsOpenGeneric,
    bool HasPublicParameterlessConstructor);
