using Sunder.Runtime.LocalState;

namespace Sunder.Host.Supervisor;

internal static class HostRuntimeStatePathValidator
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    internal static void Validate(
        string hostStateRoot,
        string runtimeStateRoot,
        string externalConnectionPath,
        string? runtimeHostPath,
        string supervisorBasePath,
        string? runtimeResourcePolicyFilePath = null)
    {
        var hostRoot = RuntimeOwnedFileSystem.GetCanonicalPhysicalPath(hostStateRoot);
        var runtimeRoot = RuntimeOwnedFileSystem.GetCanonicalPhysicalPath(runtimeStateRoot);
        if (Contains(hostRoot, runtimeRoot) || Contains(runtimeRoot, hostRoot))
        {
            throw new InvalidOperationException(
                "Host and Runtime state roots must be physically disjoint.");
        }

        var supervisorRoot = RuntimeOwnedFileSystem.GetCanonicalPhysicalPath(supervisorBasePath);
        if (Contains(supervisorRoot, runtimeRoot) || Contains(runtimeRoot, supervisorRoot))
        {
            throw new InvalidOperationException(
                "The Runtime state root and managed Supervisor payload must be physically disjoint.");
        }

        foreach (var hostOwnedPath in new[]
                 {
                      externalConnectionPath,
                      runtimeHostPath,
                      runtimeResourcePolicyFilePath,
                  })
        {
            if (!string.IsNullOrWhiteSpace(hostOwnedPath)
                && Contains(runtimeRoot, RuntimeOwnedFileSystem.GetCanonicalPhysicalPath(hostOwnedPath)))
            {
                throw new InvalidOperationException(
                    $"Host-owned path '{hostOwnedPath}' must not be inside the Runtime state root.");
            }
        }
    }

    private static bool Contains(string root, string candidate)
    {
        if (string.Equals(root, candidate, PathComparison))
        {
            return true;
        }
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }
}
