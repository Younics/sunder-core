using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json.Nodes;
using Sunder.Package.Build.Tasks;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class GeneratedOutputLockRecoveryTests
{
    [UnixTheory]
    [InlineData("fresh")]
    [InlineData("existing-safe")]
    [InlineData("writable-ancestor")]
    [InlineData("public-root")]
    [InlineData("symlink-ancestor")]
    [Trait("Capability", "Process")]
    [UnsupportedOSPlatform("windows")]
    public async Task FreshUserProfileWithGroupWritableUmaskCreatesPrivateAncestorsWithoutAdoptingUnsafePaths(string scenario)
    {
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        const UnixFileMode readableMode = privateMode | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                                     | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        var home = GeneratedOutputPathSafety.NormalizeTrustedSystemAliases(Path.Combine(_root, "home"));
        Directory.CreateDirectory(home, privateMode);
        var sunder = Path.Combine(home, ".sunder");
        var generated = Path.Combine(sunder, "generated-output");
        var markerRoot = Path.Combine(generated, "processes");
        var sentinel = Path.Combine(sunder, "sentinel");
        if (scenario == "symlink-ancestor")
        {
            var foreign = Path.Combine(_root, "foreign");
            Directory.CreateDirectory(foreign, privateMode);
            Directory.CreateSymbolicLink(sunder, foreign);
        }
        else if (scenario != "fresh")
        {
            Directory.CreateDirectory(sunder, privateMode);
            File.SetUnixFileMode(sunder, scenario == "writable-ancestor" ? readableMode | UnixFileMode.GroupWrite : readableMode);
            if (scenario != "writable-ancestor")
            {
                Directory.CreateDirectory(generated, privateMode);
                File.SetUnixFileMode(generated, readableMode);
                Directory.CreateDirectory(markerRoot, privateMode);
                if (scenario == "public-root") File.SetUnixFileMode(markerRoot, readableMode);
            }
        }
        if (scenario != "fresh") File.WriteAllText(sentinel, "preserve existing state");
        var output = Path.Combine(_root, "output");
        var ownerCopy = Path.Combine(_root, "acquired-owner.json");
        var project = Path.Combine(_root, "private-marker.proj");
        // Exercise a new process and real lock task, without restore or producer
        // builds. The test process's cached marker and actual home are untouched.
        File.WriteAllText(project, $$"""
            <Project>
              <UsingTask TaskName="Sunder.Package.Build.Tasks.ManageSunderRpcGeneratedOutputLockTask"
                         AssemblyFile="{{Xml(typeof(ManageSunderRpcGeneratedOutputLockTask).Assembly.Location)}}" />
              <Target Name="Check">
                <ItemGroup><LockOutput Include="{{Xml(output)}}"><DiscoveryRoot>{{Xml(_root)}}</DiscoveryRoot></LockOutput></ItemGroup>
                <ManageSunderRpcGeneratedOutputLockTask Action="Acquire" ProjectDirectory="{{Xml(_root)}}" OutputDirectories="@(LockOutput)">
                  <Output TaskParameter="LeaseId" PropertyName="Lease" />
                </ManageSunderRpcGeneratedOutputLockTask>
                <Copy SourceFiles="{{Xml(Path.Combine(output + GeneratedOutputLock.LockSuffix, "owner.json"))}}" DestinationFiles="{{Xml(ownerCopy)}}" />
                <ManageSunderRpcGeneratedOutputLockTask Action="Release" ProjectDirectory="{{Xml(_root)}}" LeaseId="$(Lease)" />
              </Target>
            </Project>
            """);
        var start = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-c", "umask 0002; exec \"$@\"", "_",
                     Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", "msbuild", project,
                     "-t:Check", "-nr:false", "-nologo", "-v:minimal" }) start.ArgumentList.Add(argument);
        start.Environment["HOME"] = home;
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(_root, "dotnet-cli");
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        using var child = Process.Start(start) ?? throw new IOException("Could not start the fresh-profile lock owner.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            var diagnostics = await stdout + "\n" + await stderr;
            if (scenario is "fresh" or "existing-safe")
            {
                Assert.True(child.ExitCode == 0, diagnostics);
                var owner = JsonNode.Parse(File.ReadAllBytes(ownerCopy))!;
                Assert.Equal(Path.Combine(markerRoot, owner["processMarkerToken"]!.GetValue<string>() + ".json"),
                    owner["processMarkerPath"]!.GetValue<string>());
                Assert.Equal(scenario == "fresh" ? privateMode : readableMode, File.GetUnixFileMode(sunder));
                Assert.Equal(scenario == "fresh" ? privateMode : readableMode, File.GetUnixFileMode(generated));
                Assert.Equal(privateMode, File.GetUnixFileMode(markerRoot));
                Assert.False(Directory.Exists(output + GeneratedOutputLock.LockSuffix));
            }
            else
            {
                Assert.NotEqual(0, child.ExitCode);
                Assert.False(File.Exists(ownerCopy));
                if (scenario == "writable-ancestor")
                {
                    Assert.Contains("must not be writable by other users", diagnostics);
                    Assert.Equal(readableMode | UnixFileMode.GroupWrite, File.GetUnixFileMode(sunder));
                }
                else if (scenario == "public-root")
                {
                    Assert.Contains("must be private to the current user", diagnostics);
                    Assert.Equal(readableMode, File.GetUnixFileMode(markerRoot));
                }
                else
                {
                    Assert.Contains("process marker ancestor", diagnostics);
                    Assert.NotNull(new DirectoryInfo(sunder).LinkTarget);
                    Assert.Equal(["sentinel"], Directory.GetFileSystemEntries(Path.Combine(_root, "foreign")).Select(Path.GetFileName));
                }
            }
            if (scenario != "fresh") Assert.Equal("preserve existing state", File.ReadAllText(sentinel));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

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
