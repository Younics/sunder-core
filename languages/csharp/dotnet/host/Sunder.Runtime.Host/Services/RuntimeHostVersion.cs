using System.Reflection;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeHostVersion
{
    public static string Current => GetCurrentText();

    internal static string StripBuildMetadata(string version)
    {
        var buildMetadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return buildMetadataIndex < 0 ? version : version[..buildMetadataIndex];
    }

    private static string GetCurrentText()
    {
        var assembly = typeof(RuntimeHostVersion).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return StripBuildMetadata(informationalVersion);
        }

        return assembly.GetName().Version?.ToString(3) ?? "Development";
    }
}
