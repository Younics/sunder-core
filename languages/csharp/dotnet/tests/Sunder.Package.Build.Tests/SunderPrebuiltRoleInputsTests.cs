using System.Globalization;
using System.Text.Json.Nodes;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Authoring;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderPrebuiltRoleInputsTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("tr-TR")]
    public void CaptureMapsRoundTripWithOrdinalOrderingAcrossCultures(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["project:backend"] = "backend/Backend.csproj",
                ["project-tree:backend"] = "backend",
                ["import:I.targets"] = "sdk/I.targets",
                ["import:z.targets"] = "sdk/z.targets",
            };
            var bytes = SunderPrebuiltRoleInputs.Write(map);
            var restored = SunderPrebuiltRoleInputs.Read<SortedDictionary<string, string>>(bytes);
            Assert.Equal(map.Keys, restored.Keys);
            Assert.Equal(bytes, SunderPrebuiltRoleInputs.Write(restored));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void RestoredSelectionRelocatesButRejectsTransitiveContentAndRuntimeGraphDrift(int format)
    {
        using var scope = TemporaryDirectoryScope.Create("sunder-prebuilt-restore");
        var firstGraph = scope.WriteAllText("native/RuntimeGraph.json", "{\"runtimes\":{}}");
        var secondGraph = scope.WriteAllText("fanin/RuntimeGraph.json", "{\"runtimes\":{}}");
        var assets = JsonNode.Parse("""
            {"version":3,"targets":{"net10.0":{"Helper/1.0.0":{"type":"project"},"Dependency/2.3.4":{"type":"package"}},"net10.0/linux-x64":{"Helper/1.0.0":{"type":"project"},"Dependency/2.3.4":{"type":"package","runtime":{"lib/net10.0/Dependency.dll":{}}}}},"libraries":{"Helper/1.0.0":{"type":"project","path":"/native/Helper.csproj","msbuildProject":"/native/Helper.csproj"},"Dependency/2.3.4":{"type":"package","sha512":"pinned-content","path":"dependency/2.3.4","files":["lib/net10.0/Dependency.dll"]}},"project":{"frameworks":{"net10.0":{"targetAlias":"net10.0"}}}}
            """)!;
        assets["version"] = format;
        if (format == 4) assets["project"]!["frameworks"]!["net10.0"]!["framework"] = "net10.0";
        assets["project"]!["frameworks"]!["net10.0"]!["runtimeIdentifierGraphPath"] = firstGraph;
        var first = scope.WriteAllText("native/project.assets.json", assets.ToJsonString());
        var expected = SunderPrebuiltRestoreInputs.Capture(first, "net10.0", "linux-x64", default);
        assets["libraries"]!["Helper/1.0.0"]!["path"] = "C:\\fanin\\Helper.csproj";
        assets["libraries"]!["Helper/1.0.0"]!["msbuildProject"] = "C:\\fanin\\Helper.csproj";
        assets["project"]!["frameworks"]!["net10.0"]!["runtimeIdentifierGraphPath"] = secondGraph;
        var second = scope.WriteAllText("fanin/project.assets.json", assets.ToJsonString());
        Assert.Equal(expected, SunderPrebuiltRestoreInputs.Capture(second, "net10.0", "linux-x64", default));
        File.WriteAllText(secondGraph, "{\"runtimes\":{\"linux-x64\":{}}}");
        Assert.NotEqual(expected.Sha256, SunderPrebuiltRestoreInputs.Capture(second, "net10.0", "linux-x64", default).Sha256);
        File.WriteAllText(secondGraph, "{\"runtimes\":{}}");
        assets["libraries"]!["Dependency/2.3.4"]!["sha512"] = "different-content";
        File.WriteAllText(second, assets.ToJsonString());
        Assert.NotEqual(expected.Sha256, SunderPrebuiltRestoreInputs.Capture(second, "net10.0", "linux-x64", default).Sha256);
        Assert.Throws<InvalidDataException>(() => SunderPrebuiltRestoreInputs.Capture(second, "net10.0", "win-x64", default));
    }

    [Fact]
    public void Sdk10AssetsV4RetainsV3CompileRuntimeAndPackageContentEvidence()
    {
        using var scope = TemporaryDirectoryScope.Create("sunder-prebuilt-assets-v4");
        var assets = Sdk10AssetsV4();
        var path = scope.WriteAllText("project.assets.json", assets.ToJsonString());
        var actual = SunderPrebuiltRestoreInputs.Capture(path, "net10.0", "osx-arm64", default);
        var legacy = assets.DeepClone(); legacy["version"] = 3;
        legacy["project"]!["frameworks"]!["net10.0"]!.AsObject().Remove("framework");
        File.WriteAllText(path, legacy.ToJsonString());
        Assert.Equal(actual, SunderPrebuiltRestoreInputs.Capture(path, "net10.0", "osx-arm64", default));
        // Modern NuGet also adds framework when explicitly emitting legacy V3.
        legacy["project"]!["frameworks"]!["net10.0"]!["framework"] = "net10.0";
        File.WriteAllText(path, legacy.ToJsonString());
        Assert.Equal(actual, SunderPrebuiltRestoreInputs.Capture(path, "net10.0", "osx-arm64", default));

        foreach (var (group, original, replacement) in new[]
        {
            ("compile", "ref/net10.0/Avalonia.Controls.dll", "lib/net10.0/Avalonia.Controls.dll"),
            ("runtime", "lib/net10.0/Avalonia.Controls.dll", "ref/net10.0/Avalonia.Controls.dll"),
        })
        {
            var changed = assets.DeepClone();
            var entries = changed["targets"]!["net10.0/osx-arm64"]!["Avalonia/12.0.3"]![group]!.AsObject();
            entries.Remove(original, out var metadata); entries[replacement] = metadata;
            File.WriteAllText(path, changed.ToJsonString());
            Assert.NotEqual(actual.Sha256, SunderPrebuiltRestoreInputs.Capture(path, "net10.0", "osx-arm64", default).Sha256);
        }
    }

    [Fact]
    public void V4SelectsTheExactAliasRatherThanAnotherEquivalentFramework()
    {
        using var scope = TemporaryDirectoryScope.Create("sunder-prebuilt-assets-alias");
        var assets = Sdk10AssetsV4();
        var frameworks = assets["project"]!["frameworks"]!.AsObject();
        frameworks.Remove("net10.0", out var selected); selected!["targetAlias"] = "production";
        frameworks["production"] = selected;
        var targets = assets["targets"]!.AsObject();
        targets.Remove("net10.0", out var neutral); targets.Remove("net10.0/osx-arm64", out var runtime);
        targets["production"] = neutral; targets["production/osx-arm64"] = runtime;
        var path = scope.WriteAllText("project.assets.json", assets.ToJsonString());
        var expected = SunderPrebuiltRestoreInputs.Capture(path, "production", "osx-arm64", default);
        // A legacy V3 alias points to effective-TFM keys and has the same selection evidence.
        var legacy = assets.DeepClone(); legacy["version"] = 3;
        var oldFrameworks = legacy["project"]!["frameworks"]!.AsObject(); oldFrameworks.Remove("production", out var oldFramework);
        oldFramework!.AsObject().Remove("framework"); oldFrameworks["net10.0"] = oldFramework;
        var oldTargets = legacy["targets"]!.AsObject(); oldTargets.Remove("production", out var oldNeutral); oldTargets.Remove("production/osx-arm64", out var oldRuntime);
        oldTargets["net10.0"] = oldNeutral; oldTargets["net10.0/osx-arm64"] = oldRuntime;
        File.WriteAllText(path, legacy.ToJsonString());
        Assert.Equal(expected, SunderPrebuiltRestoreInputs.Capture(path, "production", "osx-arm64", default));

        frameworks["development"] = selected.DeepClone(); frameworks["development"]!["targetAlias"] = "development";
        targets["development"] = new JsonObject(); targets["development/osx-arm64"] = new JsonObject();
        File.WriteAllText(path, assets.ToJsonString());
        Assert.Equal(expected, SunderPrebuiltRestoreInputs.Capture(path, "production", "osx-arm64", default));
        Assert.NotEqual(expected.Sha256, SunderPrebuiltRestoreInputs.Capture(path, "development", "osx-arm64", default).Sha256);
        Assert.Throws<InvalidDataException>(() => SunderPrebuiltRestoreInputs.Capture(path, "net10.0", "osx-arm64", default));
        frameworks["production"]!["framework"] = "net9.0";
        File.WriteAllText(path, assets.ToJsonString());
        Assert.NotEqual(expected.Sha256, SunderPrebuiltRestoreInputs.Capture(path, "production", "osx-arm64", default).Sha256);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("5")]
    [InlineData("2147483648")]
    [InlineData("\"4\"")]
    [InlineData("4.0")]
    [InlineData("null")]
    public void UnknownFutureAndMalformedAssetsVersionsAreRejected(string version)
    {
        using var scope = TemporaryDirectoryScope.Create("sunder-prebuilt-assets-version");
        var assets = Sdk10AssetsV4(); assets["version"] = JsonNode.Parse(version);
        var path = scope.WriteAllText("project.assets.json", assets.ToJsonString());
        Assert.Throws<InvalidDataException>(() => SunderPrebuiltRestoreInputs.Capture(path, "net10.0", "osx-arm64", default));
    }

    [Theory]
    [InlineData("missing-framework")]
    [InlineData("alias-mismatch")]
    [InlineData("missing-rid")]
    [InlineData("null-target")]
    [InlineData("missing-library")]
    [InlineData("unknown-kind")]
    [InlineData("kind-mismatch")]
    [InlineData("missing-package-hash")]
    [InlineData("invalid-files")]
    [InlineData("invalid-compile")]
    [InlineData("invalid-asset-metadata")]
    [InlineData("invalid-dependencies")]
    [InlineData("restore-error")]
    [InlineData("invalid-logs")]
    [InlineData("unknown-log-level")]
    [InlineData("case-collision")]
    public void V4StillRejectsCorruptSelectionAndRestoreEvidence(string fault)
    {
        using var scope = TemporaryDirectoryScope.Create("sunder-prebuilt-assets-corrupt");
        var assets = Sdk10AssetsV4();
        var framework = assets["project"]!["frameworks"]!["net10.0"]!.AsObject();
        var library = assets["libraries"]!["Avalonia/12.0.3"]!.AsObject();
        var target = assets["targets"]!["net10.0/osx-arm64"]!["Avalonia/12.0.3"]!.AsObject();
        switch (fault)
        {
            case "missing-framework": framework.Remove("framework"); break;
            case "alias-mismatch": framework["targetAlias"] = "another"; break;
            case "missing-rid": assets["targets"]!.AsObject().Remove("net10.0/osx-arm64"); break;
            case "null-target": assets["targets"]!["net10.0/osx-arm64"] = null; break;
            case "missing-library": assets["libraries"]!.AsObject().Remove("Avalonia/12.0.3"); break;
            case "unknown-kind": library["type"] = "unknown"; target["type"] = "unknown"; break;
            case "kind-mismatch": library["type"] = "project"; break;
            case "missing-package-hash": library.Remove("sha512"); break;
            case "invalid-files": library["files"] = new JsonArray(17); break;
            case "invalid-compile": target["compile"] = new JsonArray(); break;
            case "invalid-asset-metadata": target["runtime"]!["lib/net10.0/Avalonia.Controls.dll"] = null; break;
            case "invalid-dependencies": target["dependencies"] = new JsonArray(); break;
            case "restore-error": assets["logs"] = JsonNode.Parse("[{\"level\":\"Error\",\"code\":\"NU1101\"}]"); break;
            case "invalid-logs": assets["logs"] = new JsonObject(); break;
            case "unknown-log-level": assets["logs"] = JsonNode.Parse("[{\"level\":\"Fatal\"}]"); break;
            case "case-collision": assets["Version"] = 4; break;
        }
        var path = scope.WriteAllText("project.assets.json", assets.ToJsonString());
        Assert.Throws<InvalidDataException>(() => SunderPrebuiltRestoreInputs.Capture(path, "net10.0", "osx-arm64", default));
    }

    private static JsonNode Sdk10AssetsV4()
    {
        // Minimized from a real SDK 10.0.402 Avalonia starter restore. The V4
        // framework/targetAlias pair and Avalonia's distinct reference/runtime
        // paths, content hash and asset metadata are retained. Checkout/cache
        // locations and unrelated packages/files are deliberately not fixtures.
        var assets = JsonNode.Parse("""
            {
              "version": 4,
              "targets": {
                "net10.0": {
                  "Avalonia/12.0.3": {
                    "type": "package",
                    "compile": { "ref/net10.0/Avalonia.Controls.dll": { "related": ".xml" } },
                    "runtime": { "lib/net10.0/Avalonia.Controls.dll": { "related": ".xml" } },
                    "build": { "buildTransitive/Avalonia.props": {}, "buildTransitive/Avalonia.targets": {} }
                  }
                }
              },
              "libraries": {
                "Avalonia/12.0.3": {
                  "sha512": "OVAzdZB5T/QIOEpw/WmQ0ZJM13BMmLO7RqR5z+lZtBMuStK63W68SV/Q+PNn/GdFEC3Ab8eQhH2FMb8FhNLK+w==",
                  "type": "package",
                  "path": "avalonia/12.0.3",
                  "hasTools": true,
                  "files": [".nupkg.metadata", ".signature.p7s", "buildTransitive/Avalonia.props", "buildTransitive/Avalonia.targets", "lib/net10.0/Avalonia.Controls.dll", "lib/net10.0/Avalonia.Controls.xml", "ref/net10.0/Avalonia.Controls.dll", "ref/net10.0/Avalonia.Controls.xml"]
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "framework": "net10.0", "targetAlias": "net10.0",
                    "imports": ["net461", "net462", "net47", "net471", "net472", "net48", "net481"],
                    "assetTargetFallback": true, "warn": true,
                    "frameworkReferences": { "Microsoft.NETCore.App": { "privateAssets": "all" } }
                  }
                }
              }
            }
            """)!;
        assets["targets"]!["net10.0/osx-arm64"] = assets["targets"]!["net10.0"]!.DeepClone();
        return assets;
    }
}
