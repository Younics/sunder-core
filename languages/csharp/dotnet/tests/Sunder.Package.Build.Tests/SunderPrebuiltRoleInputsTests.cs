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

    [Fact]
    public void RestoredSelectionRelocatesButRejectsTransitiveContentAndRuntimeGraphDrift()
    {
        using var scope = TemporaryDirectoryScope.Create("sunder-prebuilt-restore");
        var firstGraph = scope.WriteAllText("native/RuntimeGraph.json", "{\"runtimes\":{}}");
        var secondGraph = scope.WriteAllText("fanin/RuntimeGraph.json", "{\"runtimes\":{}}");
        var assets = JsonNode.Parse("""
            {"version":3,"targets":{"net10.0":{"Helper/1.0.0":{"type":"project"},"Dependency/2.3.4":{"type":"package"}},"net10.0/linux-x64":{"Helper/1.0.0":{"type":"project"},"Dependency/2.3.4":{"type":"package","runtime":{"lib/net10.0/Dependency.dll":{}}}}},"libraries":{"Helper/1.0.0":{"type":"project","path":"/native/Helper.csproj","msbuildProject":"/native/Helper.csproj"},"Dependency/2.3.4":{"type":"package","sha512":"pinned-content","path":"dependency/2.3.4","files":["lib/net10.0/Dependency.dll"]}},"project":{"frameworks":{"net10.0":{"targetAlias":"net10.0"}}}}
            """)!;
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
}
