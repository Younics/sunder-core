using System.Security;
using Sunder.Core.TestSupport;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Capability", "Process")]
    public async Task SingleProject_WatchIncludesAuthoredUiAssetsAndContractsWithoutGeneratedOutputs(bool customContracts)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-source-watch");
        var root = ResolvePhysicalTemporaryPath(scope.CreateDirectory("package"));
        var linked = ResolvePhysicalTemporaryPath(scope.WriteAllText("linked/banner.txt", "linked resource"));
        var contracts = customContracts ? "contracts/reviewed" : "sunder-contracts";
        var expected = new[] { "Package.cs", "Views/Main.axaml", "Assets/icon.svg", "settings.json", "content.txt", contracts + ".lock.json", contracts + ".owned.json" };
        foreach (var path in expected.Concat(["Assets/ignored.txt", "obj/generated.axaml", "bin/generated.cs", "notes.txt"]))
            scope.WriteAllText("package/" + path, "watch input");
        var targets = Path.Combine(FindRepositoryRoot(), "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.targets");
        var project = scope.WriteAllText("package/Watch.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><SunderPackageProject>true</SunderPackageProject>
                {{(customContracts ? $"<SunderContractLockPath>{contracts}.lock.json</SunderContractLockPath><SunderOwnedContractsPath>{contracts}.owned.json</SunderOwnedContractsPath>" : "")}}
              </PropertyGroup>
              <ItemGroup>
                <AvaloniaXaml Include="Views/Main.axaml" />
                <AvaloniaResource Include="{{SecurityElement.Escape(linked)}}" Link="banner.txt" />
                <None Update="settings.json" CopyToOutputDirectory="PreserveNewest" />
                <None Update="Assets/ignored.txt" Watch="false" />
                <Content Include="content.txt" />
              </ItemGroup>
              <Import Project="{{SecurityElement.Escape(targets)}}" />
            </Project>
            """);
        var result = await RunDotnetAsync(root, ["watch", "--project", ResolvePhysicalTemporaryPath(project), "--list"]);
        AssertSuccess(result);
        var watched = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Trim()).ToHashSet(StringComparer.Ordinal);
        Assert.All(expected, path => Assert.Contains(Path.GetFullPath(Path.Combine(root, path)), watched));
        Assert.Contains(linked, watched);
        Assert.DoesNotContain(watched, path => path.StartsWith(Path.Combine(root, "bin") + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || path.StartsWith(Path.Combine(root, "obj") + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.DoesNotContain(Path.Combine(root, "Assets", "ignored.txt"), watched);
        Assert.DoesNotContain(Path.Combine(root, "notes.txt"), watched);
    }
}
