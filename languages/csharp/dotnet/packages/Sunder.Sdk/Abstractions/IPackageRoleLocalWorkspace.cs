using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Abstractions;

/// <summary>Provides activation-owned access to a package's persistent workspace for the current host role.</summary>
/// <remarks>
/// The App and Runtime own separate workspace instances and their activation lifetimes. Package callers must not
/// dispose this capability. Workspace roots never cross host process APIs. Relative paths use
/// <see cref="PackageStorageValidation"/> and are lexically contained beneath the workspace root. A returned local
/// path does not authorize a symbolic-link target outside that root; packages are responsible for links they create
/// or pass to other filesystem APIs.
/// </remarks>
[SunderSdkCapability(SunderSdkCapabilities.RoleLocalWorkspaceV1)]
public interface IPackageRoleLocalWorkspace
{
    /// <summary>Gets the authorized package workspace root for the current host role.</summary>
    string WorkspaceRootPath { get; }

    /// <summary>Resolves a portable non-empty relative path within the authorized role-local workspace.</summary>
    /// <exception cref="ArgumentException">The path violates the portable package-relative path contract.</exception>
    string GetLocalPath(string relativePath);
}
