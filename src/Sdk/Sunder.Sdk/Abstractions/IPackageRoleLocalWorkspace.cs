using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides activation-owned access to a package's persistent workspace for the current host role.</summary>
/// <remarks>
/// The App and Runtime own separate workspace instances and their activation lifetimes. Package callers must not
/// dispose this capability. Workspace roots never cross host process APIs, and relative paths cannot escape the root.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.RoleLocalWorkspaceV1)]
public interface IPackageRoleLocalWorkspace
{
    /// <summary>Gets the authorized package workspace root for the current host role.</summary>
    string WorkspaceRootPath { get; }

    /// <summary>Resolves a non-empty relative path within the authorized role-local workspace.</summary>
    /// <exception cref="ArgumentException">The path is empty, rooted, or contains traversal.</exception>
    string GetLocalPath(string relativePath);
}
