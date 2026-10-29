namespace Sunder.Package.Format;

/// <summary>Framework-dependent Worker V1 profiles. Recognition does not grant Host execution support.</summary>
internal static class DotNetWorkerProfile
{
    /// <summary>Recognizes a canonical .NET Worker target and derives its single-major runtime range.</summary>
    public static bool TryGetRuntimeRange(string? targetFramework, out string range)
    {
        range = string.Empty;
        if (targetFramework is null || !targetFramework.StartsWith("net", StringComparison.Ordinal)
            || !targetFramework.EndsWith(".0", StringComparison.Ordinal)
            || !int.TryParse(targetFramework.AsSpan(3, targetFramework.Length - 5), out var major)
            || major is < 10 or > 99 || targetFramework != $"net{major}.0") return false;
        range = $">={major}.0.0 <{major + 1}.0.0";
        return true;
    }

    /// <summary>Whether the running release permits authoring this target.</summary>
    public static bool IsSupported(string? targetFramework)
        => TryGetRuntimeRange(targetFramework, out _) && SunderToolchainPolicy.DotNetRuntimeMajors
            .Any(major => targetFramework == $"net{major}.0");
}
