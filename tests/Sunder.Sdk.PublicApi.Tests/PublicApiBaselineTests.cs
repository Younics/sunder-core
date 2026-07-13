using System.Reflection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Stacks;
using Sunder.Runtime.Contracts;
using Sunder.Registry.Contracts;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class PublicApiBaselineTests
{
    public static TheoryData<Assembly> SdkAssemblies => new()
    {
        typeof(IPackageContext).Assembly,
        typeof(IAvaloniaPackageContributionRegistry).Assembly,
        typeof(IPackageStackContributor).Assembly,
        typeof(RuntimeHandshakeResponse).Assembly,
        typeof(RegistryPackageArtifact).Assembly,
    };

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
                .OrderBy(static type => type.FullName, StringComparer.Ordinal)
                .SelectMany(FormatType)
                .ToArray();
            return string.Join('\n', lines) + "\n";
        }

        private static IEnumerable<string> FormatType(Type type)
        {
            yield return $"type {TypeKind(type)} {FormatTypeName(type)}{FormatInheritance(type)}";

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var constructor in type.GetConstructors(flags).OrderBy(FormatMethod, StringComparer.Ordinal))
            {
                yield return "  " + FormatMethod(constructor);
            }

            foreach (var field in type.GetFields(flags).OrderBy(static field => field.Name, StringComparer.Ordinal))
            {
                var value = field.IsLiteral ? $" = {field.GetRawConstantValue() ?? "null"}" : string.Empty;
                yield return $"  field {FormatTypeName(field.FieldType, Nullability.Create(field).ReadState)} {field.Name}{value}";
            }

            foreach (var property in type.GetProperties(flags).OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                var accessors = $"{{ {(property.GetMethod is null ? string.Empty : "get; ")}{(property.SetMethod is null ? string.Empty : "set; ")}}}";
                var index = property.GetIndexParameters();
                var name = index.Length == 0 ? property.Name : $"this[{string.Join(", ", index.Select(FormatParameter))}]";
                yield return $"  property {FormatTypeName(property.PropertyType, Nullability.Create(property).ReadState)} {name} {accessors}";
            }

            foreach (var eventInfo in type.GetEvents(flags).OrderBy(static eventInfo => eventInfo.Name, StringComparer.Ordinal))
            {
                yield return $"  event {FormatTypeName(eventInfo.EventHandlerType!)} {eventInfo.Name}";
            }

            foreach (var method in type.GetMethods(flags).Where(static method => !method.IsSpecialName).OrderBy(FormatMethod, StringComparer.Ordinal))
            {
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
                ? FormatTypeName(methodInfo.ReturnType, Nullability.Create(methodInfo.ReturnParameter).ReadState) + " "
                : string.Empty;
            return $"method {returnType}{name}({string.Join(", ", method.GetParameters().Select(FormatParameter))})";
        }

        private static string FormatParameter(ParameterInfo parameter)
        {
            var prefix = parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty;
            var parameterType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
            var defaultValue = parameter.HasDefaultValue ? $" = {FormatValue(parameter.DefaultValue)}" : string.Empty;
            return $"{prefix}{FormatTypeName(parameterType, Nullability.Create(parameter).ReadState)} {parameter.Name}{defaultValue}";
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
