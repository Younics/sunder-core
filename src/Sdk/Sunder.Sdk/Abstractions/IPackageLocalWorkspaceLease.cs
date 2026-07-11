using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>
/// A scoped capability for a package's persistent local workspace.
/// </summary>
/// <remarks>
/// Each host role owns its workspace. Workspace roots never cross host process APIs.
/// Dispose the lease with its package activation scope. Relative paths are validated and
/// cannot escape the workspace.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.LocalWorkspaceV1)]
public interface IPackageLocalWorkspaceLease : IAsyncDisposable
{
    /// <summary>Gets the authorized package workspace root.</summary>
    string WorkspaceRootPath { get; }

    /// <summary>Resolves a non-empty relative path within the authorized workspace.</summary>
    /// <exception cref="ArgumentException">The path is empty, rooted, or contains traversal.</exception>
    string GetLocalPath(string relativePath);
}
