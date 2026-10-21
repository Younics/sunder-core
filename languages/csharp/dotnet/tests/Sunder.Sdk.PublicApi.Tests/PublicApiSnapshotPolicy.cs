using System.Text.Json;

namespace Sunder.Sdk.PublicApi.Tests;

internal static class PublicApiSnapshotPolicy
{
    internal static string? GetUpdateRoot(string variable)
    {
        var updateRoot = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(updateRoot)) return null;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "eng", "compatibility", "supported-releases.json")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("Snapshot updates require the source checkout and supported-releases.json.");

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "eng", "compatibility", "supported-releases.json")));
        if (manifest.RootElement.GetProperty("releaseStatus").GetString() != "UNRELEASED"
            || manifest.RootElement.GetProperty("releases").GetArrayLength() != 0)
            throw new InvalidOperationException("Snapshot updates require an explicitly unreleased candidate.");
        EnsureUpdatesAllowed(manifest.RootElement.GetProperty("status").GetString(),
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CI"))
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")));

        var output = Path.GetFullPath(updateRoot);
        var protectedRoot = Path.Combine(directory.FullName, "eng", "compatibility");
        var relative = Path.GetRelativePath(protectedRoot, output);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Snapshot updates cannot write compatibility history. Use an editable candidate or temporary output directory.");
        return output;
    }

    internal static void EnsureUpdatesAllowed(string? status, bool ci)
    {
        if (ci || status != "UNFROZEN")
            throw new InvalidOperationException("Public API update variables are forbidden in CI and for frozen release baselines. Compare retained releases; do not reset them.");
    }
}
