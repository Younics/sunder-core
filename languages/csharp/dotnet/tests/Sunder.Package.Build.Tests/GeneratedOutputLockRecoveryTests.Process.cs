using System.Diagnostics;
using System.Security;
using Sunder.Package.Build.Tasks;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class GeneratedOutputLockRecoveryTests
{
    [Fact]
    [Trait("Capability", "Process")]
    public async Task DotNetHolderInDifferentWorkerTempRootRemainsExclusiveAndRecoversAfterWorkerCleanup()
    {
        Directory.CreateDirectory(_root);
        var discovery = Path.Combine(_root, "discovery");
        var output = Path.Combine(discovery, "output");
        var jobTemp = Path.Combine(_root, "worker-temp");
        var ready = Path.Combine(_root, "ready");
        var release = Path.Combine(_root, "release");
        var project = Path.Combine(_root, "lock-owner.proj");
        Directory.CreateDirectory(jobTemp);
        // Execute the real task in another .NET process; this project has no SDK,
        // producer build, restore, package generation, or shared build outputs.
        File.WriteAllText(project, $$"""
            <Project>
              <UsingTask TaskName="Sunder.Package.Build.Tasks.ManageSunderRpcGeneratedOutputLockTask"
                         AssemblyFile="{{Xml(typeof(ManageSunderRpcGeneratedOutputLockTask).Assembly.Location)}}" />
              <UsingTask TaskName="HoldUntilReleased" TaskFactory="RoslynCodeTaskFactory"
                         AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
                <ParameterGroup><Ready ParameterType="System.String" Required="true" /><Release ParameterType="System.String" Required="true" /></ParameterGroup>
                <Task><Code Type="Fragment" Language="cs"><![CDATA[
                  System.IO.File.WriteAllText(Ready, "ready");
                  while (!System.IO.File.Exists(Release)) System.Threading.Thread.Sleep(20);
                ]]></Code></Task>
              </UsingTask>
              <Target Name="Hold">
                <ItemGroup><LockOutput Include="{{Xml(output)}}"><DiscoveryRoot>{{Xml(discovery)}}</DiscoveryRoot></LockOutput></ItemGroup>
                <ManageSunderRpcGeneratedOutputLockTask Action="Acquire" ProjectDirectory="{{Xml(_root)}}" OutputDirectories="@(LockOutput)" />
                <HoldUntilReleased Ready="{{Xml(ready)}}" Release="{{Xml(release)}}" />
              </Target>
            </Project>
            """);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "msbuild", project, "-t:Hold", "-nr:false", "-nologo", "-v:minimal" }) start.ArgumentList.Add(argument);
        foreach (var variable in new[] { "TMPDIR", "TMP", "TEMP" }) start.Environment[variable] = jobTemp;
        using var child = Process.Start(start) ?? throw new IOException("Could not start the isolated lock owner.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(ready))
            {
                if (child.HasExited) throw new IOException($"Lock owner exited: {await stdout}\n{await stderr}");
                if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Isolated lock owner did not become ready.");
                await Task.Delay(25);
            }
            var owner = ReadOwner(output);
            var marker = owner["processMarkerPath"]!.GetValue<string>();
            _markers.Add(marker);
            Assert.False(GeneratedOutputPathSafety.IsDescendantOf(marker, jobTemp));
            var before = File.ReadAllBytes(Path.Combine(output + GeneratedOutputLock.LockSuffix, "owner.json"));
            Assert.Throws<TimeoutException>(() => GeneratedOutputLock.Acquire([output], TimeSpan.FromMilliseconds(1400)));
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(output + GeneratedOutputLock.LockSuffix, "owner.json")));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(jobTemp, recursive: true);
            Assert.True(File.Exists(marker));
            using (GeneratedOutputLock.Acquire([GeneratedOutputLock.TargetLeafDiscoveryKey(discovery, output)], [output], TimeSpan.FromSeconds(5))) { }
            Assert.False(Directory.Exists(output + GeneratedOutputLock.LockSuffix));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static string Xml(string value) => SecurityElement.Escape(value)!;
}
