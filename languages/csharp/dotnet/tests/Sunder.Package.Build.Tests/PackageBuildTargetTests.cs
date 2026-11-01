using Xunit;
using static Sunder.Package.Build.Tests.PackageBuildProcessFixture;
using static Sunder.Package.Build.Tests.PackageManifestFixture;

namespace Sunder.Package.Build.Tests;

[Collection("Package build artifacts")]
public sealed class PackageBuildTargetTests
{
    [Theory]
    [InlineData("app")]
    [InlineData(" app ")]
    [InlineData("runtime")]
    [InlineData(null)]
    public async Task PackageTargets_QueryCurrentAppUsesWithoutBuildingAndPreserveDuplicates(string? role)
    {
        var root = CreateTempDirectory();
        var targetsPath = Path.GetFullPath(Path.Combine(FindTaskDirectory(), "..", "build", "Sunder.Package.Build.targets"));
        var appProject = Path.Combine(root, "App.proj");
        var declaration = role is null ? string.Empty
            : $"<SunderUsesContract Include=\"test.rpc\" Role=\"{role}\" ContractVersion=\"2.0.0\" Required=\"true\" Actions=\"discover;invoke\" />";
        await File.WriteAllTextAsync(appProject, $$"""
            <Project>
              <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                {{declaration}}
                {{declaration}}
              </ItemGroup>
              <Import Project="{{targetsPath}}" />
              <Target Name="Build"><Error Text="Query must not build the App." /></Target>
              <Target Name="Restore"><Error Text="Query must not restore the App." /></Target>
              <Target Name="CheckQueryContext" BeforeTargets="_GetSunderPackageAppContractUses">
                <Error Condition="'$(RuntimeIdentifier)' != '' or '$(BuildProjectReferences)' != 'false'"
                       Text="App query did not isolate its evaluation context." />
              </Target>
            </Project>
            """);
        var project = Path.Combine(root, "Package.proj");
        await File.WriteAllTextAsync(project, $$"""
            <Project>
              <PropertyGroup>
                <Configuration>Debug</Configuration>
                <TargetFramework>net10.0</TargetFramework>
                <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
                <SunderPackagePrimaryProject>true</SunderPackagePrimaryProject>
                <SunderPackageUsePrebuiltLeaves>true</SunderPackageUsePrebuiltLeaves>
                <BuildProjectReferences>false</BuildProjectReferences>
                <SunderPackageAppProject>{{appProject}}</SunderPackageAppProject>
                <SunderPackageAppTargetFramework>net8.0</SunderPackageAppTargetFramework>
              </PropertyGroup>
              <Import Project="{{targetsPath}}" />
              <Target Name="Run" DependsOnTargets="_ValidateSunderPackagePrimaryProject;_PrepareSunderContractPackInputs">
                <Message Importance="high" Text="queried-count:@(_SunderRpcPackAppUse->Count())" />
                <Message Importance="high" Text="@(_SunderRpcPackAppUse->'%(ContractVersion)|%(Required)|%(Actions)')" />
              </Target>
            </Project>
            """);
        var result = await ReadProcessAsync(StartMsbuild(project, root));
        if (role == "runtime")
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("must explicitly declare Role=app", result.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(result.ExitCode == 0, result.Output);
            Assert.Contains(role is null ? "queried-count:0" : "queried-count:2", result.Output, StringComparison.Ordinal);
            if (role is not null) Assert.Contains("2.0.0|true|discover;invoke", result.Output, StringComparison.Ordinal);
        }
        Assert.False(Directory.Exists(Path.Combine(root, "obj")));
    }
}
