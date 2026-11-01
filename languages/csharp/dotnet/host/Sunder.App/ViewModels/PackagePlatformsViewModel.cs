namespace Sunder.App.ViewModels;

public sealed class PackagePlatformsViewModel
{
    public string VersionText { get; private init; } = string.Empty;
    public string Summary { get; private init; } = "Platform information unavailable.";
    public string DeviceSummary { get; private init; } = string.Empty;
    public bool HasDeviceSummary => DeviceSummary.Length > 0;
    public bool? SupportsCurrentPlatform { get; private init; }
    public bool IsUnsupported => SupportsCurrentPlatform == false;
    public IReadOnlyList<PackagePlatformRoleViewModel> Roles { get; private init; } = [];
    public bool HasRoles => Roles.Count > 0;

    public static PackagePlatformsViewModel Unavailable() => new();
    public static PackagePlatformsViewModel Loading() => new() { Summary = "Loading platform information…" };

    internal static PackagePlatformsViewModel Create(IEnumerable<(string Role, string Rid)>? declaredTargets, string currentRid, string version)
    {
        if (declaredTargets is null) return Unavailable();
        var groups = declaredTargets.Distinct().GroupBy(target => target.Role).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var supported = groups.SelectMany(group => group.Select(target => target.Rid)).Distinct(StringComparer.Ordinal)
            .Where(rid => groups.All(group => group.Any(target => target.Rid == rid))).Order(StringComparer.Ordinal).ToArray();
        var supportsCurrent = groups.All(group => group.Any(target => target.Rid == currentRid));
        var deviceStatus = supportsCurrent ? "platform supported"
            : groups.Any(group => group.Any(target => target.Rid == currentRid)) ? "partial platform support — see target details"
            : "unsupported platform";
        return new()
        {
            VersionText = $"For version {version}",
            Summary = groups.Length == 0 ? "Platform-independent — shared contracts only."
                : supported.Length == 0 ? "No platform includes all declared roles."
                : string.Join(" · ", supported.Select(PlatformName)),
            SupportsCurrentPlatform = supportsCurrent,
            DeviceSummary = $"This Sunder installation: {PlatformName(currentRid)} — {deviceStatus}",
            Roles = groups.Select(group => new PackagePlatformRoleViewModel(
                group.Key == "app" ? "Desktop UI" : group.Key == "runtime" ? "Background worker" : group.Key,
                string.Join(" · ", group.Select(target => target.Rid).Order(StringComparer.Ordinal)
                    .Select(rid => $"{PlatformName(rid)} ({rid})")))).ToArray(),
        };
    }

    private static string PlatformName(string rid) => rid switch
    {
        "win-x64" => "Windows x64",
        "win-arm64" => "Windows ARM64",
        "linux-x64" => "Linux x64",
        "linux-arm64" => "Linux ARM64",
        "osx-x64" => "macOS Intel",
        "osx-arm64" => "macOS Apple Silicon",
        _ => rid,
    };
}

public sealed record PackagePlatformRoleViewModel(string Role, string Platforms);
