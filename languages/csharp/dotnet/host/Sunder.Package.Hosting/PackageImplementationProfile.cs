using Sunder.Package.Format;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Hosting;

/// <summary>A complete implementation boundary. Capabilities from different profiles are never combined.</summary>
public sealed record PackageImplementationProfile(
    string Role,
    string Kind,
    IReadOnlySet<int> SdkMajors,
    IReadOnlySet<string> HostCapabilities,
    IReadOnlySet<string> TargetFrameworks,
    string? AvaloniaVersion = null)
{
    public IReadOnlyList<string> Validate(SunderPackageTargetManifest target)
    {
        var errors = new List<string>();
        if (target.Role != Role || target.Kind != Kind)
            errors.Add($"This profile implements '{Role}/{Kind}', not '{target.Role}/{target.Kind}'.");
        if (!SemanticVersion.TryParse(target.SdkVersion, out var sdk)
            || !SdkMajors.Any(major => major.ToString(System.Globalization.CultureInfo.InvariantCulture) == sdk.Major))
            errors.Add($"SDK '{target.SdkVersion}' is not in the supported SDK majors [{string.Join(", ", SdkMajors.Order())}].");
        if (TargetFrameworks.Count > 0 && (target.TargetFramework is null || !TargetFrameworks.Contains(target.TargetFramework)))
            errors.Add($"Target framework '{target.TargetFramework}' is not implemented by this profile.");
        if (Kind == SunderPackageFormat.AvaloniaTargetKind
            && !SupportsAvalonia(target.AvaloniaVersion, AvaloniaVersion))
            errors.Add($"Avalonia '{target.AvaloniaVersion}' is not supported by App framework '{AvaloniaVersion}'.");
        if (target.RequiredHostCapabilities is null)
            errors.Add("The target is missing requiredHostCapabilities.");
        foreach (var capability in target.RequiredHostCapabilities ?? [])
            if (capability is null || !HostCapabilities.Contains(capability))
                errors.Add($"Host capability '{capability}' is not implemented by '{Role}/{Kind}'.");
        var protocol = Kind switch { "worker" => "worker-protocol.v1", "avalonia" => "app-bridge.v1", "web" => "web-bridge.v1", _ => null };
        if (protocol is not null && target.RequiredHostCapabilities?.Contains(protocol, StringComparer.Ordinal) != true)
            errors.Add($"The target must require '{protocol}'.");
        return errors;
    }

    public static bool SupportsAvalonia(string? required, string? implemented)
        => SemanticVersion.TryParse(required, out var minimum) && SemanticVersion.TryParse(implemented, out var current)
           && minimum.Major == current.Major && PackageVersionRange.IsSatisfiedBy(implemented!, ">=" + required);
}
