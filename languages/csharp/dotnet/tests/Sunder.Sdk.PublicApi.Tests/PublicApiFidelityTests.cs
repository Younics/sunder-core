using System.Reflection;
using Sunder.Package.Build.Tasks;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class PublicApiFidelityTests
{
    [Fact]
    public void PackageBuildTaskPropertiesMatchAccessorAndFunctionalAttributeSnapshot()
    {
        var actual = FormatAssembly(typeof(GenerateSunderPackageManifestTask).Assembly);
        var updateRoot = PublicApiSnapshotPolicy.GetUpdateRoot("SUNDER_UPDATE_PUBLIC_API_FIDELITY_ROOT");
        if (!string.IsNullOrWhiteSpace(updateRoot))
        {
            Directory.CreateDirectory(updateRoot);
            File.WriteAllText(Path.Combine(updateRoot, "Sunder.Package.Build.txt"), actual);
            return;
        }

        var snapshotPath = Path.Combine(AppContext.BaseDirectory, "ApiFidelity", "Sunder.Package.Build.txt");
        Assert.True(File.Exists(snapshotPath), $"Public API fidelity snapshot is missing: {snapshotPath}");
        Assert.Equal(File.ReadAllText(snapshotPath).Replace("\r\n", "\n"), actual);
    }

    [Fact]
    public void FidelityFormatterDetectsAccessorVisibilityAndFunctionalAttributeMutations()
    {
        var publicSetter = FormatProperties(typeof(PublicSetterFixture));
        var privateSetter = FormatProperties(typeof(PrivateSetterFixture));
        Assert.Contains("{ public get; public set; }", publicSetter, StringComparison.Ordinal);
        Assert.Contains("{ public get; private set; }", privateSetter, StringComparison.Ordinal);
        Assert.NotEqual(NormalizeFixtureName(publicSetter), NormalizeFixtureName(privateSetter));

        var taskProperties = FormatAssembly(typeof(GenerateSunderPackageManifestTask).Assembly);
        Assert.Contains("Microsoft.Build.Framework.RequiredAttribute", taskProperties, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Build.Framework.OutputAttribute", taskProperties, StringComparison.Ordinal);
        var seededAttributeMutation = ReplaceFirst(
            taskProperties,
            "Microsoft.Build.Framework.RequiredAttribute",
            "Microsoft.Build.Framework.OutputAttribute");
        Assert.NotEqual(taskProperties, seededAttributeMutation);
    }

    private static string FormatAssembly(Assembly assembly)
        => string.Concat(assembly.GetExportedTypes()
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .Select(FormatProperties));

    private static string FormatProperties(Type type)
    {
        const BindingFlags flags = BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.Instance
                                   | BindingFlags.Static
                                   | BindingFlags.DeclaredOnly;
        var properties = type.GetProperties(flags)
            .Where(static property => IsApiAccessor(property.GetMethod) || IsApiAccessor(property.SetMethod))
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .Select(property =>
            {
                var accessors = FormatAccessor(property.GetMethod, "get")
                                + FormatAccessor(property.SetMethod, IsInitOnly(property) ? "init" : "set");
                return $"  property {FormatTypeName(property.PropertyType)} {property.Name} "
                       + $"{{ {accessors.TrimEnd()} }}"
                       + FormatFunctionalAttributes(property);
            });
        return $"type {type.FullName!.Replace('+', '.')}\n{string.Join('\n', properties)}\n";
    }

    private static bool IsApiAccessor(MethodInfo? method)
        => method is not null && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);

    private static string FormatAccessor(MethodInfo? method, string kind)
        => method is null ? string.Empty : $"{Visibility(method)} {kind}; ";

    private static string Visibility(MethodBase method)
        => method.IsPublic ? "public"
            : method.IsFamily ? "protected"
            : method.IsFamilyOrAssembly ? "protected internal"
            : method.IsFamilyAndAssembly ? "private protected"
            : method.IsAssembly ? "internal"
            : "private";

    private static bool IsInitOnly(PropertyInfo property)
        => property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers()
            .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true;

    private static string FormatFunctionalAttributes(PropertyInfo property)
    {
        var attributes = property.GetCustomAttributesData()
            .Select(static attribute => attribute.AttributeType.FullName)
            .Where(static name => name is "Microsoft.Build.Framework.OutputAttribute"
                or "Microsoft.Build.Framework.RequiredAttribute")
            .Order(StringComparer.Ordinal)
            .ToArray();
        return attributes.Length == 0 ? string.Empty : $" [{string.Join(", ", attributes)}]";
    }

    private static string FormatTypeName(Type type)
    {
        if (type.IsArray) return FormatTypeName(type.GetElementType()!) + "[]";
        if (!type.IsGenericType) return (type.FullName ?? type.Name).Replace('+', '.');
        var name = (type.FullName ?? type.Name);
        name = name[..name.IndexOf('`')];
        return name.Replace('+', '.')
               + "<" + string.Join(", ", type.GetGenericArguments().Select(FormatTypeName)) + ">";
    }

    private static string NormalizeFixtureName(string value)
        => value.Replace(nameof(PublicSetterFixture), "Fixture", StringComparison.Ordinal)
            .Replace(nameof(PrivateSetterFixture), "Fixture", StringComparison.Ordinal);

    private static string ReplaceFirst(string value, string oldValue, string newValue)
    {
        var index = value.IndexOf(oldValue, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Snapshot did not contain '{oldValue}'.");
        return string.Concat(value.AsSpan(0, index), newValue, value.AsSpan(index + oldValue.Length));
    }

    private sealed class PublicSetterFixture
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class PrivateSetterFixture
    {
        public string Value { get; private set; } = string.Empty;
    }

}
