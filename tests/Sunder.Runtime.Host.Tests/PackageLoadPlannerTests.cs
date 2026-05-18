using Sunder.Runtime.Host.Services;
using Sunder.Protocol;
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

    private static PreparedRuntimePackage CreatePackage(string packageId, IReadOnlyList<string>? dependencies = null)
        => new(
            SourceFolder: "/source/" + packageId,
            Source: new PackageSourceDescriptor(packageId, PackageSourceKind.Dev, "/source/" + packageId),
            ShadowFolder: "/shadow/" + packageId,
            LibraryFolder: "/shadow/" + packageId + "/lib",
            packageId,
            Version: "1.0.0",
            Manifest: new RuntimePackageManifest
            {
                ManifestVersion = 1,
                Id = packageId,
                Name = packageId,
                Version = "1.0.0",
                EntryAssembly = packageId + ".dll",
            },
            EntryAssemblyPath: "/shadow/" + packageId + "/lib/" + packageId + ".dll",
            Dependencies: dependencies ?? []);
}
