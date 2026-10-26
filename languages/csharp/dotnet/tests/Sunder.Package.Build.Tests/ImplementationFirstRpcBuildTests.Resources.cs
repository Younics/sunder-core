using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security;
using Sunder.Core.TestSupport;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class ImplementationFirstRpcBuildTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("Category", "Artifact")]
    [Trait("Capability", "Process")]
    public async Task SingleProject_CompiledXamlAndResourcesStayWithinTheirRole(bool immutableTasks)
    {
        await using var scope = TemporaryDirectoryScope.Create("sunder-role-xaml-resources");
        var root = ResolvePhysicalTemporaryPath(scope.CreateDirectory("package"));
        var linked = ResolvePhysicalTemporaryPath(scope.WriteAllText("linked/banner.txt", "app-only-asset"));
        var repository = FindRepositoryRoot(); var packages = Path.Combine(repository, "languages/csharp/dotnet/packages");
        var baseline = Path.Combine(repository, "languages/csharp/dotnet/tests/Sunder.Package.Build.TestFixtures/ImplementationFirstRpc");
        File.Copy(Path.Combine(baseline, "sunder-contracts.lock.json"), Path.Combine(root, "sunder-contracts.lock.json"));
        var source = SingleProjectSource.Replace("public sealed class GreetingView : Avalonia.Controls.UserControl;", """
            public sealed partial class GreetingView : Avalonia.Controls.UserControl
            {
                public GreetingView() { InitializeComponent(); }
                public string NamedText => Status.Text ?? "";
            }
            """, StringComparison.Ordinal)
            .Replace("public sealed class GreetingViewModel", "public sealed partial class GreetingViewModel", StringComparison.Ordinal)
            .Replace("GreetingViewModel(IGreetingService service)", "GreetingViewModel(IGreetingService service) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject", StringComparison.Ordinal)
            .Replace("public ValueTask<GreetingResult> ReadAsync()", """
                [System.Text.RegularExpressions.GeneratedRegex("^compiled binding$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, 1000)]
                private static partial System.Text.RegularExpressions.Regex AppPattern();
                [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
                private string message = AppPattern().IsMatch("compiled binding") ? "compiled binding" : "invalid";
                [CommunityToolkit.Mvvm.Input.RelayCommand]
                private void UpdateMessage() => Message = "updated binding";
                public ValueTask<GreetingResult> ReadAsync()
                """, StringComparison.Ordinal)
            .Replace("public sealed class BackendFormatter : IBackendFormatter { public string? OptionalName { get; set; } public string Format(string name) => name; }", """
                public sealed partial class BackendFormatter : IBackendFormatter
                {
                    [System.Text.RegularExpressions.GeneratedRegex("^[a-z]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, 1000)]
                    private static partial System.Text.RegularExpressions.Regex WorkerPattern();
                    public string Format(string name) => WorkerPattern().IsMatch(name) ? name : "invalid";
                }
                """, StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(root, "Mixed.cs"), source);
        foreach (var role in new[] { "App", "Worker", "Shared" }) await WriteEditAsync(role, "initial");
        var xaml = Path.Combine(root, "GreetingView.axaml");
        await File.WriteAllTextAsync(xaml, """
            <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="using:Example.Generated" x:Class="Example.Generated.GreetingView"
                         x:DataType="local:GreetingViewModel" x:CompileBindings="True">
                <TextBlock x:Name="Status" Text="{Binding Message}" />
            </UserControl>
            """);
        foreach (var (name, contents) in new[] { ("app.data", "embedded-app"), ("worker.data", "embedded-worker"), ("app.json", "app-copy"), ("worker.json", "worker-copy") })
            await File.WriteAllTextAsync(Path.Combine(root, name), contents);
        var taskAssembly = typeof(GenerateSunderRpcBindingsTask).Assembly.Location;
        var references = new[] { "Sunder.Sdk", "Sunder.Sdk.Worker", "Sunder.Sdk.Avalonia" };
        var project = Path.Combine(root, "ResourceRoles.csproj");
        await File.WriteAllTextAsync(project, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>
                <Version>7.6.5</Version><SunderPackageProject>true</SunderPackageProject><NuGetAudit>false</NuGetAudit>
                <RuntimeIdentifiers>{{RuntimeInformation.RuntimeIdentifier}}</RuntimeIdentifiers><SelfContained>false</SelfContained><UseAppHost>false</UseAppHost>
                <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
                <SunderPackageBuildTaskAssembly>{{SecurityElement.Escape(taskAssembly)}}</SunderPackageBuildTaskAssembly>
                <SunderPackageBuildTaskSourceIsImmutable>{{immutableTasks.ToString().ToLowerInvariant()}}</SunderPackageBuildTaskSourceIsImmutable>
              </PropertyGroup>
              <ItemGroup>
                {{string.Join(Environment.NewLine, references.Select(name => $"<Reference Include=\"{name}\" HintPath=\"{SecurityElement.Escape(Path.Combine(Path.GetDirectoryName(taskAssembly)!, name + ".dll"))}\" Private=\"true\" />"))}}
                <PackageReference Include="Avalonia" Version="12.1.4" />
                <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
                <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.13" />
                <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.13" />
                <AvaloniaResource Include="{{SecurityElement.Escape(linked)}}" Link="assets\banner.txt" />
                <EmbeddedResource Include="app.data" LogicalName="fixture.app" SunderPackageAssetRole="app" />
                <EmbeddedResource Include="worker.data" LogicalName="fixture.worker" SunderPackageAssetRole="worker" />
                <None Update="app.json" TargetPath="data/app.json" CopyToOutputDirectory="PreserveNewest" SunderPackageAssetRole="app" />
                <None Update="worker.json" TargetPath="data/worker.json" CopyToOutputDirectory="PreserveNewest" SunderPackageAssetRole="worker" />
              </ItemGroup>
              <Import Project="{{SecurityElement.Escape(Path.Combine(packages, "Sunder.Package.Build/build/Sunder.Package.Build.targets"))}}" />
            </Project>
            """);
        AssertSuccess(await RunDotnetAsync(root, ["restore", project, "--disable-parallel", "--nologo"]));
        AssertSuccess(await Build());
        var worker = Path.Combine(root, "bin/Debug/net10.0/sunder-roles/worker", RuntimeInformation.RuntimeIdentifier);
        var app = Path.Combine(root, "bin/Debug/net10.0/sunder-roles/app", RuntimeInformation.RuntimeIdentifier);
        Assert.Equal(new[] { "fixture.worker" }, ResourceNames(Path.Combine(worker, "ResourceRoles.dll")));
        var appResources = ResourceNames(Path.Combine(app, "ResourceRoles.App.dll"));
        Assert.Contains("!AvaloniaResources", appResources); Assert.Contains("fixture.app", appResources); Assert.DoesNotContain("fixture.worker", appResources);
        Assert.True(File.Exists(Path.Combine(app, "data/app.json"))); Assert.False(File.Exists(Path.Combine(app, "data/worker.json")));
        Assert.True(File.Exists(Path.Combine(worker, "data/worker.json"))); Assert.False(File.Exists(Path.Combine(worker, "data/app.json")));
        AssertRegexRole(Path.Combine(worker, "ResourceRoles.dll"), "WorkerPattern", "AppPattern");
        AssertRegexRole(Path.Combine(app, "ResourceRoles.App.dll"), "AppPattern", "WorkerPattern");
        Assert.DoesNotContain("CommunityToolkit.Mvvm", AssemblyReferences(Path.Combine(worker, "ResourceRoles.dll")));
        await ProbeXamlAsync("initial");
        var dev = Path.Combine(root, "bin/Debug/net10.0/sunder-dev"); var before = HashTree(dev);
        var originalXaml = await File.ReadAllTextAsync(xaml);
        var timestamps = new[] { File.GetLastWriteTimeUtc(Path.Combine(app, "ResourceRoles.App.dll")), File.GetLastWriteTimeUtc(Path.Combine(worker, "ResourceRoles.dll")) };
        AssertSuccess(await Build());
        Assert.Equal(timestamps, new[] { File.GetLastWriteTimeUtc(Path.Combine(app, "ResourceRoles.App.dll")), File.GetLastWriteTimeUtc(Path.Combine(worker, "ResourceRoles.dll")) });
        var appAssembly = Path.Combine(app, "ResourceRoles.App.dll");
        var workerAssembly = Path.Combine(worker, "ResourceRoles.dll");
        foreach (var role in new[] { "App", "Worker", "Shared", "AXAML" })
        {
            var oldApp = await File.ReadAllBytesAsync(appAssembly);
            var oldWorker = await File.ReadAllBytesAsync(workerAssembly);
            var oldAppTime = File.GetLastWriteTimeUtc(appAssembly);
            var oldWorkerTime = File.GetLastWriteTimeUtc(workerAssembly);
            if (role == "AXAML")
                await File.WriteAllTextAsync(xaml, originalXaml.Replace("x:Name=\"Status\"", "x:Name=\"Status\" Margin=\"1\"", StringComparison.Ordinal));
            else await WriteEditAsync(role, "changed");
            AssertSuccess(await Build());
            var newApp = await File.ReadAllBytesAsync(appAssembly);
            var newWorker = await File.ReadAllBytesAsync(workerAssembly);
            if (role == "Worker")
            {
                Assert.Equal(oldApp, newApp);
                Assert.Equal(oldAppTime, File.GetLastWriteTimeUtc(appAssembly));
            }
            else Assert.False(oldApp.AsSpan().SequenceEqual(newApp), role + " edit did not change the App assembly.");
            if (role is "App" or "AXAML")
            {
                Assert.Equal(oldWorker, newWorker);
                Assert.Equal(oldWorkerTime, File.GetLastWriteTimeUtc(workerAssembly));
            }
            else Assert.False(oldWorker.AsSpan().SequenceEqual(newWorker), role + " edit did not change the Worker assembly.");
        }
        await ProbeXamlAsync("changed");
        before = HashTree(dev);
        originalXaml = await File.ReadAllTextAsync(xaml);
        await File.WriteAllTextAsync(Path.Combine(root, "Mixed.cs"), source.Replace("^compiled binding$", "[", StringComparison.Ordinal));
        var invalidRegex = await Build(); Assert.NotEqual(0, invalidRegex.ExitCode);
        Assert.Equal(before, HashTree(dev));
        await File.WriteAllTextAsync(Path.Combine(root, "Mixed.cs"), source);
        await File.WriteAllTextAsync(Path.Combine(root, "Mixed.cs"), source.Replace("private void UpdateMessage()", "private string UpdateMessage()", StringComparison.Ordinal));
        var invalidCommand = await Build(); Assert.NotEqual(0, invalidCommand.ExitCode);
        Assert.Contains("MVVMTK", invalidCommand.StandardOutput + invalidCommand.StandardError, StringComparison.Ordinal);
        Assert.Equal(before, HashTree(dev));
        await File.WriteAllTextAsync(Path.Combine(root, "Mixed.cs"), source);
        var originalProject = await File.ReadAllTextAsync(project);
        await File.WriteAllTextAsync(project, originalProject.Replace("LogicalName=\"fixture.worker\" SunderPackageAssetRole=\"worker\"", "LogicalName=\"fixture.worker\"", StringComparison.Ordinal));
        var ambiguous = await Build(); Assert.NotEqual(0, ambiguous.ExitCode);
        Assert.Contains("resource-role-ambiguous", ambiguous.StandardOutput + ambiguous.StandardError, StringComparison.Ordinal);
        Assert.Equal(before, HashTree(dev));
        await File.WriteAllTextAsync(project, originalProject);
        await File.WriteAllTextAsync(xaml, originalXaml.Replace("x:Name=\"Status\"", "x:Name=\"Renamed\"", StringComparison.Ordinal));
        var renamed = await Build(); Assert.NotEqual(0, renamed.ExitCode);
        Assert.Contains("Status", renamed.StandardOutput + renamed.StandardError, StringComparison.Ordinal);
        Assert.Equal(before, HashTree(dev));
        await File.WriteAllTextAsync(xaml, originalXaml.Replace("{Binding Message}", "{Binding MissingProperty}", StringComparison.Ordinal));
        var invalid = await Build(); Assert.NotEqual(0, invalid.ExitCode);
        Assert.Contains("MissingProperty", invalid.StandardOutput + invalid.StandardError, StringComparison.Ordinal);
        Assert.Equal(before, HashTree(dev));
        await File.WriteAllTextAsync(xaml, originalXaml);
        AssertSuccess(await RunDotnetAsync(root, ["msbuild", project, "-t:PackSunderPackage", "-nodeReuse:false", "-nologo", "-v:quiet"]));
        var inspection = await SunderPackageArchiveInspector.ExtractAndValidateAsync(Path.Combine(root, "bin/Debug/net10.0/ResourceRoles.7.6.5.sunderpkg"), scope.GetPath("extracted"));
        Assert.True(inspection.Success, string.Join(Environment.NewLine, inspection.Errors));
        Assert.True(File.Exists(scope.GetPath("extracted", "payload", "app", "shared", "lib/data/app.json")));
        Assert.False(File.Exists(scope.GetPath("extracted", "payload", "app", "shared", "lib/data/worker.json")));
        Assert.True(File.Exists(scope.GetPath("extracted", "payload", "runtime", "shared", "lib/data/worker.json")));

        Task<ProcessResult> Build() => RunDotnetAsync(root, ["build", project, "--no-restore", "--disable-build-servers", "--nologo", "-v:quiet"]);

        Task WriteEditAsync(string role, string value) => File.WriteAllTextAsync(Path.Combine(root, role + "Edit.cs"), $$"""
            using Sunder.Sdk.Packaging;
            namespace Example.Generated;
            [SunderPackageCodeRole(SunderPackageCodeRole.{{role}})]
            public static class {{role}}Edit { public static string Value => "{{value}}"; }
            """);

        static void AssertRegexRole(string path, string present, string absent)
        {
            using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var methods = metadata.MethodDefinitions.Select(handle => metadata.GetString(metadata.GetMethodDefinition(handle).Name)).ToArray();
            Assert.Contains(present, methods); Assert.DoesNotContain(absent, methods);
        }

        static string[] AssemblyReferences(string path)
        {
            using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            return metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
        }

        async Task ProbeXamlAsync(string expectedEdit)
        {
            var probe = scope.CreateDirectory("probe"); var probeProject = Path.Combine(probe, "Probe.csproj");
            await File.WriteAllTextAsync(probeProject, $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><NuGetAudit>false</NuGetAudit></PropertyGroup>
                  <ItemGroup>
                <PackageReference Include="Avalonia.Headless" Version="12.1.4" />
                <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
                    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.13" />
                    <Reference Include="ResourceRoles.App" HintPath="{{SecurityElement.Escape(Path.Combine(app, "ResourceRoles.App.dll"))}}" />
                    <Reference Include="ResourceRoles" HintPath="{{SecurityElement.Escape(Path.Combine(worker, "ResourceRoles.dll"))}}" Aliases="worker" />
                    {{string.Join(Environment.NewLine, new[] { "Sunder.Sdk", "Sunder.Sdk.Avalonia", "Microsoft.Extensions.DependencyInjection.Abstractions" }
                        .Select(name => $"<Reference Include=\"{name}\" HintPath=\"{SecurityElement.Escape(Path.Combine(Path.GetDirectoryName(taskAssembly)!, name + ".dll"))}\" />"))}}
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(probe, "Program.cs"), $$"""
                extern alias worker;
                using Avalonia;
                using Avalonia.Headless;
                using Avalonia.Platform;
                using Avalonia.Threading;
                using Microsoft.Extensions.DependencyInjection;
                using Sunder.Sdk.Abstractions;
                using Sunder.Sdk.Rpc;
                using System.Reflection;
                const string expectedEdit = {{System.Text.Json.JsonSerializer.Serialize(expectedEdit)}};
                if (Example.Generated.AppEdit.Value != expectedEdit || worker::Example.Generated.WorkerEdit.Value != expectedEdit
                    || Example.Generated.SharedEdit.Value != expectedEdit || worker::Example.Generated.SharedEdit.Value != expectedEdit)
                    throw new Exception("A role-local/shared C# edit did not reach its executable output.");
                var formatter = new worker::Example.Generated.BackendFormatter();
                if (formatter.Format("activated") != "activated" || formatter.Format("bad1") != "invalid") throw new Exception("The Worker regex did not execute.");
                AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true }).SetupWithoutStarting();
                var context = DispatchProxy.Create<IPackageContext, RegistrationOnlyHost>();
                var services = new ServiceCollection();
                new Sunder.Generated.Package.AppModule().ConfigureAppServices(services, context);
                if (services.Any(service => service.ServiceType == typeof(IPackageContext) || service.ServiceType == typeof(ISunderRpcClient)))
                    throw new Exception("Generated App composition registered a reserved host capability.");
                services.AddSingleton(context);
                services.AddSingleton(DispatchProxy.Create<ISunderRpcClient, RegistrationOnlyHost>());
                await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
                var view = provider.GetRequiredService<Example.Generated.GreetingView>();
                Dispatcher.UIThread.RunJobs();
                if (view.NamedText != "compiled binding") throw new Exception("The compiled binding/named control did not load.");
                var model = (Example.Generated.GreetingViewModel)view.DataContext!;
                var changes = 0;
                model.PropertyChanged += (_, args) => { if (args.PropertyName == "Message") changes++; };
                model.UpdateMessageCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                if (view.NamedText != "updated binding" || changes != 1) throw new Exception("Generated MVVM command/property binding did not execute.");
                using var asset = new StreamReader(AssetLoader.Open(new Uri("avares://ResourceRoles.App/assets/banner.txt")));
                if (asset.ReadToEnd() != "app-only-asset") throw new Exception("The linked resource URI changed.");
                using var embedded = new StreamReader(typeof(Example.Generated.GreetingView).Assembly.GetManifestResourceStream("fixture.app")!);
                if (embedded.ReadToEnd() != "embedded-app") throw new Exception("The App embedded resource did not load.");
                if (typeof(Example.Generated.GreetingView).Assembly.GetManifestResourceStream("fixture.worker") is not null) throw new Exception("Worker resource leaked into App.");
                public class RegistrationOnlyHost : DispatchProxy
                {
                    protected override object? Invoke(MethodInfo? method, object?[]? args)
                        => throw new InvalidOperationException("View creation must not perform Host I/O.");
                }
                """);
            AssertSuccess(await RunDotnetAsync(probe, ["restore", probeProject, "--disable-parallel", "--nologo"]));
            AssertSuccess(await RunDotnetAsync(probe, ["run", "--project", probeProject, "--no-restore", "--no-launch-profile", "-v:quiet"]));
        }
    }

    private static string[] ResourceNames(string path)
    {
        using var stream = File.OpenRead(path); using var reader = new PEReader(stream);
        var metadata = reader.GetMetadataReader();
        return metadata.ManifestResources.Select(handle => metadata.GetString(metadata.GetManifestResource(handle).Name)).Order(StringComparer.Ordinal).ToArray();
    }
}
