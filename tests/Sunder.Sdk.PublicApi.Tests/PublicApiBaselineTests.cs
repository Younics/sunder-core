using System.ComponentModel;
using System.Reflection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class PublicApiBaselineTests
{
    public static TheoryData<Assembly> SdkAssemblies => new()
    {
        typeof(IPackageContext).Assembly,
        typeof(IAvaloniaPackageContributionRegistry).Assembly,
        typeof(IPackageStackExporter).Assembly,
        typeof(RuntimeHandshakeResponse).Assembly,
        typeof(RegistryPackageArtifact).Assembly,
        typeof(SunderPackageManifest).Assembly,
    };

    public static TheoryData<Assembly> SdkContractAssemblies => new()
    {
        typeof(IPackageContext).Assembly,
        typeof(IAvaloniaPackageContributionRegistry).Assembly,
        typeof(IPackageStackExporter).Assembly,
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
    public void ExportedApiMatchesShipped11Baseline(Assembly assembly)
    {
        var fileName = assembly.GetName().Name + ".txt";
        var actual = PublicApiFormatter.Format(assembly);
        var updateRoot = Environment.GetEnvironmentVariable("SUNDER_UPDATE_PUBLIC_API_ROOT");
        if (!string.IsNullOrWhiteSpace(updateRoot))
        {
            var outputPath = Path.Combine(updateRoot, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, actual);
            return;
        }

        var baselinePath = Path.Combine(AppContext.BaseDirectory, "PublicApi", fileName);
        Assert.True(File.Exists(baselinePath), $"Public API baseline is missing: {baselinePath}");
        Assert.Equal(File.ReadAllText(baselinePath).Replace("\r\n", "\n"), actual);
    }

    private static class PublicApiFormatter
    {
        private static readonly NullabilityInfoContext Nullability = new();

        public static string Format(Assembly assembly)
        {
            var lines = assembly.GetExportedTypes()
                .Where(IsSupportedPublicApiType)
                .OrderBy(static type => type.FullName, StringComparer.Ordinal)
                .SelectMany(FormatType)
                .ToArray();
            return string.Join('\n', lines) + "\n";
        }

        private static bool IsSupportedPublicApiType(Type type)
            => type.Namespace != "CompiledAvaloniaXaml"
               || type.GetCustomAttribute<EditorBrowsableAttribute>()?.State != EditorBrowsableState.Never;

        private static IEnumerable<string> FormatType(Type type)
        {
            foreach (var attribute in FormatApiAttributes(type, string.Empty))
            {
                yield return attribute;
            }
            yield return $"type {TypeKind(type)} {FormatTypeName(type)}{FormatInheritance(type)}{FormatGenericConstraints(type.GetGenericArguments())}";

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var constructor in type.GetConstructors(flags).OrderBy(FormatMethod, StringComparer.Ordinal))
            {
                foreach (var attribute in FormatApiAttributes(constructor, "  ")) yield return attribute;
                yield return "  " + FormatMethod(constructor);
            }

            foreach (var field in type.GetFields(flags).OrderBy(static field => field.Name, StringComparer.Ordinal))
            {
                foreach (var attribute in FormatApiAttributes(field, "  ")) yield return attribute;
                var value = field.IsLiteral ? $" = {FormatValue(field.GetRawConstantValue())}" : string.Empty;
                var modifiers = field.IsStatic ? "static " : string.Empty;
                modifiers += field.IsInitOnly ? "readonly " : string.Empty;
                yield return $"  field {modifiers}{FormatTypeName(field.FieldType, Nullability.Create(field))} {field.Name}{value}";
            }

            foreach (var property in type.GetProperties(flags).OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                foreach (var attribute in FormatApiAttributes(property, "  ")) yield return attribute;
                var setter = property.SetMethod is null
                    ? string.Empty
                    : property.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit))
                        ? "init; "
                        : "set; ";
                var accessors = $"{{ {(property.GetMethod is null ? string.Empty : "get; ")}{setter}}}";
                var index = property.GetIndexParameters();
                var name = index.Length == 0 ? property.Name : $"this[{string.Join(", ", index.Select(FormatParameter))}]";
                var modifiers = property.GetMethod?.IsStatic == true || property.SetMethod?.IsStatic == true ? "static " : string.Empty;
                yield return $"  property {modifiers}{FormatTypeName(property.PropertyType, Nullability.Create(property))} {name} {accessors}";
            }

            foreach (var eventInfo in type.GetEvents(flags).OrderBy(static eventInfo => eventInfo.Name, StringComparer.Ordinal))
            {
                foreach (var attribute in FormatApiAttributes(eventInfo, "  ")) yield return attribute;
                var modifiers = eventInfo.AddMethod?.IsStatic == true ? "static " : string.Empty;
                yield return $"  event {modifiers}{FormatTypeName(eventInfo.EventHandlerType!)} {eventInfo.Name}";
            }

            foreach (var method in type.GetMethods(flags).Where(static method => !method.IsSpecialName).OrderBy(FormatMethod, StringComparer.Ordinal))
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
            var modifiers = method.IsStatic ? "static " : method.IsAbstract ? "abstract " : method.IsVirtual ? "virtual " : string.Empty;
            return $"method {modifiers}{returnType}{name}({string.Join(", ", method.GetParameters().Select(FormatParameter))}){constraints}";
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

        private static IEnumerable<string> FormatApiAttributes(MemberInfo member, string indent)
        {
            var obsolete = member.GetCustomAttribute<ObsoleteAttribute>();
            if (obsolete is not null)
            {
                yield return $"{indent}attribute System.ObsoleteAttribute({FormatValue(obsolete.Message)}, IsError = {FormatValue(obsolete.IsError)})";
            }

            var editorBrowsable = member.GetCustomAttribute<System.ComponentModel.EditorBrowsableAttribute>();
            if (editorBrowsable is not null)
            {
                yield return $"{indent}attribute System.ComponentModel.EditorBrowsableAttribute({editorBrowsable.State})";
            }

            foreach (var capability in member.GetCustomAttributesData()
                         .Where(static attribute => attribute.AttributeType.FullName == "Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute")
                         .Select(static attribute => attribute.ConstructorArguments[0].Value as string)
                         .Where(static capability => capability is not null)
                         .Order(StringComparer.Ordinal))
            {
                yield return $"{indent}attribute Sunder.Sdk.Compatibility.SunderSdkCapabilityAttribute({FormatValue(capability)})";
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
            if (type.BaseType is not null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType) && type.BaseType != typeof(Enum))
            {
                inherited.Add(FormatTypeName(type.BaseType));
            }

            inherited.AddRange(type.GetInterfaces().Select(static interfaceType => FormatTypeName(interfaceType)).Order(StringComparer.Ordinal));
            return inherited.Count == 0 ? string.Empty : " : " + string.Join(", ", inherited.Distinct(StringComparer.Ordinal));
        }

        private static string FormatTypeName(Type type, NullabilityState nullability = NullabilityState.Unknown)
        {
            if (type.IsArray)
            {
                return FormatTypeName(type.GetElementType()!) + "[]" + NullableSuffix(type, nullability);
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
                name = name[..name.IndexOf('`')]
                    + "<" + string.Join(", ", type.GetGenericArguments().Select(static argument => FormatTypeName(argument))) + ">";
            }

            return name.Replace('+', '.') + NullableSuffix(type, nullability);
        }

        private static string FormatTypeName(Type type, NullabilityInfo nullability)
        {
            if (type.IsArray)
            {
                var element = nullability.ElementType is null
                    ? FormatTypeName(type.GetElementType()!)
                    : FormatTypeName(type.GetElementType()!, nullability.ElementType);
                return element + "[]" + NullableSuffix(type, nullability.ReadState);
            }

            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null)
            {
                return FormatTypeName(nullable) + "?";
            }

            if (!type.IsGenericType)
            {
                return FormatTypeName(type, nullability.ReadState);
            }

            var name = (type.FullName ?? type.Name);
            name = name[..name.IndexOf('`')];
            var arguments = type.GetGenericArguments();
            var formattedArguments = arguments.Select((argument, index) =>
                index < nullability.GenericTypeArguments.Length
                    ? FormatTypeName(argument, nullability.GenericTypeArguments[index])
                    : FormatTypeName(argument));
            return name.Replace('+', '.')
                   + "<" + string.Join(", ", formattedArguments) + ">"
                   + NullableSuffix(type, nullability.ReadState);
        }

        private static string NullableSuffix(Type type, NullabilityState state)
            => !type.IsValueType && state == NullabilityState.Nullable ? "?" : string.Empty;

        private static string FormatValue(object? value)
            => value switch
            {
                null => "null",
                string text => $"\"{text}\"",
                bool flag => flag ? "true" : "false",
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            };

        private static string TypeKind(Type type)
            => type.IsInterface ? "interface" : type.IsEnum ? "enum" : type.IsValueType ? "struct" : type.IsSealed ? "sealed class" : "class";
    }
}
