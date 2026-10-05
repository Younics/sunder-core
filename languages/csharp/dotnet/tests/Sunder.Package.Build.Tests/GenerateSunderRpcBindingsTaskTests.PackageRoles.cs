using System.Text.Json;
using System.Diagnostics;
using System.Security;
using Avalonia.Controls;
using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Sdk.Avalonia;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class GenerateSunderRpcBindingsTaskTests
{
    [Fact]
    public void Execute_PublishesCompleteRoleInputsAndPreservesThemAfterInvalidRoleChanges()
    {
        var root = CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(root, "Package.cs");
            var references = RoleReferences();
            var original = Assert.Single(new SunderRpcCSharpSourceCompiler().Compile(new("", "7.6.5", [new(sourcePath, ImplementationFirstSource)], references)).Contracts).Descriptor;
            WriteSourceAuthoringState(root, original);
            File.WriteAllText(sourcePath, ImplementationFirstSource + RoleView);
            var task = CreateSourceTask(root, Path.Combine(root, "obj"), sourcePath, references);
            task.GeneratePackageRoles = true;
            Assert.True(task.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)task.BuildEngine).Errors));
            var plan = Assert.Single(task.PackageRolePlans);
            using var json = JsonDocument.Parse(File.ReadAllBytes(plan.ItemSpec));
            Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.True(json.RootElement.GetProperty("hasApp").GetBoolean());
            Assert.NotEmpty(task.WorkerCompileItems); Assert.NotEmpty(task.AppCompileItems);
            Assert.Contains(task.WorkerCompileItems, item => item.ItemSpec.EndsWith("worker-entry.g.cs", StringComparison.Ordinal));
            Assert.Contains(task.AppCompileItems, item => item.ItemSpec.EndsWith("app-module.g.cs", StringComparison.Ordinal));
            Assert.DoesNotContain(task.AppCompileItems, item => item.ItemSpec.EndsWith("authoring.g.cs", StringComparison.Ordinal));
            Assert.All(task.WorkerCompileItems.Concat(task.AppCompileItems), item => Assert.True(File.Exists(item.ItemSpec)));
            var before = ReadOwnedSet(Path.Combine(root, "obj/sunder-rpc"));
            var timestamps = ReadOwnedTimestamps(Path.Combine(root, "obj/sunder-rpc"));
            Assert.True(task.Execute()); Assert.Equal(before, ReadOwnedSet(Path.Combine(root, "obj/sunder-rpc")));
            Assert.Equal(timestamps, ReadOwnedTimestamps(Path.Combine(root, "obj/sunder-rpc")));
            File.WriteAllText(sourcePath, ImplementationFirstSource + RoleView.Replace("IGreetingService service", "GreetingService service", StringComparison.Ordinal)
                .Replace("service.GreetAsync(new() { Name = \"hello\" }, default)", "service.GreetAsync(new() { Name = \"hello\" }, null!, default)", StringComparison.Ordinal));
            Assert.False(task.Execute()); Assert.Empty(task.PackageRolePlans); Assert.Empty(task.WorkerCompileItems); Assert.Empty(task.AppCompileItems);
            Assert.Contains(((TestBuildEngine)task.BuildEngine).Errors, error => error.Contains("role-conflict", StringComparison.Ordinal));
            Assert.Equal(before, ReadOwnedSet(Path.Combine(root, "obj/sunder-rpc")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Execute_RemovingUiRootsRemovesStaleGeneratedAppOutput()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "Package.cs"); var references = RoleReferences();
            var descriptor = Assert.Single(new SunderRpcCSharpSourceCompiler().Compile(new("", "7.6.5", [new(path, ImplementationFirstSource)], references)).Contracts).Descriptor;
            WriteSourceAuthoringState(root, descriptor);
            File.WriteAllText(path, ImplementationFirstSource + RoleView);
            var task = CreateSourceTask(root, Path.Combine(root, "obj"), path, references); task.GeneratePackageRoles = true;
            Assert.True(task.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)task.BuildEngine).Errors));
            var oldAppFiles = task.AppCompileItems.Where(item => item.ItemSpec.Contains("/roles/app/", StringComparison.Ordinal)).Select(item => item.ItemSpec).ToArray();
            Assert.NotEmpty(oldAppFiles);
            File.WriteAllText(path, ImplementationFirstSource);
            Assert.True(task.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)task.BuildEngine).Errors));
            Assert.Empty(task.AppCompileItems); Assert.NotEmpty(task.WorkerCompileItems);
            Assert.All(oldAppFiles, path => Assert.False(File.Exists(path)));
            using var plan = JsonDocument.Parse(File.ReadAllBytes(Assert.Single(task.PackageRolePlans).ItemSpec));
            Assert.False(plan.RootElement.GetProperty("hasApp").GetBoolean());
            Assert.Equal(descriptor.Sha256, Assert.Single(task.BindingSnapshots).GetMetadata("DescriptorSha256"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ReadRoleInputs_RequiresBuildLifetimeLeaseAndRejectsChangedGeneratedSources()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "Package.cs"); var references = RoleReferences();
            var descriptor = Assert.Single(new SunderRpcCSharpSourceCompiler().Compile(new("", "7.6.5", [new(path, ImplementationFirstSource)], references)).Contracts).Descriptor;
            WriteSourceAuthoringState(root, descriptor); File.WriteAllText(path, ImplementationFirstSource + RoleView);
            var task = CreateSourceTask(root, Path.Combine(root, "obj"), path, references); task.GeneratePackageRoles = true;
            Assert.True(task.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)task.BuildEngine).Errors));
            var plan = Assert.Single(task.PackageRolePlans);
            var denied = new ReadSunderPackageRoleInputsTask
            {
                BuildEngine = new TestBuildEngine(), PlanPath = plan.ItemSpec, ExpectedSha256 = plan.GetMetadata("Sha256"),
                ProjectIdentity = "ImplementationFirstRpc", Role = "app",
            };
            Assert.False(denied.Execute()); Assert.Empty(denied.CompileItems);
            var assembly = SecurityElement.Escape(typeof(ReadSunderPackageRoleInputsTask).Assembly.Location);
            File.WriteAllText(Path.Combine(root, "Read.proj"), $$"""
                <Project DefaultTargets="Read">
                  <UsingTask TaskName="Sunder.Package.Build.Tasks.ManageSunderRpcGeneratedOutputLockTask" AssemblyFile="{{assembly}}" />
                  <UsingTask TaskName="Sunder.Package.Build.Tasks.ReadSunderPackageRoleInputsTask" AssemblyFile="{{assembly}}" />
                  <ItemGroup><_Generated Include="{{SecurityElement.Escape(Path.GetDirectoryName(plan.ItemSpec)!)}}" DiscoveryRoot="{{SecurityElement.Escape(Path.Combine(root, "obj"))}}" /></ItemGroup>
                  <Target Name="Read">
                    <ManageSunderRpcGeneratedOutputLockTask Action="Acquire" ProjectDirectory="$(MSBuildProjectDirectory)" OutputDirectories="@(_Generated)">
                      <Output TaskParameter="LeaseId" PropertyName="_Lease" />
                    </ManageSunderRpcGeneratedOutputLockTask>
                    <ReadSunderPackageRoleInputsTask PlanPath="{{SecurityElement.Escape(plan.ItemSpec)}}" ExpectedSha256="{{plan.GetMetadata("Sha256")}}"
                        ProjectIdentity="ImplementationFirstRpc" Role="app" OutputLockLeaseId="$(_Lease)">
                      <Output TaskParameter="CompileItems" ItemName="_Compile" />
                      <Output TaskParameter="AssemblyName" PropertyName="_Assembly" />
                    </ReadSunderPackageRoleInputsTask>
                    <Error Condition="'@(_Compile)' == '' or '$(_Assembly)' != 'Sunder.Rpc.Authoring.App'" Text="Role inputs were not returned." />
                    <ManageSunderRpcGeneratedOutputLockTask Action="Release" ProjectDirectory="$(MSBuildProjectDirectory)" LeaseId="$(_Lease)" />
                  </Target>
                </Project>
                """);
            var success = await Run(); Assert.Equal(0, success.Exit);
            File.AppendAllText(task.AppCompileItems[0].ItemSpec, "\n// changed after analysis\n");
            var tampered = await Run(); Assert.NotEqual(0, tampered.Exit);
            Assert.Contains("package-authoring.role-plan-invalid", tampered.Output, StringComparison.Ordinal);
            Assert.Contains("changed after package analysis", tampered.Output, StringComparison.Ordinal);

            async Task<(int Exit, string Output)> Run()
            {
                var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var value in new[] { "msbuild", "Read.proj", "-nodeReuse:false", "-nologo", "-v:quiet" }) start.ArgumentList.Add(value);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
                return (process.ExitCode, await output + await error);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string[] RoleReferences() => CompilationReferences().Concat([
        typeof(Control).Assembly.Location, typeof(Avalonia.AvaloniaObject).Assembly.Location,
        typeof(IAvaloniaPackageContributionRegistry).Assembly.Location,
        typeof(Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions).Assembly.Location,
    ]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    [Fact]
    public void Execute_RoleHandoffIsImmutableAcrossRegenerationAndMutableOutputClean()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "Package.cs"); var references = RoleReferences();
            var descriptor = Assert.Single(new SunderRpcCSharpSourceCompiler().Compile(new("", "7.6.5", [new(path, ImplementationFirstSource)], references)).Contracts).Descriptor;
            WriteSourceAuthoringState(root, descriptor); File.WriteAllText(path, ImplementationFirstSource + RoleView);
            var task = CreateSourceTask(root, Path.Combine(root, "obj"), path, references); task.GeneratePackageRoles = true;
            Assert.True(task.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)task.BuildEngine).Errors));
            var first = Assert.Single(task.PackageRolePlans).GetMetadata("SnapshotPath");
            var contents = File.ReadAllBytes(first); var timestamp = File.GetLastWriteTimeUtc(first);
            Assert.True(task.Execute()); Assert.Equal(first, Assert.Single(task.PackageRolePlans).GetMetadata("SnapshotPath"));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(first));
            File.WriteAllText(path, ImplementationFirstSource);
            Assert.True(task.Execute(), string.Join(Environment.NewLine, ((TestBuildEngine)task.BuildEngine).Errors));
            var second = Assert.Single(task.PackageRolePlans).GetMetadata("SnapshotPath");
            Assert.NotEqual(first, second); Assert.Equal(contents, File.ReadAllBytes(first));
            var directory = new Microsoft.Build.Utilities.TaskItem(Path.Combine(root, "obj/sunder-rpc"));
            directory.SetMetadata("DiscoveryRoot", Path.Combine(root, "obj"));
            var clean = new CleanSunderRpcGeneratedOutputTask { BuildEngine = new TestBuildEngine(), ProjectDirectory = root, GeneratedDirectories = [directory] };
            Assert.True(clean.Execute()); Assert.False(Directory.Exists(directory.ItemSpec));
            Assert.Equal(contents, File.ReadAllBytes(first)); Assert.True(File.Exists(second));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private const string RoleView = """

        [SunderPackageView("example.generated.view", "Generated", ViewModel = typeof(GreetingViewModel))]
        public sealed class GreetingView : Avalonia.Controls.UserControl;
        public sealed class GreetingViewModel(IGreetingService service)
        {
            public ValueTask<GreetingResult> ReadAsync() => service.GreetAsync(new() { Name = "hello" }, default);
        }
        """;
}
