using System.Reflection;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal static class AppPackageModuleResolver
{
    public static Type? Resolve(Assembly entryAssembly, out string? error)
    {
        var moduleTypes = entryAssembly.GetTypes()
            .Where(static type => type is { IsClass: true, IsAbstract: false, IsPublic: true }
                && typeof(ISunderPackageModule).IsAssignableFrom(type))
            .ToArray();

        if (moduleTypes.Length == 0)
        {
            error = "Package entry assembly does not contain a public ISunderPackageModule implementation.";
            return null;
        }

        if (moduleTypes.Length > 1)
        {
            error = "Package entry assembly contains multiple public ISunderPackageModule implementations: "
                + string.Join(", ", moduleTypes.Select(static type => type.FullName));
            return null;
        }

        var moduleType = moduleTypes[0];
        if (moduleType.GetConstructor(Type.EmptyTypes) is null)
        {
            error = $"Package module '{moduleType.FullName}' must declare a public parameterless constructor.";
            return null;
        }

        error = null;
        return moduleType;
    }
}
