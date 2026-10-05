using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Utilities;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Authoring;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderPackageDependencyPlannerTests
{
    [Fact]
    public void Project_PreservesWholeLibraryDependenciesNativeAssetsAndRoleOwnedImports()
    {
        using var fixture = new DependencyFixture();
        var planner = fixture.Planner();
        planner.UseAssemblies(SunderPackageRoleMembership.App, ["Ui"], []);
        planner.UseAssemblies(SunderPackageRoleMembership.Worker, ["Helper"], []);
        planner.Complete();
        var worker = planner.Project(SunderPackageRoleMembership.Worker);
        var app = planner.Project(SunderPackageRoleMembership.App);
        Assert.Equal(new[] { "Backend/1.0.0", "Helper/1.0.0", "Native/1.0.0", "Shared/1.0.0" }, worker.Libraries);
        Assert.Equal(new[] { "BuildOnly/1.0.0", "Shared/1.0.0", "Ui/1.0.0" }, app.Libraries);
        Assert.Contains(worker.RuntimePaths, path => path.EndsWith("native/libfixture.dylib", StringComparison.Ordinal));
        Assert.DoesNotContain(app.RuntimePaths, path => path.Contains("/native/", StringComparison.Ordinal));
        Assert.Equal(new[] { fixture.HelperProject }, app.ExcludedProjects);
        using var assets = JsonDocument.Parse(worker.Files.Single(file => file.Path == "project.assets.json").Contents);
        Assert.False(assets.RootElement.GetProperty("libraries").TryGetProperty("Ui/1.0.0", out _));
        Assert.Contains("Backend.targets", Text(worker, ".targets"), StringComparison.Ordinal);
        Assert.DoesNotContain("Ui.targets", Text(worker, ".targets"), StringComparison.Ordinal);
        Assert.Contains("BuildOnly.targets", Text(app, ".targets"), StringComparison.Ordinal);
        Assert.DoesNotContain("Backend.targets", Text(app, ".targets"), StringComparison.Ordinal);
        planner.VerifyInputs();
        File.AppendAllText(fixture.NativeFile, "changed");
        Assert.Contains("changed", Assert.Throws<InvalidDataException>(planner.VerifyInputs).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_RequiresExplicitOwnershipForUnreferencedRootsAndRejectsConflicts()
    {
        using var fixture = new DependencyFixture();
        var unknown = fixture.Planner();
        unknown.UseAssemblies(SunderPackageRoleMembership.Worker, ["Helper"], []);
        Assert.Contains("dependency-role-ambiguous", Assert.Throws<InvalidDataException>(unknown.Complete).Message, StringComparison.Ordinal);
        var explicitRole = fixture.Planner("app");
        explicitRole.UseAssemblies(SunderPackageRoleMembership.Worker, ["Helper"], []);
        explicitRole.Complete();
        Assert.Contains("Ui/1.0.0", explicitRole.Project(SunderPackageRoleMembership.App).Libraries);
        var conflict = fixture.Planner("app");
        conflict.UseAssemblies(SunderPackageRoleMembership.Worker, ["Ui", "Helper"], []);
        Assert.Contains("dependency-role-conflict", Assert.Throws<InvalidDataException>(conflict.Complete).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_RejectsUnownedDynamicImportsAndEscapingPackageFiles()
    {
        using var fixture = new DependencyFixture();
        File.WriteAllText(fixture.Targets, "<Project><Import Project=\"$(UnknownBuildPath)/plugin.targets\" /></Project>");
        var planner = fixture.Planner("app");
        planner.UseAssemblies(SunderPackageRoleMembership.Worker, ["Helper"], []); planner.Complete();
        Assert.Contains("dependency-import-unsupported", Assert.Throws<InvalidDataException>(() => planner.Project(SunderPackageRoleMembership.App)).Message, StringComparison.Ordinal);
        var assets = JsonNode.Parse(File.ReadAllBytes(fixture.Assets))!;
        ((JsonArray)assets["libraries"]!["Native/1.0.0"]!["files"]!).Add("../outside");
        File.WriteAllText(fixture.Assets, assets.ToJsonString());
        planner = fixture.Planner("app");
        planner.UseAssemblies(SunderPackageRoleMembership.Worker, ["Helper"], []); planner.Complete();
        Assert.Contains("not portable", Assert.Throws<InvalidDataException>(() => planner.Project(SunderPackageRoleMembership.Worker)).Message, StringComparison.Ordinal);
    }

    private static string Text(SunderPackageDependencyPlan plan, string suffix)
        => System.Text.Encoding.UTF8.GetString(plan.Files.Single(file => file.Path.EndsWith(suffix, StringComparison.Ordinal)).Contents);

    private sealed class DependencyFixture : IDisposable
    {
        private readonly TemporaryDirectoryScope _scope = TemporaryDirectoryScope.Create("sunder-role-dependencies");
        internal string Assets { get; }
        internal string Targets { get; }
        internal string HelperProject { get; }
        internal string NativeFile { get; }
        private readonly string _project;
        private readonly string _extensions;

        internal DependencyFixture()
        {
            _project = _scope.WriteAllText("Package.csproj", "<Project />");
            HelperProject = _scope.WriteAllText("Helper.csproj", "<Project />");
            _extensions = _scope.CreateDirectory("obj");
            var packages = _scope.CreateDirectory("packages") + Path.DirectorySeparatorChar;
            var target = new JsonObject(); var libraries = new JsonObject();
            Package("Shared", [], ["lib/Shared.dll"]);
            Package("Native", [], ["runtimes/osx-arm64/native/libfixture.dylib"]);
            Package("Backend", ["Shared", "Native"], ["lib/Backend.dll", "build/Backend.targets"]);
            Package("BuildOnly", [], ["build/BuildOnly.targets"]);
            Package("Ui", ["Shared", "BuildOnly"], ["lib/Ui.dll", "build/Ui.targets"]);
            target["Helper/1.0.0"] = new JsonObject { ["type"] = "project", ["dependencies"] = new JsonObject { ["Backend"] = "1.0.0" },
                ["compile"] = new JsonObject { ["bin/placeholder/Helper.dll"] = new JsonObject() }, ["runtime"] = new JsonObject { ["bin/placeholder/Helper.dll"] = new JsonObject() } };
            libraries["Helper/1.0.0"] = new JsonObject { ["type"] = "project", ["path"] = "Helper.csproj", ["msbuildProject"] = "Helper.csproj" };
            var graph = new JsonObject
            {
                ["version"] = 3, ["targets"] = new JsonObject { ["net10.0"] = target.DeepClone(), ["net10.0/osx-arm64"] = target },
                ["libraries"] = libraries, ["packageFolders"] = new JsonObject { [packages] = new JsonObject() },
                ["projectFileDependencyGroups"] = new JsonObject { ["net10.0"] = new JsonArray("Ui >= 1.0.0", "Helper >= 1.0.0") },
                ["project"] = new JsonObject
                {
                    ["frameworks"] = new JsonObject { ["net10.0"] = new JsonObject { ["targetAlias"] = "net10.0", ["dependencies"] = new JsonObject { ["Ui"] = new JsonObject { ["version"] = "[1.0.0, )" } } } },
                    ["restore"] = new JsonObject { ["packagesPath"] = packages, ["frameworks"] = new JsonObject { ["net10.0"] = new JsonObject
                    { ["projectReferences"] = new JsonObject { [HelperProject] = new JsonObject { ["projectPath"] = HelperProject } } } } },
                },
            };
            Assets = _scope.WriteAllText("obj/project.assets.json", graph.ToJsonString());
            _scope.WriteAllText("obj/Package.csproj.nuget.g.props", "<Project />");
            Targets = _scope.WriteAllText("obj/Package.csproj.nuget.g.targets", "<Project><ImportGroup>" + string.Join("", new[] { "Backend", "BuildOnly", "Ui" }
                .Select(name => $"<Import Project=\"$(NuGetPackageRoot){name.ToLowerInvariant()}/1.0.0/build/{name}.targets\" />")) + "</ImportGroup></Project>");
            NativeFile = Path.Combine(packages, "native/1.0.0/runtimes/osx-arm64/native/libfixture.dylib");

            void Package(string name, string[] dependencies, string[] files)
            {
                var body = new JsonObject { ["type"] = "package", ["dependencies"] = new JsonObject(dependencies.Select(value => KeyValuePair.Create<string, JsonNode?>(value, JsonValue.Create("1.0.0")))) };
                foreach (var file in files)
                {
                    _scope.WriteAllText($"packages/{name.ToLowerInvariant()}/1.0.0/{file}", file.EndsWith(".targets", StringComparison.Ordinal) ? "<Project />" : "fixture");
                    var group = file.Contains("/native/", StringComparison.Ordinal) ? "native" : file.EndsWith(".targets", StringComparison.Ordinal) ? "build" : "runtime";
                    if (body[group] is null) body[group] = new JsonObject();
                    body[group]![file] = new JsonObject();
                    if (group == "runtime") { body["compile"] ??= new JsonObject(); body["compile"]![file] = new JsonObject(); }
                }
                target[name + "/1.0.0"] = body;
                libraries[name + "/1.0.0"] = new JsonObject { ["type"] = "package", ["path"] = name.ToLowerInvariant() + "/1.0.0",
                    ["files"] = new JsonArray(files.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
            }
        }

        internal SunderPackageDependencyPlanner Planner(string? uiRole = null)
        {
            var ui = new TaskItem("Ui"); if (uiRole is not null) ui.SetMetadata("SunderPackageAssetRole", uiRole);
            return new(Assets, _project, _extensions, "net10.0", "osx-arm64", [ui], [new TaskItem(HelperProject)], []);
        }
        public void Dispose() => _scope.Dispose();
    }
}
