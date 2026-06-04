using Sunder.PackageManagement;
using Sunder.Protocol;
using Sunder.Registry.Shared;

namespace Sunder.App.ViewModels;

internal static class StackDisplayFormatters
{
    public static string Plural(int count) => count == 1 ? string.Empty : "s";

    public static string PackageGlyph(PackageIconDescriptor? icon, string displayName, string packageId)
    {
        if (!string.IsNullOrWhiteSpace(icon?.Glyph))
        {
            return icon.Glyph!;
        }

        var source = string.IsNullOrWhiteSpace(displayName) ? packageId : displayName;
        var first = source.FirstOrDefault(char.IsLetterOrDigit);
        return first == default ? "?" : char.ToUpperInvariant(first).ToString();
    }

    public static string PackageRequirementText(SunderStackPackageRequirement package, string separator = " · ")
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(package.InstallTag))
        {
            parts.Add(package.InstallTag);
        }

        if (!string.IsNullOrWhiteSpace(package.MinimumVersion))
        {
            parts.Add($">= {package.MinimumVersion}");
        }

        if (!string.IsNullOrWhiteSpace(package.CreatedWithVersion))
        {
            parts.Add($"created with {package.CreatedWithVersion}");
        }

        parts.Add(package.Required == true ? "required" : "optional");
        return string.Join(separator, parts);
    }

    public static string PackageRequirementText(RegistryStackPackageRequirement package, string separator = " · ")
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(package.InstallTag))
        {
            parts.Add(package.InstallTag);
        }

        if (!string.IsNullOrWhiteSpace(package.MinimumVersion))
        {
            parts.Add($">= {package.MinimumVersion}");
        }

        if (!string.IsNullOrWhiteSpace(package.CreatedWithVersion))
        {
            parts.Add($"created with {package.CreatedWithVersion}");
        }

        parts.Add(package.Required ? "required" : "optional");
        return string.Join(separator, parts);
    }

    public static string ShortenSingleLine(string value, int maxLength)
    {
        var normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..Math.Max(4, maxLength - 3)] + "...";
    }
}

public sealed record StackPackageInfo(string DisplayName, PackageIconDescriptor? Icon, Uri? IconUri);
