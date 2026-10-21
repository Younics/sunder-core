using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Build.Utilities;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Authoring;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderPackageDependencyPlannerTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Project_PreservesWholeLibraryDependenciesNativeAssetsAndRoleOwnedImports(int format)
    {
        using var fixture = new DependencyFixture(format);
        var original = File.ReadAllBytes(fixture.Assets);
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
        Assert.Contains(app.CompilePaths, path => path.EndsWith("/ref/Ui.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(app.CompilePaths, path => path.EndsWith("/lib/Ui.dll", StringComparison.Ordinal));
        Assert.Contains(app.RuntimePaths, path => path.EndsWith("/lib/Ui.dll", StringComparison.Ordinal));
        Assert.DoesNotContain(app.RuntimePaths, path => path.EndsWith("/ref/Ui.dll", StringComparison.Ordinal));
        Assert.Equal(new[] { fixture.HelperProject }, app.ExcludedProjects);
        using var assets = JsonDocument.Parse(worker.Files.Single(file => file.Path == "project.assets.json").Contents);
        Assert.False(assets.RootElement.GetProperty("libraries").TryGetProperty("Ui/1.0.0", out _));
        Assert.Equal(format, assets.RootElement.GetProperty("version").GetInt32());
        Assert.Contains("Backend.targets", Text(worker, ".targets"), StringComparison.Ordinal);
        Assert.DoesNotContain("Ui.targets", Text(worker, ".targets"), StringComparison.Ordinal);
        Assert.Contains("BuildOnly.targets", Text(app, ".targets"), StringComparison.Ordinal);
        Assert.DoesNotContain("Backend.targets", Text(app, ".targets"), StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(fixture.Assets));
        planner.VerifyInputs();
        File.AppendAllText(fixture.NativeFile, "changed");
        Assert.Contains("changed", Assert.Throws<InvalidDataException>(planner.VerifyInputs).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Complete_RequiresExplicitOwnershipForUnreferencedRootsAndRejectsConflicts(int format)
    {
        using var fixture = new DependencyFixture(format);
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

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Project_RejectsUnownedDynamicImportsAndEscapingPackageFiles(int format)
    {
        using var fixture = new DependencyFixture(format);
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

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Project_PreservesFormatSpecificAliasPivotsAndEquivalentRoleReferenceClosures(int format)
    {
        using var fixture = new DependencyFixture(format, "production");
        var before = File.ReadAllBytes(fixture.Assets);
        var planner = fixture.Planner("app"); planner.UseAssemblies(SunderPackageRoleMembership.Worker, ["Helper"], []); planner.Complete();
        var app = planner.Project(SunderPackageRoleMembership.App);
        var projected = JsonNode.Parse(app.Files.Single(file => file.Path == "project.assets.json").Contents)!;
        var key = format == 4 ? "production" : "net10.0";
        Assert.Equal(new[] { key, key + "/osx-arm64" }, projected["targets"]!.AsObject().Select(entry => entry.Key));
        Assert.Equal(key, Assert.Single(projected["projectFileDependencyGroups"]!.AsObject()).Key);
        Assert.Equal(key, Assert.Single(projected["project"]!["frameworks"]!.AsObject()).Key);
        Assert.Equal(key, Assert.Single(projected["project"]!["restore"]!["frameworks"]!.AsObject()).Key);
        Assert.Equal("production", projected["project"]!["frameworks"]![key]!["targetAlias"]!.GetValue<string>());
        Assert.Equal("production", Assert.Single(projected["project"]!["restore"]!["originalTargetFrameworks"]!.AsArray())!.GetValue<string>());
        if (format == 4)
        {
            Assert.Equal("net10.0", projected["project"]!["frameworks"]![key]!["framework"]!.GetValue<string>());
            Assert.Equal("net10.0", projected["project"]!["restore"]!["frameworks"]![key]!["framework"]!.GetValue<string>());
        }
        Assert.Contains(app.CompilePaths, path => path.EndsWith("/ref/Ui.dll", StringComparison.Ordinal));
        Assert.Contains(app.RuntimePaths, path => path.EndsWith("/lib/Ui.dll", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(fixture.Assets));
    }

    [Fact]
    public void V4_ProjectsOnlyTheExactAliasWhenEffectiveFrameworksAreEquivalent()
    {
        using var fixture = new DependencyFixture(4, "production");
        var assets = JsonNode.Parse(File.ReadAllBytes(fixture.Assets))!;
        foreach (var path in new[] { assets["project"]!["frameworks"]!, assets["project"]!["restore"]!["frameworks"]! })
        {
            path["development"] = path["production"]!.DeepClone(); path["development"]!["targetAlias"] = "development";
        }
        assets["project"]!["restore"]!["originalTargetFrameworks"]!.AsArray().Add("development");
        assets["targets"]!["development"] = new JsonObject(); assets["targets"]!["development/osx-arm64"] = new JsonObject();
        assets["projectFileDependencyGroups"]!["development"] = new JsonArray("Another >= 9.0.0");
        assets["centralTransitiveDependencyGroups"] = new JsonObject
        {
            ["production"] = new JsonObject { ["Ui"] = new JsonObject { ["version"] = "1.0.0" }, ["Backend"] = new JsonObject { ["version"] = "1.0.0" } },
            ["development"] = new JsonObject { ["Another"] = new JsonObject { ["version"] = "9.0.0" } },
        };
        File.WriteAllText(fixture.Assets, assets.ToJsonString());
        Assert.Throws<InvalidDataException>(() => fixture.Planner("app", "net10.0"));
        var planner = fixture.Planner("app"); planner.UseAssemblies(SunderPackageRoleMembership.Worker, ["Helper"], []); planner.Complete();
        var app = JsonNode.Parse(planner.Project(SunderPackageRoleMembership.App).Files.Single(file => file.Path == "project.assets.json").Contents)!;
        Assert.Equal(new[] { "production", "production/osx-arm64" }, app["targets"]!.AsObject().Select(entry => entry.Key));
        Assert.Equal("production", Assert.Single(app["project"]!["frameworks"]!.AsObject()).Key);
        Assert.Equal("production", Assert.Single(app["project"]!["restore"]!["frameworks"]!.AsObject()).Key);
        Assert.Equal("production", Assert.Single(app["projectFileDependencyGroups"]!.AsObject()).Key);
        Assert.Equal("production", Assert.Single(app["centralTransitiveDependencyGroups"]!.AsObject()).Key);
        Assert.Equal("Ui", Assert.Single(app["centralTransitiveDependencyGroups"]!["production"]!.AsObject()).Key);
    }

    [Theory]
    [InlineData("future-version")]
    [InlineData("legacy-version")]
    [InlineData("string-version")]
    [InlineData("decimal-version")]
    [InlineData("missing-effective-framework")]
    [InlineData("alias-mismatch")]
    [InlineData("restore-framework-mismatch")]
    [InlineData("missing-restore-alias")]
    [InlineData("missing-rid")]
    [InlineData("missing-library")]
    [InlineData("kind-mismatch")]
    [InlineData("invalid-library-key")]
    [InlineData("invalid-files")]
    [InlineData("invalid-target-assets")]
    [InlineData("missing-dependency-group")]
    [InlineData("restore-error")]
    [InlineData("invalid-logs")]
    [InlineData("case-collision")]
    public void V4_RejectsFutureOrCorruptConsumedStructuresBeforeProjection(string fault)
    {
        using var fixture = new DependencyFixture(4, "production");
        var assets = JsonNode.Parse(File.ReadAllBytes(fixture.Assets))!;
        switch (fault)
        {
            case "future-version": assets["version"] = 5; break;
            case "legacy-version": assets["version"] = 2; break;
            case "string-version": assets["version"] = "4"; break;
            case "decimal-version": assets["version"] = JsonNode.Parse("4.0"); break;
            case "missing-effective-framework": assets["project"]!["frameworks"]!["production"]!.AsObject().Remove("framework"); break;
            case "alias-mismatch": assets["project"]!["frameworks"]!["production"]!["targetAlias"] = "other"; break;
            case "restore-framework-mismatch": assets["project"]!["restore"]!["frameworks"]!["production"]!["framework"] = "net9.0"; break;
            case "missing-restore-alias": assets["project"]!["restore"]!["frameworks"]!.AsObject().Remove("production"); break;
            case "missing-rid": assets["targets"]!.AsObject().Remove("production/osx-arm64"); break;
            case "missing-library": assets["libraries"]!.AsObject().Remove("Ui/1.0.0"); break;
            case "kind-mismatch": assets["libraries"]!["Ui/1.0.0"]!["type"] = "project"; break;
            case "invalid-library-key": assets["targets"]!["production/osx-arm64"]!["no-version"] = new JsonObject { ["type"] = "package" }; break;
            case "invalid-files": assets["libraries"]!["Ui/1.0.0"]!["files"] = null; break;
            case "invalid-target-assets": assets["targets"]!["production/osx-arm64"]!["Ui/1.0.0"]!["compile"] = new JsonArray(); break;
            case "missing-dependency-group": assets["projectFileDependencyGroups"]!.AsObject().Remove("production"); break;
            case "restore-error": assets["logs"] = JsonNode.Parse("[{\"level\":\"Error\",\"code\":\"NU1101\"}]"); break;
            case "invalid-logs": assets["logs"] = new JsonObject(); break;
            case "case-collision": assets["Version"] = 4; break;
        }
        var source = assets.ToJsonString(); File.WriteAllText(fixture.Assets, source);
        Assert.Throws<InvalidDataException>(() => fixture.Planner("app"));
        Assert.Equal(source, File.ReadAllText(fixture.Assets));
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
        private readonly string _alias;

        internal DependencyFixture(int format = 3, string alias = "net10.0")
        {
            _alias = alias; var frameworkKey = format == 4 ? alias : "net10.0";
            _project = _scope.WriteAllText("Package.csproj", "<Project />");
            HelperProject = _scope.WriteAllText("Helper.csproj", "<Project />");
            _extensions = _scope.CreateDirectory("obj");
            var packages = _scope.CreateDirectory("packages") + Path.DirectorySeparatorChar;
            var target = new JsonObject(); var libraries = new JsonObject();
            Package("Shared", [], ["lib/Shared.dll"]);
            Package("Native", [], ["runtimes/osx-arm64/native/libfixture.dylib"]);
            Package("Backend", ["Shared", "Native"], ["lib/Backend.dll", "build/Backend.targets"]);
            Package("BuildOnly", [], ["build/BuildOnly.targets"]);
            Package("Ui", ["Shared", "BuildOnly"], ["ref/Ui.dll", "lib/Ui.dll", "build/Ui.targets"]);
            target["Helper/1.0.0"] = new JsonObject { ["type"] = "project", ["dependencies"] = new JsonObject { ["Backend"] = "1.0.0" },
                ["compile"] = new JsonObject { ["bin/placeholder/Helper.dll"] = new JsonObject() }, ["runtime"] = new JsonObject { ["bin/placeholder/Helper.dll"] = new JsonObject() } };
            libraries["Helper/1.0.0"] = new JsonObject { ["type"] = "project", ["path"] = "Helper.csproj", ["msbuildProject"] = "Helper.csproj" };
            var graph = new JsonObject
            {
                ["version"] = format, ["targets"] = new JsonObject { [frameworkKey] = target.DeepClone(), [frameworkKey + "/osx-arm64"] = target },
                ["libraries"] = libraries, ["packageFolders"] = new JsonObject { [packages] = new JsonObject() },
                ["projectFileDependencyGroups"] = new JsonObject { [frameworkKey] = new JsonArray("Ui >= 1.0.0", "Helper >= 1.0.0") },
                ["project"] = new JsonObject
                {
                    ["frameworks"] = new JsonObject { [frameworkKey] = new JsonObject { ["targetAlias"] = alias, ["dependencies"] = new JsonObject { ["Ui"] = new JsonObject { ["version"] = "[1.0.0, )" } } } },
                    ["restore"] = new JsonObject { ["packagesPath"] = packages, ["originalTargetFrameworks"] = new JsonArray(alias), ["frameworks"] = new JsonObject { [frameworkKey] = new JsonObject
                    { ["projectReferences"] = new JsonObject { [HelperProject] = new JsonObject { ["projectPath"] = HelperProject } } } } },
                },
            };
            if (format == 4)
            {
                graph["project"]!["frameworks"]![frameworkKey]!["framework"] = "net10.0";
                graph["project"]!["restore"]!["frameworks"]![frameworkKey]!["framework"] = "net10.0";
            }
            graph["project"]!["restore"]!["frameworks"]![frameworkKey]!["targetAlias"] = alias;
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
                    var group = file.Contains("/native/", StringComparison.Ordinal) ? "native" : file.EndsWith(".targets", StringComparison.Ordinal) ? "build" : file.StartsWith("ref/", StringComparison.Ordinal) ? "compile" : "runtime";
                    if (body[group] is null) body[group] = new JsonObject();
                    body[group]![file] = new JsonObject();
                    if (group == "runtime" && !files.Contains("ref/" + Path.GetFileName(file))) { body["compile"] ??= new JsonObject(); body["compile"]![file] = new JsonObject(); }
                }
                target[name + "/1.0.0"] = body;
                libraries[name + "/1.0.0"] = new JsonObject { ["type"] = "package", ["sha512"] = "fixture-package-content", ["path"] = name.ToLowerInvariant() + "/1.0.0",
                    ["files"] = new JsonArray(files.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) };
            }
        }

        internal SunderPackageDependencyPlanner Planner(string? uiRole = null, string? framework = null)
        {
            var ui = new TaskItem("Ui"); if (uiRole is not null) ui.SetMetadata("SunderPackageAssetRole", uiRole);
            return new(Assets, _project, _extensions, framework ?? _alias, "osx-arm64", [ui], [new TaskItem(HelperProject)], []);
        }
        public void Dispose() => _scope.Dispose();
    }
}
