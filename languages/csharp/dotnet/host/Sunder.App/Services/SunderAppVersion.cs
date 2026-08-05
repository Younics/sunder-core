using System.Reflection;

namespace Sunder.App.Services;

internal static class SunderAppVersion
{
    public static string CurrentText => GetCurrentText();

    public static string CurrentDisplayText => $"v{StripBuildMetadata(CurrentText)}";

    private static string GetCurrentText()
    {
        var assembly = typeof(SunderAppVersion).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion;
        }

        return assembly.GetName().Version?.ToString(3) ?? "Development";
    }

    private static string StripBuildMetadata(string version)
    {
        var buildMetadataIndex = version.IndexOf('+', StringComparison.Ordinal);
        return buildMetadataIndex < 0 ? version : version[..buildMetadataIndex];
    }
}
