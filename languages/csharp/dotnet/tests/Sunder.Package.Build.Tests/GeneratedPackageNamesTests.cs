using System.Text.Json;
using System.Text.RegularExpressions;
using Sunder.Package.Build.Editor;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class GeneratedPackageNamesTests
{
    [Theory]
    [InlineData("younics.ai.agent", "Younics_Ai_Agent")]
    [InlineData("younics.ai.agent.provider.openai", "Younics_Ai_Agent_Provider_Openai")]
    [InlineData("younics.sunder.package.maker", "Younics_Sunder_Package_Maker")]
    [InlineData("1.2.a", "_1_2_A")]
    [InlineData("example.1a", "Example_1a")]
    [InlineData("class.namespace", "Class_Namespace")]
    [InlineData("example.services", "Example_Services")]
    [InlineData("a.bc", "A_Bc")]
    [InlineData("ab.c", "Ab_C")]
    public void NamesAreCanonicalFlattenedAndTemplateUsesTheSameInvariantEncoding(string packageId, string stem)
    {
        Assert.Equal("Sunder.Generated.Packages." + stem + "_Services", GeneratedPackageNames.Services(packageId));
        Assert.Equal("Sunder.Generated.Packages." + stem + "_Models", GeneratedPackageNames.Models(packageId));
        Assert.Equal("Sunder.Generated.Packages." + stem + "_Internal", GeneratedPackageNames.Internal(packageId));

        // Exercise the actual template configuration, not a second handwritten
        // test implementation of the namespace algorithm. Regex steps avoid
        // current-culture title casing (notably Turkish 'i').
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Sunder.Core.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        using var template = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root.FullName,
            "languages/csharp/dotnet/packages/Sunder.Package.Templates/templates/sunder-package/.template.config/template.json")));
        var result = packageId;
        foreach (var step in template.RootElement.GetProperty("symbols").GetProperty("packageNamespace").GetProperty("parameters").GetProperty("steps").EnumerateArray())
            result = Regex.Replace(result, step.GetProperty("regex").GetString()!, step.GetProperty("replacement").GetString()!, RegexOptions.CultureInvariant);
        Assert.Equal(stem, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Example.package")]
    [InlineData("example_package")]
    [InlineData("example..package")]
    [InlineData("example.package.")]
    [InlineData("éxample.package")]
    public void InvalidIdsCannotAliasValidNamespaces(string packageId)
        => Assert.Throws<ArgumentException>(() => GeneratedPackageNames.Services(packageId));

    [Theory]
    [InlineData("younics.ai.agent", "younics.ai.agent.runtime.catalog", "runtime-catalog", "Younics_Ai_Agent", "CYounics_Ai_Agent_Runtime_Catalog", "IRuntimeCatalogService")]
    [InlineData("example.dependency", "example.rpc.v1.minimal", "example", "Example_Dependency", "CExample_Rpc_V1_Minimal", "IExampleService")]
    [InlineData("1.2.a", "1a.2", "3-service", "_1_2_A", "C1a_2", "I_3Service")]
    [InlineData("example.dependency", "a.bc", "run-control", "Example_Dependency", "CA_Bc", "IRunControlService")]
    [InlineData("example.dependency", "ab.c", "run-control", "Example_Dependency", "CAb_C", "IRunControlService")]
    public void ImportedServicesAlwaysIncludeContractIdentityWhileOwnedServicesRemainFlat(string packageId, string contractId,
        string serviceId, string authority, string contract, string service)
    {
        Assert.Equal($"Sunder.Generated.Packages.{authority}_Services.{contract}.{service}",
            GeneratedPackageNames.ImportedServiceInterface(packageId, contractId, serviceId));
        Assert.Equal($"Sunder.Generated.Packages.{authority}_Services.{service}", GeneratedPackageNames.ServiceInterface(packageId, serviceId));
    }
}
