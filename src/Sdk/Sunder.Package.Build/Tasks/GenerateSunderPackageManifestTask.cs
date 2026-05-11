using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Build.Tasks;

public sealed class GenerateSunderPackageManifestTask : Microsoft.Build.Utilities.Task
{
    private static readonly OpCode[] SingleByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] MultiByteOpCodes = new OpCode[0x100];
    private static readonly Regex PackageIdRegex = new("^[a-z0-9]+(\\.[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SemVerRegex = new("^\\d+\\.\\d+\\.\\d+([-.+][0-9A-Za-z.-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    static GenerateSunderPackageManifestTask()
    {
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
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

    [Required]
    public string TargetAssemblyPath { get; set; } = string.Empty;

    [Required]
    public string ManifestOutputPath { get; set; } = string.Empty;

    [Required]
    public string EntryAssembly { get; set; } = string.Empty;

    [Required]
    public string PackageVersion { get; set; } = string.Empty;

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    public string? TargetFramework { get; set; }

    public string? SdkVersion { get; set; }

    public string? SdkApiVersion { get; set; }

    public string? SdkPackageVersion { get; set; }

    public ITaskItem[] SdkCapabilities { get; set; } = [];

    public override bool Execute()
    {
        if (!File.Exists(TargetAssemblyPath))
        {
            Log.LogError($"Sunder package entry assembly was not found at '{TargetAssemblyPath}'.");
            return false;
        }

        var metadata = ReadPackageMetadata();
        if (metadata is null)
        {
            return false;
        }

        if (!ValidateMetadata(metadata))
        {
            return false;
        }

        var manifest = new GeneratedSunderPackageManifest(
            ManifestVersion: 1,
            Id: metadata.Id,
            Name: metadata.Name,
            Summary: string.IsNullOrWhiteSpace(metadata.Summary) ? null : metadata.Summary,
            Version: PackageVersion,
            EntryAssembly: EntryAssembly,
            Icon: string.IsNullOrWhiteSpace(metadata.Icon) ? null : NormalizePath(metadata.Icon),
            DependsOn: metadata.Dependencies.Count == 0 ? null : metadata.Dependencies,
            SdkApiVersion: ResolveSdkApiVersion(),
            SdkPackageVersion: ResolveSdkPackageVersion(),
            RequiredSdkCapabilities: metadata.RequiredSdkCapabilities.Count == 0 ? null : metadata.RequiredSdkCapabilities,
            SdkVersion: string.IsNullOrWhiteSpace(SdkVersion) ? null : SdkVersion,
            TargetFramework: string.IsNullOrWhiteSpace(TargetFramework) ? null : TargetFramework);

        var manifestDirectory = Path.GetDirectoryName(ManifestOutputPath);
        if (!string.IsNullOrWhiteSpace(manifestDirectory))
        {
            Directory.CreateDirectory(manifestDirectory);
        }

        File.WriteAllText(ManifestOutputPath, JsonSerializer.Serialize(manifest, JsonOptions) + Environment.NewLine);
        Log.LogMessage(MessageImportance.High, $"Generated Sunder package manifest at {ManifestOutputPath}");
        return !Log.HasLoggedErrors;
    }

    private SunderPackageMetadata? ReadPackageMetadata()
    {
        var assemblyDirectory = Path.GetDirectoryName(TargetAssemblyPath)!;
        var loadContext = new PackageMetadataLoadContext(assemblyDirectory);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(TargetAssemblyPath);
            var attributes = assembly.GetCustomAttributesData();
            var packageAttributes = attributes
                .Where(attribute => attribute.AttributeType.FullName == typeof(SunderPackageAttribute).FullName)
                .ToArray();

            if (packageAttributes.Length == 0)
            {
                Log.LogError($"Sunder package assembly '{TargetAssemblyPath}' must declare one SunderPackage attribute.");
                return null;
            }

            if (packageAttributes.Length > 1)
            {
                Log.LogError($"Sunder package assembly '{TargetAssemblyPath}' declares multiple SunderPackage attributes.");
                return null;
            }

            var packageAttribute = packageAttributes[0];
            var dependencies = attributes
                .Where(attribute => attribute.AttributeType.FullName == typeof(SunderPackageDependencyAttribute).FullName)
                .Select(attribute => new GeneratedSunderPackageDependency(
                    PackageId: GetNamedString(attribute, nameof(SunderPackageDependencyAttribute.PackageId)) ?? string.Empty,
                    VersionRange: GetNamedString(attribute, nameof(SunderPackageDependencyAttribute.VersionRange)) ?? string.Empty))
                .ToArray();

            return new SunderPackageMetadata(
                Id: GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Id)) ?? string.Empty,
                Name: GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Name)) ?? string.Empty,
                Summary: GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Summary)),
                Icon: GetNamedString(packageAttribute, nameof(SunderPackageAttribute.Icon)),
                Dependencies: dependencies,
                RequiredSdkCapabilities: InferRequiredSdkCapabilities(assembly));
        }
        catch (ReflectionTypeLoadException ex)
        {
            Log.LogError($"Failed to inspect Sunder package metadata in '{TargetAssemblyPath}': {ex.Message}");
            foreach (var loaderException in ex.LoaderExceptions.Where(static exception => exception is not null))
            {
                Log.LogError(loaderException!.Message);
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: false);
            return null;
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private int ResolveSdkApiVersion()
    {
        if (string.IsNullOrWhiteSpace(SdkApiVersion))
        {
            return SunderSdkApiVersions.Current;
        }

        if (int.TryParse(SdkApiVersion, out var apiVersion) && apiVersion > 0)
        {
            return apiVersion;
        }

        Log.LogError($"Sunder SDK API version '{SdkApiVersion}' must be a positive integer.");
        return SunderSdkApiVersions.Current;
    }

    private string? ResolveSdkPackageVersion()
    {
        if (!string.IsNullOrWhiteSpace(SdkPackageVersion))
        {
            return SdkPackageVersion;
        }

        return typeof(SunderPackageAttribute).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
    }

    private IReadOnlyList<string> InferRequiredSdkCapabilities(Assembly assembly)
    {
        var capabilities = new SortedSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SunderSdkCapabilities.CoreV1,
            SunderSdkCapabilities.PackagingV1,
            SunderSdkCapabilities.ContributionsV1,
        };
        var inspectedTypes = new HashSet<Type>();
        var inspectedMembers = new HashSet<MemberInfo>();

        foreach (var attribute in assembly.GetCustomAttributesData())
        {
            AddTypeCapabilities(attribute.AttributeType, capabilities, inspectedTypes);
        }

        foreach (var type in GetLoadableTypes(assembly))
        {
            try
            {
                InspectType(type, capabilities, inspectedTypes, inspectedMembers);
            }
            catch
            {
                // Keep manifest generation resilient when a package type depends on optional UI/runtime assemblies.
            }
        }

        AddMetadataReferenceCapabilities(assembly.Location, capabilities);

        foreach (var capability in SdkCapabilities.Select(static item => item.ItemSpec).Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            capabilities.Add(capability.Trim());
        }

        return capabilities.ToArray();
    }

    private static void AddMetadataReferenceCapabilities(string assemblyPath, ISet<string> capabilities)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
            {
                return;
            }

            var metadata = peReader.GetMetadataReader();
            foreach (var typeReferenceHandle in metadata.TypeReferences)
            {
                var typeReference = metadata.GetTypeReference(typeReferenceHandle);
                AddSdkTypeReferenceCapability(
                    metadata.GetString(typeReference.Namespace),
                    metadata.GetString(typeReference.Name),
                    capabilities);
            }

            foreach (var memberReferenceHandle in metadata.MemberReferences)
            {
                var memberReference = metadata.GetMemberReference(memberReferenceHandle);
                if (!IsContributionRegistryReference(metadata, memberReference.Parent))
                {
                    continue;
                }

                AddContributionRegistryMethodCapability(metadata.GetString(memberReference.Name), capabilities);
            }
        }
        catch
        {
            // Reflection-based inference still covers normal cases; metadata inference is best-effort hardening.
        }
    }

    private static void AddSdkTypeReferenceCapability(string @namespace, string name, ISet<string> capabilities)
    {
        if (!@namespace.StartsWith("Sunder.Sdk.", StringComparison.Ordinal))
        {
            return;
        }

        switch (@namespace, name)
        {
            case ("Sunder.Sdk.Abstractions", "ISunderPackageModule"):
            case ("Sunder.Sdk.Abstractions", "IPackageContext"):
                capabilities.Add(SunderSdkCapabilities.CoreV1);
                break;
            case ("Sunder.Sdk.Packaging", "SunderPackageAttribute"):
            case ("Sunder.Sdk.Packaging", "SunderPackageDependencyAttribute"):
                capabilities.Add(SunderSdkCapabilities.PackagingV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageContributionRegistry"):
                capabilities.Add(SunderSdkCapabilities.ContributionsV1);
                break;
            case ("Sunder.Sdk.Abstractions", "PackageViewRegistration"):
            case ("Sunder.Sdk.Abstractions", "PackageViewPlacement"):
                capabilities.Add(SunderSdkCapabilities.ViewsV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageWorkspaceFactory"):
                capabilities.Add(SunderSdkCapabilities.WorkspacesV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageBackgroundService"):
                capabilities.Add(SunderSdkCapabilities.BackgroundServicesV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageExtensionCatalog"):
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
            case ("Sunder.Sdk.Configuration", "PackageConfigurationSchema"):
            case ("Sunder.Sdk.Configuration", "PackageConfigurationSection"):
            case ("Sunder.Sdk.Configuration", "PackageConfigurationField"):
            case ("Sunder.Sdk.Configuration", "PackageConfigurationFieldKind"):
            case ("Sunder.Sdk.Configuration", "PackageConfigurationOption"):
                capabilities.Add(SunderSdkCapabilities.ConfigurationSchemaV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageConfiguration"):
                capabilities.Add(SunderSdkCapabilities.ConfigurationValuesV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageStorageContext"):
            case ("Sunder.Sdk.Abstractions", "IPackageFileStore"):
            case ("Sunder.Sdk.Abstractions", "IPackageKeyValueStore"):
                capabilities.Add(SunderSdkCapabilities.StorageV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageSecrets"):
                capabilities.Add(SunderSdkCapabilities.SecretsV1);
                break;
            case ("Sunder.Sdk.Logging", "IPackageLogging"):
            case ("Sunder.Sdk.Logging", "IPackageEventLogger"):
            case ("Sunder.Sdk.Logging", "PackageLogLevel"):
                capabilities.Add(SunderSdkCapabilities.LoggingV1);
                break;
            case ("Sunder.Sdk.Notifications", "IPackageNotificationService"):
            case ("Sunder.Sdk.Notifications", "PackageNotificationRequest"):
            case ("Sunder.Sdk.Notifications", "PackageNotificationSeverity"):
            case ("Sunder.Sdk.Notifications", "PackageNotificationDisplayMode"):
                capabilities.Add(SunderSdkCapabilities.NotificationsV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageShellViewService"):
            case ("Sunder.Sdk.Abstractions", "IPackageViewNavigationTarget"):
            case ("Sunder.Sdk.Abstractions", "PackageViewNavigationContext"):
            case ("Sunder.Sdk.Abstractions", "PackageHotbarView"):
            case ("Sunder.Sdk.Abstractions", "PackageHotbarPlacement"):
                capabilities.Add(SunderSdkCapabilities.ShellViewV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageCallbackHandler"):
            case ("Sunder.Sdk.Callbacks", _):
                capabilities.Add(SunderSdkCapabilities.CallbacksV1);
                break;
            case ("Sunder.Sdk.Abstractions", "IPackageAuthHandler"):
            case ("Sunder.Sdk.Authentication", _):
                capabilities.Add(SunderSdkCapabilities.AuthV1);
                break;
            case ("Sunder.Sdk.Theming", "SunderThemeKeys"):
                capabilities.Add(SunderSdkCapabilities.ThemingV1);
                break;
        }
    }

    private static bool IsContributionRegistryReference(MetadataReader metadata, EntityHandle parent)
    {
        if (parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }

        var typeReference = metadata.GetTypeReference((TypeReferenceHandle)parent);
        return metadata.GetString(typeReference.Namespace) == "Sunder.Sdk.Abstractions"
            && metadata.GetString(typeReference.Name) == "IPackageContributionRegistry";
    }

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(static type => type is not null)!;
        }
    }

    private static void InspectType(
        Type type,
        ISet<string> capabilities,
        ISet<Type> inspectedTypes,
        ISet<MemberInfo> inspectedMembers)
    {
        if (!inspectedTypes.Add(type))
        {
            return;
        }

        AddTypeCapabilities(type, capabilities, inspectedTypes);
        AddTypeCapabilities(type.BaseType, capabilities, inspectedTypes);
        foreach (var implementedInterface in type.GetInterfaces())
        {
            AddTypeCapabilities(implementedInterface, capabilities, inspectedTypes);
        }

        foreach (var attribute in type.GetCustomAttributesData())
        {
            AddTypeCapabilities(attribute.AttributeType, capabilities, inspectedTypes);
        }

        const BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var constructor in type.GetConstructors(bindingFlags))
        {
            InspectMethodBase(constructor, capabilities, inspectedTypes, inspectedMembers);
        }

        foreach (var method in type.GetMethods(bindingFlags))
        {
            InspectMethodBase(method, capabilities, inspectedTypes, inspectedMembers);
            AddTypeCapabilities(method.ReturnType, capabilities, inspectedTypes);
        }

        foreach (var property in type.GetProperties(bindingFlags))
        {
            InspectMember(property, capabilities, inspectedTypes, inspectedMembers);
            AddTypeCapabilities(property.PropertyType, capabilities, inspectedTypes);
        }

        foreach (var field in type.GetFields(bindingFlags))
        {
            InspectMember(field, capabilities, inspectedTypes, inspectedMembers);
            AddTypeCapabilities(field.FieldType, capabilities, inspectedTypes);
        }

        foreach (var eventInfo in type.GetEvents(bindingFlags))
        {
            InspectMember(eventInfo, capabilities, inspectedTypes, inspectedMembers);
            AddTypeCapabilities(eventInfo.EventHandlerType, capabilities, inspectedTypes);
        }

        foreach (var nestedType in type.GetNestedTypes(bindingFlags))
        {
            InspectType(nestedType, capabilities, inspectedTypes, inspectedMembers);
        }
    }

    private static void InspectMethodBase(
        MethodBase method,
        ISet<string> capabilities,
        ISet<Type> inspectedTypes,
        ISet<MemberInfo> inspectedMembers)
    {
        InspectMember(method, capabilities, inspectedTypes, inspectedMembers);
        foreach (var parameter in method.GetParameters())
        {
            AddTypeCapabilities(parameter.ParameterType, capabilities, inspectedTypes);
        }

        foreach (var genericArgument in method.IsGenericMethod ? method.GetGenericArguments() : [])
        {
            AddTypeCapabilities(genericArgument, capabilities, inspectedTypes);
        }

        InspectMethodBody(method, capabilities, inspectedTypes, inspectedMembers);
    }

    private static void InspectMember(
        MemberInfo member,
        ISet<string> capabilities,
        ISet<Type> inspectedTypes,
        ISet<MemberInfo> inspectedMembers)
    {
        if (!inspectedMembers.Add(member))
        {
            return;
        }

        var capabilityMember = member is MethodInfo { IsGenericMethod: true } methodInfo
            ? methodInfo.GetGenericMethodDefinition()
            : member;
        if (capabilityMember.Module.Assembly == typeof(SunderPackageAttribute).Assembly)
        {
            AddMemberCapabilities(capabilityMember, capabilities);
        }

        AddTypeCapabilities(capabilityMember.DeclaringType, capabilities, inspectedTypes);
    }

    private static void AddTypeCapabilities(Type? type, ISet<string> capabilities, ISet<Type> inspectedTypes)
    {
        if (type is null)
        {
            return;
        }

        if (type.HasElementType)
        {
            AddTypeCapabilities(type.GetElementType(), capabilities, inspectedTypes);
            return;
        }

        if (type.IsGenericParameter)
        {
            foreach (var constraint in type.GetGenericParameterConstraints())
            {
                AddTypeCapabilities(constraint, capabilities, inspectedTypes);
            }

            return;
        }

        var capabilitySourceType = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        if (capabilitySourceType.Assembly == typeof(SunderPackageAttribute).Assembly)
        {
            AddMemberCapabilities(capabilitySourceType, capabilities);
        }

        if (type.IsGenericType)
        {
            foreach (var genericArgument in type.GetGenericArguments())
            {
                AddTypeCapabilities(genericArgument, capabilities, inspectedTypes);
            }
        }
    }

    private static void AddMemberCapabilities(MemberInfo member, ISet<string> capabilities)
    {
        AddKnownSdkMemberCapabilities(member, capabilities);

        foreach (var attribute in member.GetCustomAttributesData())
        {
            if (attribute.AttributeType.FullName != typeof(SunderSdkCapabilityAttribute).FullName
                || attribute.ConstructorArguments.Count != 1
                || attribute.ConstructorArguments[0].Value is not string capability
                || string.IsNullOrWhiteSpace(capability))
            {
                continue;
            }

            capabilities.Add(capability);
        }
    }

    private static void AddKnownSdkMemberCapabilities(MemberInfo member, ISet<string> capabilities)
    {
        if (member.DeclaringType?.FullName != "Sunder.Sdk.Abstractions.IPackageContributionRegistry")
        {
            return;
        }

        AddContributionRegistryMethodCapability(member.Name, capabilities);
    }

    private static void AddContributionRegistryMethodCapability(string methodName, ISet<string> capabilities)
    {
        switch (methodName)
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
        }
    }

    private static void InspectMethodBody(
        MethodBase method,
        ISet<string> capabilities,
        ISet<Type> inspectedTypes,
        ISet<MemberInfo> inspectedMembers)
    {
        MethodBody? body;
        try
        {
            body = method.GetMethodBody();
        }
        catch
        {
            return;
        }

        var il = body?.GetILAsByteArray();
        if (il is null || il.Length == 0)
        {
            return;
        }

        var module = method.Module;
        var typeArguments = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : Type.EmptyTypes;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
        for (var index = 0; index < il.Length;)
        {
            var opCode = ReadOpCode(il, ref index);
            if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                if (index + 4 > il.Length)
                {
                    break;
                }

                var token = BitConverter.ToInt32(il, index);
                index += 4;
                InspectResolvedToken(module, token, typeArguments, methodArguments, capabilities, inspectedTypes, inspectedMembers);
                continue;
            }

            index += OperandSize(opCode.OperandType, il, index);
        }
    }

    private static OpCode ReadOpCode(byte[] il, ref int index)
    {
        var code = il[index++];
        if (code != 0xfe)
        {
            return SingleByteOpCodes[code];
        }

        return index < il.Length ? MultiByteOpCodes[il[index++]] : default;
    }

    private static void InspectResolvedToken(
        Module module,
        int token,
        Type[] typeArguments,
        Type[] methodArguments,
        ISet<string> capabilities,
        ISet<Type> inspectedTypes,
        ISet<MemberInfo> inspectedMembers)
    {
        MemberInfo? member;
        try
        {
            member = module.ResolveMember(token);
        }
        catch
        {
            try
            {
                member = module.ResolveMember(token, typeArguments, methodArguments);
            }
            catch
            {
                return;
            }
        }

        if (member is null)
        {
            return;
        }

        switch (member)
        {
            case Type referencedType:
                AddTypeCapabilities(referencedType, capabilities, inspectedTypes);
                break;
            case MethodInfo methodInfo:
                InspectMember(methodInfo, capabilities, inspectedTypes, inspectedMembers);
                AddTypeCapabilities(methodInfo.ReturnType, capabilities, inspectedTypes);
                foreach (var parameter in methodInfo.GetParameters())
                {
                    AddTypeCapabilities(parameter.ParameterType, capabilities, inspectedTypes);
                }
                foreach (var genericArgument in methodInfo.IsGenericMethod ? methodInfo.GetGenericArguments() : [])
                {
                    AddTypeCapabilities(genericArgument, capabilities, inspectedTypes);
                }
                break;
            case ConstructorInfo constructorInfo:
                InspectMember(constructorInfo, capabilities, inspectedTypes, inspectedMembers);
                foreach (var parameter in constructorInfo.GetParameters())
                {
                    AddTypeCapabilities(parameter.ParameterType, capabilities, inspectedTypes);
                }
                break;
            case FieldInfo fieldInfo:
                InspectMember(fieldInfo, capabilities, inspectedTypes, inspectedMembers);
                AddTypeCapabilities(fieldInfo.FieldType, capabilities, inspectedTypes);
                break;
        }
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
            OperandType.InlineSwitch => index + 4 > il.Length ? 0 : 4 + (BitConverter.ToInt32(il, index) * 4),
            _ => 0,
        };

    private bool ValidateMetadata(SunderPackageMetadata metadata)
    {
        ValidatePackageId(metadata.Id, "package id");

        if (string.IsNullOrWhiteSpace(metadata.Name))
        {
            Log.LogError("Sunder package metadata must include Name.");
        }

        if (string.IsNullOrWhiteSpace(PackageVersion) || !SemVerRegex.IsMatch(PackageVersion))
        {
            Log.LogError($"Sunder package version '{PackageVersion}' must be SemVer-compatible.");
        }

        if (string.IsNullOrWhiteSpace(EntryAssembly))
        {
            Log.LogError("Sunder package entry assembly name is required.");
        }

        if (!string.IsNullOrWhiteSpace(metadata.Icon))
        {
            ValidateRelativePath(metadata.Icon, "package icon");
            if (!PackageAssetExists(metadata.Icon))
            {
                Log.LogError($"Sunder package icon '{metadata.Icon}' does not exist under '{ProjectDirectory}'.");
            }
        }

        var seenDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in metadata.Dependencies)
        {
            ValidatePackageId(dependency.PackageId, "dependency package id");
            if (!seenDependencies.Add(dependency.PackageId))
            {
                Log.LogError($"Sunder package dependency '{dependency.PackageId}' is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(dependency.VersionRange))
            {
                Log.LogError($"Sunder package dependency '{dependency.PackageId}' must include VersionRange.");
            }
        }

        return !Log.HasLoggedErrors;
    }

    private void ValidatePackageId(string packageId, string label)
    {
        if (string.IsNullOrWhiteSpace(packageId) || !PackageIdRegex.IsMatch(packageId))
        {
            Log.LogError($"Sunder {label} '{packageId}' must use lowercase dot-separated ASCII identifiers.");
        }
    }

    private void ValidateRelativePath(string path, string label)
    {
        if (Path.IsPathRooted(path))
        {
            Log.LogError($"Sunder {label} path '{path}' must be relative.");
            return;
        }

        var normalizedSegments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (normalizedSegments.Any(static segment => segment == ".."))
        {
            Log.LogError($"Sunder {label} path '{path}' must not contain parent directory traversal.");
        }
    }

    private bool PackageAssetExists(string assetPath)
    {
        var normalizedPath = assetPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (File.Exists(Path.Combine(ProjectDirectory, normalizedPath)))
        {
            return true;
        }

        const string assetsPrefix = "assets/";
        var forwardPath = assetPath.Replace('\\', '/');
        if (!forwardPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sourceAssetPath = Path.Combine(
            ProjectDirectory,
            "Assets",
            forwardPath[assetsPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(sourceAssetPath);
    }

    private static string? GetNamedString(CustomAttributeData attribute, string name)
        => attribute.NamedArguments.FirstOrDefault(argument => argument.MemberName == name).TypedValue.Value as string;

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private sealed record SunderPackageMetadata(
        string Id,
        string Name,
        string? Summary,
        string? Icon,
        IReadOnlyList<GeneratedSunderPackageDependency> Dependencies,
        IReadOnlyList<string> RequiredSdkCapabilities);

    private sealed record GeneratedSunderPackageManifest(
        int ManifestVersion,
        string Id,
        string Name,
        string? Summary,
        string Version,
        string EntryAssembly,
        string? Icon,
        IReadOnlyList<GeneratedSunderPackageDependency>? DependsOn,
        int SdkApiVersion,
        string? SdkPackageVersion,
        IReadOnlyList<string>? RequiredSdkCapabilities,
        string? SdkVersion,
        string? TargetFramework);

    private sealed record GeneratedSunderPackageDependency(string PackageId, string VersionRange);

    private sealed class PackageMetadataLoadContext(string assemblyDirectory) : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null)
            {
                return null;
            }

            if (IsSdkSharedAssembly(assemblyName.Name))
            {
                var sharedAssembly = ResolveSdkSharedAssembly(assemblyName);
                if (sharedAssembly is not null)
                {
                    return sharedAssembly;
                }
            }

            var candidatePath = Path.Combine(assemblyDirectory, assemblyName.Name + ".dll");
            if (File.Exists(candidatePath))
            {
                return LoadFromAssemblyPath(candidatePath);
            }

            var taskDirectory = Path.GetDirectoryName(typeof(GenerateSunderPackageManifestTask).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(taskDirectory))
            {
                candidatePath = Path.Combine(taskDirectory, assemblyName.Name + ".dll");
                if (File.Exists(candidatePath))
                {
                    return LoadFromAssemblyPath(candidatePath);
                }
            }

            return null;
        }

        private static bool IsSdkSharedAssembly(string assemblyName)
            => assemblyName.Equals(typeof(SunderPackageAttribute).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("Microsoft.Extensions.DependencyInjection.Abstractions", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("Microsoft.Extensions.DependencyInjection", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("Microsoft.Extensions.Logging.Abstractions", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("Microsoft.Extensions.Logging", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("Avalonia", StringComparison.OrdinalIgnoreCase)
               || assemblyName.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase)
               || assemblyName.Equals("MicroCom.Runtime", StringComparison.OrdinalIgnoreCase);

        private static Assembly? ResolveSdkSharedAssembly(AssemblyName assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (AssemblyName.ReferenceMatchesDefinition(assemblyName, assembly.GetName()))
                {
                    return assembly;
                }
            }

            var taskDirectory = Path.GetDirectoryName(typeof(GenerateSunderPackageManifestTask).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(taskDirectory))
            {
                var candidatePath = Path.Combine(taskDirectory, assemblyName.Name + ".dll");
                if (File.Exists(candidatePath))
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidatePath);
                }
            }

            try
            {
                return Assembly.Load(assemblyName);
            }
            catch
            {
                return null;
            }
        }
    }
}
