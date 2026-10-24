using System.Xml.Linq;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class TaskShadowStagingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    [Trait("Capability", "Filesystem")]
    public async Task OwnerRebuild_WithExternallyScheduledReferences_PreservesAnActiveConsumersReferences(
        bool generatePackageOnBuild,
        bool buildOwnerFirst)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-task-shadow-rebuild-shared-graph");
        var root = FindRepositoryRoot();
        var fixture = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(scope.GetPath("fixture"));
        CopyDirectory(Path.Combine(root, "languages/csharp/dotnet/tests/Sunder.Package.Build.TestFixtures/TaskShadowCleanBuild"), fixture);
        var owner = Path.Combine(fixture, "Sunder.Package.Build/Sunder.Package.Build.csproj");
        var dependency = Path.Combine(fixture, "Sunder.Package.Format/Sunder.Package.Format.csproj");
        var reference = Path.Combine(fixture, "Sunder.Package.Format/obj/Debug/net10.0/ref/Sunder.Package.Format.dll");
        var sourceLock = Path.Combine(root, "languages/csharp/dotnet/packages/Sunder.Package.Build/build/Sunder.Package.Build.SourceLock.targets");
        var consumerReady = scope.GetPath("consumer-ready.txt");
        var ownerComplete = scope.GetPath("owner-complete.txt");

        // An end-of-build existence check misses deletion followed by recreation.
        // Reject the late Clean itself while a real consumer holds resolved refs.
        var document = XDocument.Load(dependency);
        document.Root!.Add(XElement.Parse($$"""
            <Target Name="RejectCleaningAnActiveConsumersReferences" BeforeTargets="CoreClean"
                    Condition="Exists('{{consumerReady}}')">
              <Error Text="task-shadow.shared-reference-cleaned-during-consumer" />
            </Target>
            """));
        document.Save(dependency);
        var barrier = scope.WriteAllText("RebuildBarrier.targets", """
            <Project>
              <UsingTask TaskName="WaitForRebuildSignal" TaskFactory="RoslynCodeTaskFactory"
                         AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
                <ParameterGroup><File ParameterType="System.String" Required="true" /></ParameterGroup>
                <Task><Code Type="Fragment" Language="cs"><![CDATA[
                  var engine = (Microsoft.Build.Framework.IBuildEngine3)BuildEngine;
                  engine.Yield();
                  try
                  {
                      var deadline = System.DateTime.UtcNow.AddSeconds(30);
                      while (!System.IO.File.Exists(File))
                      {
                          if (System.DateTime.UtcNow >= deadline)
                              throw new System.TimeoutException("Missing rebuild signal: " + File);
                          System.Threading.Thread.Sleep(10);
                      }
                  }
                  finally { engine.Reacquire(); }
                ]]></Code></Task>
              </UsingTask>
            </Project>
            """);
        scope.WriteAllText("Consumer/Consumer.cs", """
            public sealed class Consumer
            {
                public string Value => Sunder.Package.Build.TestFixtures.TaskShadow.TaskShadowDependency.Value;
            }
            """);
        var consumer = scope.WriteAllText("Consumer/Consumer.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="{{dependency}}" /></ItemGroup>
              <Import Project="{{barrier}}" />
              <Target Name="HoldResolvedReferences" BeforeTargets="CoreCompile">
                <Error Condition="!Exists('{{reference}}')" Text="The solution did not build the shared reference first." />
                <WriteLinesToFile File="{{consumerReady}}" Lines="ready" Overwrite="true" />
                <WaitForRebuildSignal File="{{ownerComplete}}" />
              </Target>
              <Target Name="Probe" DependsOnTargets="Build" />
            </Project>
            """);
        var ownerDriver = scope.WriteAllText("LateOwner.proj", $$"""
            <Project>
              <Import Project="{{barrier}}" />
              <Target Name="Probe">
                <WaitForRebuildSignal File="{{consumerReady}}" />
                <MSBuild Projects="{{owner}}" Targets="Rebuild"
                         Properties="BuildProjectReferences=false;GeneratePackageOnBuild={{generatePackageOnBuild}};SunderPackageBuildSourceLockTargetsPath={{sourceLock}}" />
                <WriteLinesToFile File="{{ownerComplete}}" Lines="complete" Overwrite="true" />
                <OnError ExecuteTargets="ReleaseConsumer" />
              </Target>
              <Target Name="ReleaseConsumer">
                <WriteLinesToFile File="{{ownerComplete}}" Lines="failed" Overwrite="true" />
              </Target>
            </Project>
            """);
        var driver = scope.WriteAllText("SolutionOrder.proj", $$"""
            <Project>
              <ItemGroup><Participant Include="{{consumer}}" /><Participant Include="{{ownerDriver}}" /></ItemGroup>
              <Target Name="Run">
                <MSBuild Projects="{{dependency}}" Targets="Rebuild" Properties="BuildProjectReferences=false" />
                <MSBuild Projects="{{owner}}" Targets="Build" Condition="'{{buildOwnerFirst}}' == 'True'"
                         Properties="BuildProjectReferences=false;GeneratePackageOnBuild={{generatePackageOnBuild}};SunderPackageBuildSourceLockTargetsPath={{sourceLock}}" />
                <MSBuild Projects="@(Participant)" Targets="Probe" BuildInParallel="true"
                         Properties="BuildProjectReferences=false" />
              </Target>
            </Project>
            """);

        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", consumer, "--nologo"));
        AssertSuccess(await RunDotnetAsync(scope.RootPath, "restore", owner, "--nologo"));
        var result = await RunMsbuildAsync(driver, fixture, "Run", "-m:3", "-p:Configuration=Debug");

        AssertSuccess(result);
        Assert.Equal("complete", File.ReadAllText(ownerComplete).Trim());
        Assert.True(File.Exists(reference));
        Assert.True(File.Exists(Path.Combine(scope.RootPath, "Consumer/bin/Debug/net10.0/Consumer.dll")));
    }
}
