using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.Build.Utilities;
using Sunder.Package.Format;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Tasks;

internal sealed class PackageMetadataDecoder(
    string assemblyPath,
    PackageDependencyExtractor dependencyExtractor,
    PackageCapabilityInference capabilityInference,
    TaskLoggingHelper log)
{
    private static readonly string PackageAttributeName = typeof(SunderPackageAttribute).FullName!;
    private static readonly string PackageDependencyAttributeName = typeof(SunderPackageDependencyAttribute).FullName!;

    public PackageManifestMetadata? Decode()
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                log.LogError($"Sunder package assembly '{assemblyPath}' does not contain managed metadata.");
                return null;
            }

            var metadata = peReader.GetMetadataReader();
            var attributes = ReadPackageAttributes(metadata);
            var packageAttributes = attributes
                .Where(attribute => attribute.TypeName == PackageAttributeName)
                .ToArray();

            if (packageAttributes.Length == 0)
            {
                log.LogError($"Sunder package assembly '{assemblyPath}' must declare one SunderPackage attribute.");
                return null;
            }

            if (packageAttributes.Length > 1)
            {
                log.LogError($"Sunder package assembly '{assemblyPath}' declares multiple SunderPackage attributes.");
                return null;
            }

            var packageAttribute = packageAttributes[0];
            var moduleShape = PackageModuleShapeReader.Read(assemblyPath);
            var moduleShapeErrors = moduleShape.Validate();
            if (moduleShapeErrors.Count > 0)
            {
                foreach (var error in moduleShapeErrors)
                {
                    log.LogError(error);
                }
                return null;
            }
            return new PackageManifestMetadata(
                GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Id)) ?? string.Empty,
                GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Name)) ?? string.Empty,
                GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Summary)),
                GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Icon)),
                PackageHostRoleMetadata.ToImplementedRoles(moduleShape.Roles),
                dependencyExtractor.Extract(attributes),
                capabilityInference.Infer(assemblyPath));
        }
        catch (Exception ex)
        {
            log.LogErrorFromException(ex, showStackTrace: false);
            return null;
        }
    }

    internal static string? GetNamedString(PackageCustomAttribute attribute, string name)
        => attribute.NamedStrings.GetValueOrDefault(name);

    private static IReadOnlyList<PackageCustomAttribute> ReadPackageAttributes(MetadataReader metadata)
    {
        var attributes = new List<PackageCustomAttribute>();
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            var typeName = GetAttributeTypeName(metadata, attribute.Constructor);
            if (typeName != PackageAttributeName && typeName != PackageDependencyAttributeName)
            {
                continue;
            }

            var value = attribute.DecodeValue(AttributeTypeProvider.Instance);
            var namedStrings = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var argument in value.NamedArguments)
            {
                if (argument.Name is null)
                {
                    throw new BadImageFormatException($"Custom attribute '{typeName}' contains an unnamed argument.");
                }
                namedStrings.Add(argument.Name, argument.Value as string);
            }
            attributes.Add(new PackageCustomAttribute(typeName, namedStrings));
        }
        return attributes;
    }

    private static string GetAttributeTypeName(MetadataReader metadata, EntityHandle constructor)
    {
        var type = constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => throw new BadImageFormatException($"Unsupported custom attribute constructor handle '{constructor.Kind}'."),
        };
        return GetTypeName(metadata, type);
    }

    private static string GetTypeName(MetadataReader metadata, EntityHandle type)
        => type.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeName(metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)type)),
            HandleKind.TypeReference => GetTypeName(metadata, metadata.GetTypeReference((TypeReferenceHandle)type)),
            _ => throw new BadImageFormatException($"Unsupported custom attribute type handle '{type.Kind}'."),
        };

    private static string GetTypeName(MetadataReader metadata, TypeDefinition type)
        => JoinTypeName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));

    private static string GetTypeName(MetadataReader metadata, TypeReference type)
        => JoinTypeName(metadata.GetString(type.Namespace), metadata.GetString(type.Name));

    private static string JoinTypeName(string typeNamespace, string typeName)
        => string.IsNullOrEmpty(typeNamespace) ? typeName : $"{typeNamespace}.{typeName}";

    private sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static AttributeTypeProvider Instance { get; } = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => typeof(Type).FullName!;
        public bool IsSystemType(string type) => type == typeof(Type).FullName;
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => GetTypeName(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => GetTypeName(reader, reader.GetTypeReference(handle));
    }
}

internal sealed record PackageCustomAttribute(
    string TypeName,
    IReadOnlyDictionary<string, string?> NamedStrings);
