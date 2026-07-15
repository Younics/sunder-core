using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Sunder.Package.Build.Tasks;

internal sealed class SdkCapabilityCatalog
{
    private const string CapabilityAttributeName = "Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute";
    private readonly Dictionary<ContractType, HashSet<string>> _typeCapabilities = [];
    private readonly Dictionary<ContractMember, HashSet<string>> _memberCapabilities = [];

    public static SdkCapabilityCatalog Create(
        IEnumerable<string> candidatePaths,
        ICollection<string> diagnostics)
    {
        var catalog = new SdkCapabilityCatalog();
        foreach (var path in candidatePaths
                     .Where(static path => ManagedAssemblyPath.IsCandidate(path) && File.Exists(path))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!PackageCapabilityInference.IsSdkContractAssemblyName(Path.GetFileNameWithoutExtension(path)))
            {
                continue;
            }

            try
            {
                catalog.ReadAssembly(path);
            }
            catch (Exception exception)
            {
                diagnostics.Add(
                    $"Could not read SDK capability annotations from '{path}': "
                    + $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        return catalog;
    }

    public bool AddTypeCapabilities(
        string? assemblyName,
        string @namespace,
        string name,
        ISet<string> capabilities)
        => AddCapabilities(_typeCapabilities, new ContractType(assemblyName ?? string.Empty, @namespace, name), capabilities);

    public bool AddMemberCapabilities(
        string? assemblyName,
        string @namespace,
        string typeName,
        string memberName,
        ISet<string> capabilities)
        => AddCapabilities(
            _memberCapabilities,
            new ContractMember(new ContractType(assemblyName ?? string.Empty, @namespace, typeName), memberName),
            capabilities);

    private static bool AddCapabilities<TKey>(
        IReadOnlyDictionary<TKey, HashSet<string>> source,
        TKey key,
        ISet<string> destination)
        where TKey : notnull
    {
        if (!source.TryGetValue(key, out var declared))
        {
            return false;
        }

        foreach (var capability in declared)
        {
            destination.Add(capability);
        }
        return true;
    }

    private void ReadAssembly(string path)
    {
        using var stream = File.OpenRead(path);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            throw new InvalidDataException("The file does not contain managed metadata.");
        }

        var metadata = peReader.GetMetadataReader();
        var assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        foreach (var typeHandle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(typeHandle);
            var typeKey = new ContractType(
                assemblyName,
                GetTypeNamespace(metadata, typeHandle),
                metadata.GetString(type.Name));
            AddDeclaredCapabilities(metadata, type.GetCustomAttributes(), GetOrAdd(_typeCapabilities, typeKey));

            foreach (var methodHandle in type.GetMethods())
            {
                var method = metadata.GetMethodDefinition(methodHandle);
                AddDeclaredCapabilities(
                    metadata,
                    method.GetCustomAttributes(),
                    GetOrAdd(_memberCapabilities, new ContractMember(typeKey, metadata.GetString(method.Name))));
            }

            foreach (var propertyHandle in type.GetProperties())
            {
                var property = metadata.GetPropertyDefinition(propertyHandle);
                var capabilities = ReadDeclaredCapabilities(metadata, property.GetCustomAttributes());
                var accessors = property.GetAccessors();
                AddAccessorCapabilities(metadata, typeKey, accessors.Getter, capabilities);
                AddAccessorCapabilities(metadata, typeKey, accessors.Setter, capabilities);
            }

            foreach (var eventHandle in type.GetEvents())
            {
                var @event = metadata.GetEventDefinition(eventHandle);
                var capabilities = ReadDeclaredCapabilities(metadata, @event.GetCustomAttributes());
                var accessors = @event.GetAccessors();
                AddAccessorCapabilities(metadata, typeKey, accessors.Adder, capabilities);
                AddAccessorCapabilities(metadata, typeKey, accessors.Remover, capabilities);
                AddAccessorCapabilities(metadata, typeKey, accessors.Raiser, capabilities);
            }

            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                AddDeclaredCapabilities(
                    metadata,
                    field.GetCustomAttributes(),
                    GetOrAdd(_memberCapabilities, new ContractMember(typeKey, metadata.GetString(field.Name))));
            }
        }
    }

    private void AddAccessorCapabilities(
        MetadataReader metadata,
        ContractType type,
        MethodDefinitionHandle accessorHandle,
        IReadOnlyList<string> capabilities)
    {
        if (accessorHandle.IsNil || capabilities.Count == 0)
        {
            return;
        }

        var accessor = metadata.GetMethodDefinition(accessorHandle);
        GetOrAdd(_memberCapabilities, new ContractMember(type, metadata.GetString(accessor.Name)))
            .UnionWith(capabilities);
    }

    private static HashSet<string> GetOrAdd<TKey>(Dictionary<TKey, HashSet<string>> dictionary, TKey key)
        where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out var values))
        {
            values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            dictionary.Add(key, values);
        }
        return values;
    }

    private static IReadOnlyList<string> ReadDeclaredCapabilities(
        MetadataReader metadata,
        CustomAttributeHandleCollection attributes)
    {
        var capabilities = new List<string>();
        AddDeclaredCapabilities(metadata, attributes, capabilities);
        return capabilities;
    }

    private static void AddDeclaredCapabilities(
        MetadataReader metadata,
        CustomAttributeHandleCollection attributes,
        ICollection<string> capabilities)
    {
        foreach (var handle in attributes)
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (!string.Equals(GetAttributeTypeName(metadata, attribute.Constructor), CapabilityAttributeName, StringComparison.Ordinal))
            {
                continue;
            }

            var reader = metadata.GetBlobReader(attribute.Value);
            if (reader.ReadUInt16() != 1)
            {
                throw new InvalidDataException("A Sunder SDK capability annotation has an invalid custom-attribute prolog.");
            }

            var capability = reader.ReadSerializedString();
            if (string.IsNullOrWhiteSpace(capability))
            {
                throw new InvalidDataException("A Sunder SDK capability annotation is empty.");
            }
            capabilities.Add(capability);
        }
    }

    private static string? GetAttributeTypeName(MetadataReader metadata, EntityHandle constructor)
    {
        var typeHandle = constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default,
        };
        return typeHandle.Kind switch
        {
            HandleKind.TypeReference => GetTypeReferenceName(metadata, (TypeReferenceHandle)typeHandle),
            HandleKind.TypeDefinition => GetTypeDefinitionName(metadata, (TypeDefinitionHandle)typeHandle),
            _ => null,
        };
    }

    private static string GetTypeReferenceName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        var type = metadata.GetTypeReference(handle);
        var @namespace = metadata.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace)
            ? metadata.GetString(type.Name)
            : @namespace + "." + metadata.GetString(type.Name);
    }

    private static string GetTypeDefinitionName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var @namespace = GetTypeNamespace(metadata, handle);
        return string.IsNullOrEmpty(@namespace)
            ? metadata.GetString(type.Name)
            : @namespace + "." + metadata.GetString(type.Name);
    }

    private static string GetTypeNamespace(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var @namespace = metadata.GetString(type.Namespace);
        if (!string.IsNullOrEmpty(@namespace))
        {
            return @namespace;
        }
        var declaringType = type.GetDeclaringType();
        return declaringType.IsNil ? string.Empty : GetTypeNamespace(metadata, declaringType);
    }

    private readonly record struct ContractType(string AssemblyName, string Namespace, string Name);
    private readonly record struct ContractMember(ContractType Type, string Name);
}
