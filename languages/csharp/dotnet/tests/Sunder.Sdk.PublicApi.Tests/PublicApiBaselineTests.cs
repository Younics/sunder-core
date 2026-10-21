using System.ComponentModel;
using System.Reflection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Sunder.Host.Contracts;
using Sunder.Package.Build.Tasks;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class PublicApiBaselineTests
{
    public static TheoryData<Type> SdkAssemblies => new()
    {
        typeof(IPackageContext),
        typeof(IAvaloniaPackageContributionRegistry),
        typeof(IPackageStackExporter),
        typeof(SunderWorker),
        typeof(HostHandshakeResponse),
        typeof(GenerateSunderPackageManifestTask),
        typeof(RuntimeHandshakeResponse),
        typeof(RegistryPackageCanonicalArtifact),
        typeof(SunderPackageManifest),
    };

    public static TheoryData<Assembly> SdkContractAssemblies => new()
    {
        typeof(IPackageContext).Assembly,
        typeof(IAvaloniaPackageContributionRegistry).Assembly,
        typeof(IPackageStackExporter).Assembly,
        typeof(SunderWorker).Assembly,
    };

    [Theory]
    [MemberData(nameof(SdkContractAssemblies))]
    public void Sdk11ContractAssembliesUseStableBinaryVersion(Assembly assembly)
        => Assert.Equal(new Version(1, 1, 0, 0), assembly.GetName().Version);

    [Fact]
    public void AvaloniaGeneratedImplementationTypesAreNotSupportedApi()
    {
        var generatedTypes = typeof(IAvaloniaPackageContributionRegistry).Assembly
            .GetExportedTypes()
            .Where(static type => type.Namespace == "CompiledAvaloniaXaml");

        Assert.DoesNotContain(generatedTypes, static type =>
            type.GetCustomAttribute<EditorBrowsableAttribute>()?.State != EditorBrowsableState.Never);
    }

    [Theory]
    [MemberData(nameof(SdkAssemblies))]
    public void ExportedApiMatchesCurrentCandidateSnapshot(Type assemblyMarker)
    {
        var assembly = assemblyMarker.Assembly;
        var fileName = assembly.GetName().Name + ".txt";
        var actual = PublicApiFormatter.Format(assembly);
        var updateRoot = PublicApiSnapshotPolicy.GetUpdateRoot("SUNDER_UPDATE_PUBLIC_API_ROOT");
        if (!string.IsNullOrWhiteSpace(updateRoot))
        {
            var selectedAssemblies = (Environment.GetEnvironmentVariable("SUNDER_UPDATE_PUBLIC_API_ASSEMBLIES") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (selectedAssemblies.Length == 0
                || selectedAssemblies.Contains(assembly.GetName().Name, StringComparer.Ordinal))
            {
                var outputPath = Path.Combine(updateRoot, fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, actual);
                return;
            }
        }

        var baselinePath = Path.Combine(AppContext.BaseDirectory, "PublicApi", fileName);
        Assert.True(File.Exists(baselinePath), $"Public API baseline is missing: {baselinePath}");
        Assert.Equal(File.ReadAllText(baselinePath).Replace("\r\n", "\n"), actual);
    }

    // The v2 text format is consumed by scripts/compatibility/public-api-comparison.mjs.
    // Keep released v2 snapshots immutable; a future formatter needs an explicit migration.
    internal static class PublicApiFormatter
    {
        [ThreadStatic]
        private static NullabilityInfoContext? nullability;
        private static NullabilityInfoContext Nullability => nullability ??= new();

        public static string Format(Assembly assembly)
        {
            var lines = assembly.GetTypes()
                .Where(IsSupportedPublicApiType)
                .OrderBy(static type => type.FullName, StringComparer.Ordinal)
                .SelectMany(FormatType)
                .ToArray();
            return "# sunder-public-api-v2\nassembly " + assembly.GetName().FullName + "\n"
                + string.Join('\n', lines) + "\n";
        }

        private static bool IsSupportedPublicApiType(Type type)
            => (type.IsPublic || (type.DeclaringType is not null
                    && (type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamORAssem)
                    && IsSupportedPublicApiType(type.DeclaringType)))
               && (type.Namespace != "CompiledAvaloniaXaml"
                   || type.GetCustomAttribute<EditorBrowsableAttribute>()?.State != EditorBrowsableState.Never);

        private static IEnumerable<string> FormatType(Type type)
        {
            foreach (var attribute in FormatApiAttributes(type, string.Empty))
            {
                yield return attribute;
            }
            var visibility = type.IsNestedFamily ? "protected" : type.IsNestedFamORAssem ? "protected internal" : "public";
            yield return $"type {visibility} {TypeKind(type)} {FormatDeclaredTypeName(type)}{FormatInheritance(type)}{FormatGenericConstraints(type.GetGenericArguments())}";

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var constructor in type.GetConstructors(flags).Where(IsApiMethod).OrderBy(FormatMethod, StringComparer.Ordinal))
            {
                foreach (var attribute in FormatApiAttributes(constructor, "  ")) yield return attribute;
                yield return "  " + FormatMethod(constructor);
            }

            foreach (var field in type.GetFields(flags)
                         .Where(static field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly)
                         .OrderBy(static field => field.Name, StringComparer.Ordinal))
            {
                foreach (var attribute in FormatApiAttributes(field, "  ")) yield return attribute;
                var fieldNullability = Nullability.Create(field);
                var value = field.IsLiteral ? $" = {FormatValue(field.GetRawConstantValue())}" : string.Empty;
                var modifiers = field.IsPublic ? "public " : field.IsFamily ? "protected " : "protected internal ";
                modifiers += field.IsLiteral ? "const " : field.IsStatic ? "static " : string.Empty;
                modifiers += field.IsInitOnly ? "readonly " : string.Empty;
                modifiers += IsRequired(field) ? "required " : string.Empty;
                var writeType = field.IsLiteral || field.IsInitOnly ? string.Empty : FormatWriteNullability(field.FieldType, fieldNullability);
                yield return $"  field {modifiers}{FormatTypeName(field.FieldType, fieldNullability)} {field.Name}{value}{writeType}";
            }

            foreach (var property in type.GetProperties(flags)
                         .Where(static property => IsApiMethod(property.GetMethod) || IsApiMethod(property.SetMethod))
                         .OrderBy(static property => property.ToString(), StringComparer.Ordinal))
            {
                foreach (var attribute in FormatApiAttributes(property, "  ")) yield return attribute;
                var setter = property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true
                    ? "init" : "set";
                foreach (var attribute in FormatApiAttributes(property.GetMethod, "  ", "get")) yield return attribute;
                foreach (var attribute in FormatApiAttributes(property.SetMethod, "  ", setter)) yield return attribute;
                var accessors = $"{{ {FormatAccessor(property.GetMethod, "get")}{FormatAccessor(property.SetMethod, setter)}}}";
                var index = property.GetIndexParameters();
                var name = index.Length == 0 ? property.Name : $"this[{string.Join(", ", index.Select(FormatParameter))}]";
                var modifiers = IsRequired(property) ? "required " : string.Empty;
                var propertyNullability = Nullability.Create(property);
                var writeType = property.SetMethod is null ? string.Empty : FormatWriteNullability(property.PropertyType, propertyNullability);
                yield return $"  property {modifiers}{FormatTypeName(property.PropertyType, propertyNullability)} {name} {accessors}{writeType}";
            }

            foreach (var eventInfo in type.GetEvents(flags)
                         .Where(static eventInfo => IsApiMethod(eventInfo.AddMethod) || IsApiMethod(eventInfo.RemoveMethod))
                         .OrderBy(static eventInfo => eventInfo.Name, StringComparer.Ordinal))
            {
                foreach (var attribute in FormatApiAttributes(eventInfo, "  ")) yield return attribute;
                foreach (var attribute in FormatApiAttributes(eventInfo.AddMethod, "  ", "add")) yield return attribute;
                foreach (var attribute in FormatApiAttributes(eventInfo.RemoveMethod, "  ", "remove")) yield return attribute;
                yield return $"  event {FormatTypeName(eventInfo.EventHandlerType!, Nullability.Create(eventInfo))} {eventInfo.Name} {{ {FormatAccessor(eventInfo.AddMethod, "add")}{FormatAccessor(eventInfo.RemoveMethod, "remove")}}}";
            }

            foreach (var method in type.GetMethods(flags)
                         .Where(static method => IsApiMethod(method) && (!method.IsSpecialName || method.Name.StartsWith("op_", StringComparison.Ordinal)))
                         .OrderBy(FormatMethod, StringComparer.Ordinal))
            {
                foreach (var attribute in FormatApiAttributes(method, "  ")) yield return attribute;
                yield return "  " + FormatMethod(method);
            }
        }

        private static string FormatMethod(MethodBase method)
        {
            var name = method is ConstructorInfo ? ".ctor" : method.Name;
            if (method.IsGenericMethodDefinition)
            {
                name += $"<{string.Join(", ", method.GetGenericArguments().Select(static argument => argument.Name))}>";
            }

            var returnType = method is MethodInfo methodInfo
                ? FormatTypeName(methodInfo.ReturnType, Nullability.Create(methodInfo.ReturnParameter)) + " "
                : string.Empty;
            var constraints = method.IsGenericMethodDefinition
                ? FormatGenericConstraints(method.GetGenericArguments())
                : string.Empty;
            var modifiers = FormatMethodModifiers(method);
            return $"method {modifiers}{returnType}{name}({string.Join(", ", method.GetParameters().Select(FormatParameter))}){constraints}";
        }

        private static bool IsApiMethod(MethodBase? method)
            => method is not null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);

        private static bool IsRequired(MemberInfo member)
            => member.IsDefined(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), inherit: false);

        private static string FormatAccessor(MethodInfo? method, string kind)
            => method is null ? string.Empty : FormatMethodModifiers(method) + kind + "; ";

        private static string FormatMethodModifiers(MethodBase method)
        {
            var result = method.IsPublic ? "public " : method.IsFamily ? "protected "
                : method.IsFamilyOrAssembly ? "protected internal " : method.IsFamilyAndAssembly ? "private protected "
                : method.IsAssembly ? "internal " : "private ";
            if (method.IsStatic) result += "static ";
            if (method.IsAbstract) result += "abstract ";
            if (method.IsVirtual)
            {
                if (method.IsFinal) result += "final ";
                // Static interface members omit NewSlot but do not override an inherited instance slot.
                result += method.IsStatic || (method.Attributes & MethodAttributes.NewSlot) != 0 ? "virtual " : "override ";
            }
            return result;
        }

        private static string FormatGenericConstraints(IReadOnlyList<Type> genericArguments)
        {
            var constraints = new List<string>();
            foreach (var argument in genericArguments.Where(static argument => argument.IsGenericParameter))
            {
                var values = new List<string>();
                var attributes = argument.GenericParameterAttributes;
                if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0) values.Add("class");
                if ((attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) values.Add("struct");
                values.AddRange(argument.GetGenericParameterConstraints()
                    .Where(static constraint => constraint != typeof(ValueType))
                    .Select(static constraint => FormatTypeName(constraint)));
                if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0
                    && (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0)
                {
                    values.Add("new()");
                }
                if (values.Count > 0)
                {
                    constraints.Add($" where {argument.Name} : {string.Join(", ", values)}");
                }
            }
            return string.Concat(constraints);
        }

        private static IEnumerable<string> FormatApiAttributes(MemberInfo? member, string indent, string? accessor = null)
        {
            if (member is null) yield break;
            // Keep accessor attributes attached to the property/event record, with their exact scope.
            var prefix = $"{indent}attribute " + (accessor is null ? string.Empty : accessor + " ");
            var obsolete = member.GetCustomAttribute<ObsoleteAttribute>();
            if (obsolete is not null)
            {
                yield return $"{prefix}System.ObsoleteAttribute({FormatValue(obsolete.Message)}, IsError = {FormatValue(obsolete.IsError)})";
            }

            var editorBrowsable = member.GetCustomAttribute<System.ComponentModel.EditorBrowsableAttribute>();
            if (editorBrowsable is not null)
            {
                yield return $"{prefix}System.ComponentModel.EditorBrowsableAttribute({editorBrowsable.State})";
            }

            foreach (var attribute in member.GetCustomAttributesData()
                         .Select(static attribute => attribute.AttributeType.FullName)
                         .Where(static name => name is "Microsoft.Build.Framework.RequiredAttribute"
                             or "Microsoft.Build.Framework.OutputAttribute"
                             or "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"
                             or "System.Runtime.CompilerServices.IsReadOnlyAttribute"
                             or "System.Runtime.CompilerServices.IsByRefLikeAttribute")
                         .Order(StringComparer.Ordinal))
            {
                yield return $"{prefix}{attribute}";
            }

            foreach (var capability in member.GetCustomAttributesData()
                         .Where(static attribute => attribute.AttributeType.FullName == "Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute")
                         .Select(static attribute => attribute.ConstructorArguments[0].Value as string)
                         .Where(static capability => capability is not null)
                         .Order(StringComparer.Ordinal))
            {
                yield return $"{prefix}Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute({FormatValue(capability)})";
            }
        }

        private static string FormatParameter(ParameterInfo parameter)
        {
            var prefix = parameter.GetCustomAttribute<ParamArrayAttribute>() is not null
                ? "params "
                : parameter.IsOut
                    ? "out "
                    : parameter.IsIn && parameter.ParameterType.IsByRef
                        ? "in "
                        : parameter.ParameterType.IsByRef ? "ref " : string.Empty;
            var parameterType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
            var defaultValue = parameter.HasDefaultValue ? $" = {FormatValue(parameter.DefaultValue)}" : string.Empty;
            return $"{prefix}{FormatTypeName(parameterType, Nullability.Create(parameter))} {parameter.Name}{defaultValue}";
        }

        private static string FormatInheritance(Type type)
        {
            var inherited = new List<string>();
            if (type.IsEnum) inherited.Add(FormatTypeName(Enum.GetUnderlyingType(type)));
            if (type.BaseType is not null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType) && type.BaseType != typeof(Enum))
            {
                inherited.Add(FormatTypeName(type.BaseType));
            }

            inherited.AddRange(type.GetInterfaces().Select(static interfaceType => FormatTypeName(interfaceType)).Order(StringComparer.Ordinal));
            return inherited.Count == 0 ? string.Empty : " : " + string.Join(", ", inherited.Distinct(StringComparer.Ordinal));
        }

        private static string FormatTypeName(Type type, NullabilityState nullability = NullabilityState.Unknown)
        {
            if (type.IsByRef) return "ref " + FormatTypeName(type.GetElementType()!);
            if (type.IsPointer) return FormatTypeName(type.GetElementType()!) + "*";
            if (type.IsArray)
            {
                return FormatTypeName(type.GetElementType()!) + ArraySuffix(type) + NullableSuffix(type, nullability);
            }

            if (type.IsGenericParameter)
            {
                return type.Name + NullableSuffix(type, nullability);
            }

            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null)
            {
                return FormatTypeName(nullable) + "?";
            }

            var name = type.FullName ?? type.Name;
            if (type.IsGenericType)
            {
                name = FormatGenericTypeName(type, type.GetGenericArguments().Select(static argument => FormatTypeName(argument)).ToArray());
            }

            return name.Replace('+', '.') + NullableSuffix(type, nullability);
        }

        private static string FormatWriteNullability(Type type, NullabilityInfo nullability)
        {
            // The declaration's type is the read contract. Record an independent accepted-input
            // type when attributes such as AllowNull/DisallowNull make the write contract differ.
            var readType = FormatTypeName(type, nullability);
            var writeType = FormatTypeName(type, nullability, forWrite: true);
            return readType == writeType ? string.Empty : $" [write: {writeType}]";
        }

        private static string FormatTypeName(Type type, NullabilityInfo nullability, bool forWrite = false)
        {
            var state = forWrite ? nullability.WriteState : nullability.ReadState;
            // NullabilityInfo describes the element's generic arguments for by-ref types too.
            if (type.IsByRef) return "ref " + FormatTypeName(type.GetElementType()!, nullability, forWrite);
            if (type.IsPointer) return FormatTypeName(type.GetElementType()!, nullability, forWrite) + "*";
            if (type.IsArray)
            {
                var element = nullability.ElementType is null
                    ? FormatTypeName(type.GetElementType()!)
                    : FormatTypeName(type.GetElementType()!, nullability.ElementType, forWrite);
                return element + ArraySuffix(type) + NullableSuffix(type, state);
            }

            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null)
            {
                // For Nullable<Container<T...>>, NullabilityInfo.GenericTypeArguments describes
                // Container's arguments directly, not a separate Nullable<T> wrapper node.
                return FormatTypeName(nullable, nullability, forWrite) + "?";
            }

            if (!type.IsGenericType)
            {
                return FormatTypeName(type, state);
            }

            var arguments = type.GetGenericArguments();
            var formattedArguments = arguments.Select((argument, index) =>
                index < nullability.GenericTypeArguments.Length
                    ? FormatTypeName(argument, nullability.GenericTypeArguments[index], forWrite)
                    : FormatTypeName(argument));
            return FormatGenericTypeName(type, formattedArguments.ToArray())
                   + NullableSuffix(type, state);
        }

        private static string ArraySuffix(Type type) => "[" + new string(',', type.GetArrayRank() - 1) + "]";

        private static string FormatDeclaredTypeName(Type type)
            => !type.IsGenericType ? FormatTypeName(type) : FormatGenericTypeName(type,
                type.GetGenericArguments().Select(static argument =>
                    ((argument.GenericParameterAttributes & GenericParameterAttributes.VarianceMask) switch
                    {
                        GenericParameterAttributes.Covariant => "out ",
                        GenericParameterAttributes.Contravariant => "in ",
                        _ => string.Empty,
                    }) + argument.Name).ToArray());

        private static string FormatGenericTypeName(Type type, string[] arguments)
        {
            var index = 0;
            var definition = type.GetGenericTypeDefinition();
            return string.Join('.', (definition.FullName ?? definition.Name).Split('+').Select(part =>
            {
                var tick = part.IndexOf('`');
                if (tick < 0) return part;
                var count = int.Parse(part[(tick + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                var result = part[..tick] + "<" + string.Join(", ", arguments.Skip(index).Take(count)) + ">";
                index += count;
                return result;
            }));
        }

        private static string NullableSuffix(Type type, NullabilityState state)
            => !type.IsValueType && state == NullabilityState.Nullable ? "?" : string.Empty;

        private static string FormatValue(object? value)
            => value switch
            {
                null => "null",
                string text => System.Text.Json.JsonSerializer.Serialize(text),
                char character => "char(" + ((int)character).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
                bool flag => flag ? "true" : "false",
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            };

        private static string TypeKind(Type type)
            => type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct"
                : type.IsAbstract && type.IsSealed ? "static class" : type.IsAbstract ? "abstract class"
                : type.IsSealed ? "sealed class" : "class";
    }
}
