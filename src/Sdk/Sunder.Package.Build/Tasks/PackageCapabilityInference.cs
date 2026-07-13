using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Sunder.Sdk.Compatibility;

namespace Sunder.Package.Build.Tasks;

internal sealed class PackageCapabilityInference(
    IReadOnlyList<string> configuredCapabilities,
    IReadOnlyList<string> dependencyPaths)
{
    private static readonly OpCode[] SingleByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] MultiByteOpCodes = new OpCode[0x100];
    private readonly List<string> _diagnostics = [];
    private readonly SortedSet<string> _dynamicSdkCallSites = new(StringComparer.Ordinal);

    public bool IsComplete { get; private set; }

    public IReadOnlyList<string> Diagnostics => _diagnostics;

    static PackageCapabilityInference()
    {
        foreach (var field in typeof(OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode opCode)
            {
                continue;
            }

            var value = unchecked((ushort)opCode.Value);
            if (value < 0x100)
            {
                SingleByteOpCodes[value] = opCode;
            }
            else if ((value & 0xff00) == 0xfe00)
            {
                MultiByteOpCodes[value & 0xff] = opCode;
            }
        }
    }

    public IReadOnlyList<string> Infer(string assemblyPath)
    {
        _diagnostics.Clear();
        _dynamicSdkCallSites.Clear();
        IsComplete = false;
        var capabilities = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SunderSdkCapabilities.Baseline11V1,
            SunderSdkCapabilities.CoreV1,
            SunderSdkCapabilities.PackagingV1,
            SunderSdkCapabilities.ContributionsV1,
        };

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                _diagnostics.Add($"Package assembly '{assemblyPath}' does not contain managed metadata.");
                return capabilities.ToArray();
            }

            var metadata = peReader.GetMetadataReader();
            ValidateSdkAssemblyReferences(metadata, assemblyPath);
            InspectMetadataReferences(metadata, capabilities);
            InspectAuthoredMethodBodies(peReader, metadata, capabilities);
        }
        catch (Exception exception)
        {
            _diagnostics.Add($"Could not inspect package metadata in '{assemblyPath}': {exception.GetType().Name}: {exception.Message}");
        }

        var explicitCapabilities = configuredCapabilities
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToArray();
        foreach (var capability in explicitCapabilities)
        {
            capabilities.Add(capability);
        }

        if (_dynamicSdkCallSites.Count > 0 && explicitCapabilities.Length == 0)
        {
            _diagnostics.Add(
                "Capability inference found dynamic/reflection access in package-authored code at "
                + string.Join(", ", _dynamicSdkCallSites)
                + ". Declare every dynamically accessed capability with <SunderSdkCapability Include=\"...\" />.");
        }

        IsComplete = _diagnostics.Count == 0;
        return capabilities.ToArray();
    }

    private void ValidateSdkAssemblyReferences(MetadataReader metadata, string assemblyPath)
    {
        var availableFiles = dependencyPaths
            .Append(assemblyPath)
            .Concat(Directory.EnumerateFiles(Path.GetDirectoryName(assemblyPath)!, "*.dll"))
            .Where(static path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(static path => Path.GetFileNameWithoutExtension(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var handle in metadata.AssemblyReferences)
        {
            var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            if (IsSdkContractAssemblyName(name) && !availableFiles.Contains(name))
            {
                _diagnostics.Add(
                    $"Referenced Sunder SDK assembly '{name}' could not be resolved from the MSBuild reference or runtime copy-local paths; "
                    + "capability inference cannot safely determine its SDK usage.");
            }
        }
    }

    private static void InspectMetadataReferences(MetadataReader metadata, ISet<string> capabilities)
    {
        foreach (var handle in metadata.TypeReferences)
        {
            var identity = GetTypeReferenceIdentity(metadata, handle);
            if (identity.IsSdk)
            {
                AddSdkTypeReferenceCapability(identity.Namespace, identity.Name, capabilities);
            }
        }

        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            var identity = GetParentTypeIdentity(metadata, member.Parent);
            if (identity is { IsSdk: true } sdkType)
            {
                AddKnownSdkMemberCapability(sdkType.Namespace, sdkType.Name, metadata.GetString(member.Name), capabilities);
            }
        }
    }

    private void InspectAuthoredMethodBodies(
        PEReader peReader,
        MetadataReader metadata,
        ISet<string> capabilities)
    {
        foreach (var handle in metadata.MethodDefinitions)
        {
            var method = metadata.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0 || IsGeneratedMethod(metadata, handle))
            {
                continue;
            }

            var declaringTypeHandle = method.GetDeclaringType();
            var typeName = GetTypeDisplayName(metadata, declaringTypeHandle);
            var methodName = metadata.GetString(method.Name);
            var callSite = $"{typeName}.{methodName}";
            byte[] il;
            try
            {
                il = peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
            }
            catch (Exception exception)
            {
                _diagnostics.Add($"Could not read package-authored IL in '{callSite}': {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            InspectMethodBody(metadata, il, callSite, capabilities);
        }
    }

    private void InspectMethodBody(
        MetadataReader metadata,
        byte[] il,
        string callSite,
        ISet<string> capabilities)
    {
        for (var index = 0; index < il.Length;)
        {
            var opCode = ReadOpCode(il, ref index);
            if (opCode.Size == 0)
            {
                _diagnostics.Add($"Could not decode package-authored IL in '{callSite}'; the unknown instruction may hide a Sunder SDK call.");
                return;
            }

            if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                if (index + sizeof(int) > il.Length)
                {
                    _diagnostics.Add($"Could not decode the metadata token in package-authored IL at '{callSite}'; it may hide a Sunder SDK call.");
                    return;
                }

                var token = BitConverter.ToInt32(il.AsSpan(index, sizeof(int)));
                index += sizeof(int);
                InspectToken(metadata, token, callSite, capabilities);
                continue;
            }

            var operandSize = OperandSize(opCode.OperandType, il, index);
            if (operandSize < 0 || index + operandSize > il.Length)
            {
                _diagnostics.Add($"Could not decode an operand in package-authored IL at '{callSite}'; it may hide a Sunder SDK call.");
                return;
            }
            index += operandSize;
        }
    }

    private void InspectToken(MetadataReader metadata, int token, string callSite, ISet<string> capabilities)
    {
        EntityHandle handle;
        try
        {
            handle = MetadataTokens.EntityHandle(token);
        }
        catch (Exception exception)
        {
            _diagnostics.Add(
                $"Could not classify metadata token 0x{token:x8} in package-authored IL at '{callSite}': "
                + $"{exception.Message} The token may hide a Sunder SDK call.");
            return;
        }

        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                InspectMemberReference(metadata, (MemberReferenceHandle)handle, callSite, capabilities);
                break;
            case HandleKind.MethodSpecification:
                InspectMethodSpecification(metadata, (MethodSpecificationHandle)handle, callSite, capabilities);
                break;
            case HandleKind.TypeReference:
                var identity = GetTypeReferenceIdentity(metadata, (TypeReferenceHandle)handle);
                if (identity.IsSdk)
                {
                    AddSdkTypeReferenceCapability(identity.Namespace, identity.Name, capabilities);
                }
                break;
            case HandleKind.TypeSpecification:
                if (!TryDecodeTypeSpecification(metadata, (TypeSpecificationHandle)handle, out var specificationType))
                {
                    _diagnostics.Add(
                        $"Could not classify type metadata token 0x{token:x8} in package-authored IL at '{callSite}'; "
                        + "the type may be a Sunder SDK contract.");
                }
                else if (specificationType.IsSdk)
                {
                    AddSdkTypeReferenceCapability(specificationType.Namespace, specificationType.Name, capabilities);
                }
                break;
            case HandleKind.MethodDefinition:
            case HandleKind.FieldDefinition:
            case HandleKind.TypeDefinition:
                break;
            default:
                _diagnostics.Add(
                    $"Could not classify metadata token 0x{token:x8} ({handle.Kind}) in package-authored IL at '{callSite}'; "
                    + "the token may hide a Sunder SDK call.");
                break;
        }
    }

    private void InspectMethodSpecification(
        MetadataReader metadata,
        MethodSpecificationHandle handle,
        string callSite,
        ISet<string> capabilities)
    {
        var method = metadata.GetMethodSpecification(handle).Method;
        if (method.Kind == HandleKind.MemberReference)
        {
            InspectMemberReference(metadata, (MemberReferenceHandle)method, callSite, capabilities);
        }
        else if (method.Kind != HandleKind.MethodDefinition)
        {
            _diagnostics.Add($"Could not classify a generic method target in package-authored IL at '{callSite}'; it may hide a Sunder SDK call.");
        }
    }

    private void InspectMemberReference(
        MetadataReader metadata,
        MemberReferenceHandle handle,
        string callSite,
        ISet<string> capabilities)
    {
        var member = metadata.GetMemberReference(handle);
        var identity = GetParentTypeIdentity(metadata, member.Parent);
        if (identity is not { } declaringType)
        {
            if (member.Parent.Kind is HandleKind.ModuleReference or HandleKind.MethodDefinition)
            {
                return;
            }
            _diagnostics.Add(
                $"Could not classify external member token 0x{MetadataTokens.GetToken(handle):x8} in package-authored IL at '{callSite}'; "
                + "its declaring type may be a Sunder SDK contract.");
            return;
        }

        var memberName = metadata.GetString(member.Name);
        if (declaringType.IsSdk)
        {
            AddSdkTypeReferenceCapability(declaringType.Namespace, declaringType.Name, capabilities);
            AddKnownSdkMemberCapability(declaringType.Namespace, declaringType.Name, memberName, capabilities);
        }
        else if (IsDynamicAccessMember(declaringType, memberName))
        {
            _dynamicSdkCallSites.Add(callSite);
        }
        // A resolvable metadata reference to a non-SDK assembly cannot hide an SDK call.
    }

    private static bool IsGeneratedMethod(MetadataReader metadata, MethodDefinitionHandle methodHandle)
    {
        var method = metadata.GetMethodDefinition(methodHandle);
        var typeHandle = method.GetDeclaringType();
        var typeNamespace = GetTypeNamespace(metadata, typeHandle);
        var methodName = metadata.GetString(method.Name);
        if (typeNamespace == "CompiledAvaloniaXaml"
            || typeNamespace.StartsWith("CompiledAvaloniaXaml.", StringComparison.Ordinal)
            || methodName.StartsWith("!XamlIl", StringComparison.Ordinal))
        {
            return true;
        }

        return HasGeneratedCodeAttribute(metadata, method.GetCustomAttributes())
            || HasGeneratedCodeAttribute(metadata, metadata.GetTypeDefinition(typeHandle).GetCustomAttributes());
    }

    private static bool HasGeneratedCodeAttribute(MetadataReader metadata, CustomAttributeHandleCollection attributes)
    {
        foreach (var handle in attributes)
        {
            var name = GetAttributeTypeName(metadata, metadata.GetCustomAttribute(handle).Constructor);
            if (name is "System.Runtime.CompilerServices.CompilerGeneratedAttribute"
                or "System.CodeDom.Compiler.GeneratedCodeAttribute")
            {
                return true;
            }
        }
        return false;
    }

    private static string? GetAttributeTypeName(MetadataReader metadata, EntityHandle constructor)
    {
        EntityHandle typeHandle = constructor.Kind switch
        {
            HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            _ => default,
        };
        return typeHandle.Kind switch
        {
            HandleKind.TypeReference => GetTypeReferenceIdentity(metadata, (TypeReferenceHandle)typeHandle).FullName,
            HandleKind.TypeDefinition => GetTypeDisplayName(metadata, (TypeDefinitionHandle)typeHandle),
            _ => null,
        };
    }

    private static MetadataTypeIdentity? GetParentTypeIdentity(MetadataReader metadata, EntityHandle parent)
        => parent.Kind switch
        {
            HandleKind.TypeReference => GetTypeReferenceIdentity(metadata, (TypeReferenceHandle)parent),
            HandleKind.TypeDefinition => GetTypeDefinitionIdentity(metadata, (TypeDefinitionHandle)parent),
            HandleKind.TypeSpecification => TryDecodeTypeSpecification(metadata, (TypeSpecificationHandle)parent, out var identity) ? identity : null,
            _ => null,
        };

    private static bool TryDecodeTypeSpecification(
        MetadataReader metadata,
        TypeSpecificationHandle handle,
        out MetadataTypeIdentity identity)
    {
        try
        {
            identity = metadata.GetTypeSpecification(handle).DecodeSignature(MetadataTypeProvider.Instance, genericContext: null);
            return true;
        }
        catch
        {
            identity = default;
            return false;
        }
    }

    private static MetadataTypeIdentity GetTypeReferenceIdentity(MetadataReader metadata, TypeReferenceHandle handle)
    {
        var type = metadata.GetTypeReference(handle);
        var assemblyName = GetResolutionAssemblyName(metadata, type.ResolutionScope);
        return new MetadataTypeIdentity(
            assemblyName,
            metadata.GetString(type.Namespace),
            metadata.GetString(type.Name));
    }

    private static MetadataTypeIdentity GetTypeDefinitionIdentity(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        return new MetadataTypeIdentity(null, GetTypeNamespace(metadata, handle), metadata.GetString(type.Name));
    }

    private static string? GetResolutionAssemblyName(MetadataReader metadata, EntityHandle scope)
        => scope.Kind switch
        {
            HandleKind.AssemblyReference => metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
            HandleKind.TypeReference => GetTypeReferenceIdentity(metadata, (TypeReferenceHandle)scope).AssemblyName,
            _ => null,
        };

    private static string GetTypeDisplayName(MetadataReader metadata, TypeDefinitionHandle handle)
    {
        var type = metadata.GetTypeDefinition(handle);
        var declaringType = type.GetDeclaringType();
        return declaringType.IsNil
            ? JoinTypeName(GetTypeNamespace(metadata, handle), metadata.GetString(type.Name))
            : GetTypeDisplayName(metadata, declaringType) + "+" + metadata.GetString(type.Name);
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

    private static string JoinTypeName(string @namespace, string name)
        => string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;

    private static bool IsDynamicAccessMember(MetadataTypeIdentity type, string memberName)
    {
        if (type.Namespace == "System" && type.Name == "Activator")
        {
            return memberName.StartsWith("CreateInstance", StringComparison.Ordinal);
        }
        if (type.Namespace == "System" && type.Name == "Type")
        {
            return memberName is "GetType" or "GetMethod" or "GetMethods" or "GetProperty" or "GetProperties"
                or "GetField" or "GetFields" or "GetEvent" or "GetEvents" or "GetMember" or "GetMembers"
                or "InvokeMember" or "MakeGenericType";
        }
        if (type.Namespace == "System.Reflection" && type.Name == "Assembly")
        {
            return memberName is "GetType" or "GetTypes" or "GetExportedTypes" or "CreateInstance";
        }
        return type.Namespace == "System.Reflection"
            && type.Name is "MethodInfo" or "MethodBase" or "ConstructorInfo" or "PropertyInfo" or "FieldInfo"
            && memberName is "Invoke" or "GetValue" or "SetValue";
    }

    internal static bool IsSdkContractAssemblyName(string? assemblyName)
        => assemblyName?.Equals("Sunder.Sdk", StringComparison.OrdinalIgnoreCase) == true
           || assemblyName?.StartsWith("Sunder.Sdk.", StringComparison.OrdinalIgnoreCase) == true;

    private static void AddSdkTypeReferenceCapability(string @namespace, string name, ISet<string> capabilities)
    {
        switch (@namespace, name)
        {
            case ("Sunder.Sdk.Abstractions", "ISunderRuntimePackageModule"):
            case ("Sunder.Sdk.Abstractions", "ISunderAppPackageModule"):
            case ("Sunder.Sdk.Abstractions", "IPackageContext"):
                capabilities.Add(SunderSdkCapabilities.CoreV1);
                break;
            case ("Sunder.Sdk.Packaging", _):
                capabilities.Add(SunderSdkCapabilities.PackagingV1);
                break;
            case ("Sunder.Sdk.Abstractions", "ISunderRuntimeContributionRegistry"):
            case ("Sunder.Sdk.Abstractions", "ISunderAppContributionRegistry"):
            case ("Sunder.Sdk.Avalonia", "IAvaloniaPackageContributionRegistry"):
                capabilities.Add(SunderSdkCapabilities.ContributionsV1);
                break;
            case ("Sunder.Sdk.Abstractions", "PackageViewRegistration"):
            case ("Sunder.Sdk.Abstractions", "PackageViewPlacement"):
                capabilities.Add(SunderSdkCapabilities.ViewsV1);
                break;
            case ("Sunder.Sdk.Avalonia", "IPackageWorkspaceFactory"):
                capabilities.Add(SunderSdkCapabilities.WorkspacesV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageBackgroundService"):
                capabilities.Add(SunderSdkCapabilities.BackgroundServicesV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IBackgroundProcessQueue"):
            case ("Sunder.Sdk.Abstractions", "BackgroundProcessRequest"):
            case ("Sunder.Sdk.Abstractions", "BackgroundProcessSnapshot"):
            case ("Sunder.Sdk.Abstractions", "BackgroundProcessContext"):
            case ("Sunder.Sdk.Abstractions", "BackgroundProcessChangedEventArgs"):
            case ("Sunder.Sdk.Abstractions", "BackgroundProcessConcurrencyMode"):
            case ("Sunder.Sdk.Abstractions", "BackgroundProcessState"):
            case ("Sunder.Sdk.Abstractions", "BackgroundProcessIndicator"):
                capabilities.Add(SunderSdkCapabilities.BackgroundProcessesV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageExtensionCatalog"):
            case ("Sunder.Sdk.Abstractions", "PackageExtensionContribution`1"):
            case ("Sunder.Sdk.Abstractions", "PackageExtensionPoint`1"):
                capabilities.Add(SunderSdkCapabilities.ExtensionsV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageExtensionCatalogMonitor"):
            case ("Sunder.Sdk.Abstractions", "IPackageExtensionCatalogChangeNotifier"):
            case ("Sunder.Sdk.Abstractions", "PackageExtensionCatalogChangedEventArgs"):
            case ("Sunder.Sdk.Abstractions", "PackageExtensionCatalogChangeReason"):
            case ("Sunder.Sdk.Abstractions", "PackageExtensionChangeKind"):
            case ("Sunder.Sdk.Abstractions", "PackageExtensionChange"):
                capabilities.Add(SunderSdkCapabilities.ExtensionChangesV1);
                break;
            case ("Sunder.Sdk.Configuration", _):
                capabilities.Add(SunderSdkCapabilities.ConfigurationSchemaV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageSettings"):
                capabilities.Add(SunderSdkCapabilities.SettingsV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageStorageContext"):
            case ("Sunder.Sdk.Abstractions", "IPackageFileStore"):
            case ("Sunder.Sdk.Abstractions", "IPackageKeyValueStore"):
                capabilities.Add(SunderSdkCapabilities.StorageV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageRoleLocalWorkspace"):
                capabilities.Add(SunderSdkCapabilities.RoleLocalWorkspaceV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageSecrets"):
                capabilities.Add(SunderSdkCapabilities.SecretsV1);
                break;
            case ("Sunder.Sdk.Logging", _):
                capabilities.Add(SunderSdkCapabilities.LoggingV1);
                break;
            case ("Sunder.Sdk.Notifications", _):
                capabilities.Add(SunderSdkCapabilities.NotificationsV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageShellViewService"):
            case ("Sunder.Sdk.Abstractions", "IPackageViewNavigationTarget"):
            case ("Sunder.Sdk.Abstractions", "PackageViewNavigationContext"):
            case ("Sunder.Sdk.Abstractions", "PackageHotbarView"):
            case ("Sunder.Sdk.Abstractions", "PackageHotbarPlacement"):
                capabilities.Add(SunderSdkCapabilities.ShellViewV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageSettingsNavigationService"):
            case ("Sunder.Sdk.Abstractions", "NullPackageSettingsNavigationService"):
                capabilities.Add(SunderSdkCapabilities.SettingsNavigationV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageInstalledSessionControl"):
            case ("Sunder.Sdk.Abstractions", "InstalledPackageSessionStatus"):
                capabilities.Add(SunderSdkCapabilities.InstalledPackageSessionsV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageDevelopmentSessionControl"):
            case ("Sunder.Sdk.Abstractions", "PackageDevelopmentSessionAvailability"):
            case ("Sunder.Sdk.Abstractions", "PackageDevelopmentSessionLoadRequest"):
            case ("Sunder.Sdk.Abstractions", "PackageDevelopmentSessionOperationResult"):
            case ("Sunder.Sdk.Abstractions", "PackageDevelopmentSessionOperationOutcome"):
            case ("Sunder.Sdk.Abstractions", "PackageDevelopmentSessionStatus"):
            case ("Sunder.Sdk.Abstractions", "UnavailablePackageDevelopmentSessionControl"):
                capabilities.Add(SunderSdkCapabilities.DevelopmentPackageSessionsV1);
                break;
            case ("Sunder.Sdk.Runtime", _):
                capabilities.Add(SunderSdkCapabilities.RuntimeOperationsV1);
                break;
            case ("Sunder.Sdk.Stacks", "IPackageStackContributor"):
            case ("Sunder.Sdk.Stacks", "IPackageStackImportAppliedHandler"):
            case ("Sunder.Sdk.Stacks", "SunderStackExtensionPoints"):
                capabilities.Add(SunderSdkCapabilities.StacksV1);
                capabilities.Add(SunderSdkCapabilities.StackContributionsV1);
                break;
            case ("Sunder.Sdk.Stacks", _):
                capabilities.Add(SunderSdkCapabilities.StacksV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageCallbackHandler"):
            case ("Sunder.Sdk.Abstractions", "IPackageCallbackClient"):
            case ("Sunder.Sdk.Abstractions", "NullPackageCallbackClient"):
            case ("Sunder.Sdk.Callbacks", _):
                capabilities.Add(SunderSdkCapabilities.CallbacksV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageAuthHandler"):
            case ("Sunder.Sdk.Authentication", _):
                capabilities.Add(SunderSdkCapabilities.AuthV1);
                break;
            case ("Sunder.Sdk.Avalonia.Theming", "SunderThemeKeys"):
                capabilities.Add(SunderSdkCapabilities.ThemingV1);
                break;
        }
    }

    private static void AddKnownSdkMemberCapability(
        string @namespace,
        string typeName,
        string memberName,
        ISet<string> capabilities)
    {
        if (@namespace == "Sunder.Sdk.Abstractions"
            && typeName == "IPackageContext"
            && memberName is "get_Settings" or "get_Callbacks")
        {
            capabilities.Add(memberName == "get_Settings"
                ? SunderSdkCapabilities.SettingsV1
                : SunderSdkCapabilities.CallbacksV1);
            return;
        }

        if ((@namespace == "Sunder.Sdk.Abstractions"
                && typeName is "ISunderRuntimeContributionRegistry" or "ISunderAppContributionRegistry")
            || (@namespace == "Sunder.Sdk.Avalonia"
                && typeName is "IAvaloniaPackageContributionRegistry" or "AvaloniaPackageContributionRegistryExtensions"))
        {
            switch (memberName)
            {
                case "RegisterPackageView":
                    capabilities.Add(SunderSdkCapabilities.ViewsV1);
                    break;
                case "RegisterPackageViewFactory":
                    capabilities.Add(SunderSdkCapabilities.WorkspacesV1);
                    break;
                case "RegisterSettingsView":
                case "RegisterSettingsViewFactory":
                    capabilities.Add(SunderSdkCapabilities.SettingsViewsV1);
                    break;
                case "RegisterBackgroundService":
                    capabilities.Add(SunderSdkCapabilities.BackgroundServicesV1);
                    break;
                case "RegisterExtension":
                    capabilities.Add(SunderSdkCapabilities.ExtensionsV1);
                    break;
                case "RegisterConfigurationSchema":
                    capabilities.Add(SunderSdkCapabilities.ConfigurationSchemaV1);
                    break;
                case "RegisterRuntimeOperation":
                case "RegisterRuntimeStream":
                    capabilities.Add(SunderSdkCapabilities.RuntimeOperationsV1);
                    break;
            }
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int index)
    {
        if (index >= il.Length)
        {
            return default;
        }
        var code = il[index++];
        if (code != 0xfe)
        {
            return SingleByteOpCodes[code];
        }
        return index < il.Length ? MultiByteOpCodes[il[index++]] : default;
    }

    private static int OperandSize(OperandType operandType, byte[] il, int index)
        => operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineSig or OperandType.InlineString => 4,
            OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => index + 4 > il.Length ? -1 : 4 + (BitConverter.ToInt32(il.AsSpan(index, 4)) * 4),
            _ => -1,
        };

    private readonly record struct MetadataTypeIdentity(string? AssemblyName, string Namespace, string Name)
    {
        public string FullName => JoinTypeName(Namespace, Name);
        public bool IsSdk => IsSdkContractAssemblyName(AssemblyName);
    }

    private sealed class MetadataTypeProvider : ISignatureTypeProvider<MetadataTypeIdentity, object?>
    {
        public static MetadataTypeProvider Instance { get; } = new();

        public MetadataTypeIdentity GetArrayType(MetadataTypeIdentity elementType, ArrayShape shape) => elementType;
        public MetadataTypeIdentity GetByReferenceType(MetadataTypeIdentity elementType) => elementType;
        public MetadataTypeIdentity GetFunctionPointerType(MethodSignature<MetadataTypeIdentity> signature) => default;
        public MetadataTypeIdentity GetGenericInstantiation(MetadataTypeIdentity genericType, ImmutableArray<MetadataTypeIdentity> typeArguments) => genericType;
        public MetadataTypeIdentity GetGenericMethodParameter(object? genericContext, int index) => default;
        public MetadataTypeIdentity GetGenericTypeParameter(object? genericContext, int index) => default;
        public MetadataTypeIdentity GetModifiedType(MetadataTypeIdentity modifier, MetadataTypeIdentity unmodifiedType, bool isRequired) => unmodifiedType;
        public MetadataTypeIdentity GetPinnedType(MetadataTypeIdentity elementType) => elementType;
        public MetadataTypeIdentity GetPointerType(MetadataTypeIdentity elementType) => elementType;
        public MetadataTypeIdentity GetPrimitiveType(PrimitiveTypeCode typeCode) => new("System.Private.CoreLib", "System", typeCode.ToString());
        public MetadataTypeIdentity GetSZArrayType(MetadataTypeIdentity elementType) => elementType;
        public MetadataTypeIdentity GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => GetTypeDefinitionIdentity(reader, handle);
        public MetadataTypeIdentity GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => GetTypeReferenceIdentity(reader, handle);
        public MetadataTypeIdentity GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public MetadataTypeIdentity GetUnsupportedSignatureTypeKind(byte rawTypeKind) => default;
    }
}
