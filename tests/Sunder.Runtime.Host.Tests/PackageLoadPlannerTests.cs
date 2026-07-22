using Sunder.Package.Format;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class PackageLoadPlannerTests
{
    [Fact]
    public void ResolveLoadOrder_LoadsDependenciesBeforeDependents()
    {
        var errors = new List<string>();
        var planner = new PackageLoadPlanner();

        var ordered = planner.ResolveLoadOrder(
            [CreatePackage("package.app", ["package.core"]), CreatePackage("package.core")],
            errors);

        Assert.Empty(errors);
        Assert.Equal(["package.core", "package.app"], ordered.Select(package => package.PackageId).ToArray());
    }

    [Fact]
    public void ResolveLoadOrder_ExcludesPackageWithMissingDependency()
    {
        var errors = new List<string>();
        var planner = new PackageLoadPlanner();

        var ordered = planner.ResolveLoadOrder(
            [CreatePackage("package.app", ["package.missing"]), CreatePackage("package.tools")],
            errors);

        Assert.Equal(["package.tools"], ordered.Select(package => package.PackageId).ToArray());
        Assert.Contains(errors, error => error.Contains("depends on 'package.missing'", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveLoadOrder_ReportsDuplicatePackageIds()
    {
        var errors = new List<string>();
        var planner = new PackageLoadPlanner();

        var ordered = planner.ResolveLoadOrder(
            [CreatePackage("package.app"), CreatePackage("package.app")],
            errors);

        Assert.Single(ordered);
        Assert.Contains(errors, error => error.Contains("Duplicate package id 'package.app'", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveLoadOrder_ExcludesPackageWhoseDependencyVersionIsOutsideAuthoredRange()
    {
        var errors = new List<string>();
        var planner = new PackageLoadPlanner();

        var ordered = planner.ResolveLoadOrder(
            [
                CreatePackage("package.app", [new PackageDependencyDescriptor("package.core", ">=1.0.0 <2.0.0")]),
                CreatePackage("package.core", version: "2.0.0"),
            ],
            errors);

        Assert.Equal(["package.core"], ordered.Select(package => package.PackageId).ToArray());
        Assert.Contains(errors, error =>
            error.Contains("package.app", StringComparison.Ordinal)
            && error.Contains(">=1.0.0 <2.0.0", StringComparison.Ordinal)
            && error.Contains("2.0.0", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveLoadOrder_ExcludesDependencyCycles()
    {
        var errors = new List<string>();
        var planner = new PackageLoadPlanner();

        var ordered = planner.ResolveLoadOrder(
            [CreatePackage("package.a", ["package.b"]), CreatePackage("package.b", ["package.a"])],
            errors);

        Assert.Empty(ordered);
        Assert.Contains(errors, error => error.Contains("Dependency cycle detected", StringComparison.Ordinal));
    }

    private static PreparedRuntimePackage CreatePackage(
        string packageId,
        IReadOnlyList<string>? dependencyIds = null,
        string version = "1.0.0")
        => CreatePackage(
            packageId,
            (dependencyIds ?? [])
                .Select(dependencyId => new PackageDependencyDescriptor(dependencyId, ">=0.0.0-0"))
                .ToArray(),
            version);

    private static PreparedRuntimePackage CreatePackage(
        string packageId,
        IReadOnlyList<PackageDependencyDescriptor> dependencies,
        string version = "1.0.0")
        => new(
            SourceFolder: "/source/" + packageId,
            Source: new RuntimePackageSource(packageId, PackageSourceKind.Dev, "/source/" + packageId),
            ShadowFolder: "/shadow/" + packageId,
            LibraryFolder: "/shadow/" + packageId + "/lib",
            packageId,
            Version: version,
            HostRoles: PackageHostRoles.Runtime,
            Activation: new RuntimePackageActivationState(packageId, packageId, version, PackageHostRoles.Runtime, null),
            EntryAssemblyPath: "/shadow/" + packageId + "/lib/" + packageId + ".dll",
            Dependencies: dependencies);
}
