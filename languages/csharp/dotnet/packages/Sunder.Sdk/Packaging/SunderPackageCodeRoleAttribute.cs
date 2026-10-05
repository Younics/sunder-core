namespace Sunder.Sdk.Packaging;

/// <summary>Identifies the compilation ownership of package source when ordinary service/view inference is insufficient.</summary>
public enum SunderPackageCodeRole
{
    /// <summary>Compiles the source into the isolated Worker implementation.</summary>
    Worker,

    /// <summary>Compiles the source into the in-process package UI role.</summary>
    App,

    /// <summary>Compiles role-neutral source independently into both roles; no object or static state is shared between processes.</summary>
    Shared,
}

/// <summary>Declares an explicit role for a package type with ambiguous ownership.</summary>
/// <remarks>
/// This is an advanced source-ownership constraint, not an RPC exposure or permission grant.
/// Service implementations and registered views normally establish ownership without this attribute.
/// A shared declaration cannot depend on a role-exclusive implementation.
/// </remarks>
/// <param name="role">The explicit compilation ownership.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Enum | AttributeTargets.Delegate,
    AllowMultiple = false, Inherited = false)]
public sealed class SunderPackageCodeRoleAttribute(SunderPackageCodeRole role) : Attribute
{
    /// <summary>Gets the declared compilation ownership.</summary>
    public SunderPackageCodeRole Role { get; } = role;
}
