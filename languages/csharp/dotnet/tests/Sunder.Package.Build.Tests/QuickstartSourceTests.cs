using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class QuickstartSourceTests
{
    [Fact]
    public void QuickstartAuthorsCurrentPrivateServicesAtThePackageVersion()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var dotnet = Path.Combine(directory.FullName, "languages", "csharp", "dotnet");
        var sample = Path.Combine(dotnet, "samples", "Sunder.Package.Quickstart");
        Assert.False(File.Exists(Path.Combine(sample, "Sunder.Package.Quickstart.Protocol", "Sunder.Package.Quickstart.Protocol.csproj")));
        var project = Assert.Single(Directory.EnumerateFiles(sample, "*.csproj", SearchOption.AllDirectories), path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));
        var projectText = File.ReadAllText(project);
        Assert.DoesNotContain(".Protocol", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderPackageAppProject", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderPackagePrimaryProject", projectText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(sample, "Sunder.Package.Quickstart.Worker", "Program.cs")));
        Assert.Contains("IGreetingService", File.ReadAllText(Path.Combine(sample, "Views", "QuickstartViewModel.cs")), StringComparison.Ordinal);
        var version = System.Xml.Linq.XDocument.Load(project).Descendants("Version").Single().Value;
        Assert.Equal("1.1.0", version);
        var servicePath = Path.Combine(sample, "Services", "GreetingService.cs"); var source = File.ReadAllText(servicePath);
        Assert.Contains("[SunderService]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderRpc", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GreetRequest", source, StringComparison.Ordinal);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(SunderServiceAttribute).Assembly.Location).Distinct(StringComparer.Ordinal).ToArray();
        var result = new SunderRpcCSharpSourceCompiler().Compile(new("docs.sunder.quickstart", version,
            [new(servicePath, source), new("GlobalUsings.cs", "global using System; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;")], references));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var contract = Assert.Single(result.Contracts);
        Assert.Equal(SunderServiceExposure.Package, contract.Exposure);
        Assert.Equal(SunderServiceAudience.Owner, Assert.Single(contract.Providers).Audience);
        var current = contract.Descriptor;
        Assert.Equal("docs.sunder.quickstart.greeting", current.ContractId);
        Assert.Equal(version, current.Version);
        Assert.Equal(new[] { "greet", "greet-many", "reset" }, Assert.Single(current.Services).Methods.Select(method => method.MethodId).Order(StringComparer.Ordinal));
    }
}
