using Sunder.Package.Format;
using Xunit;

namespace Sunder.Package.Format.Tests;

public sealed class PackageInformationTests
{
    [Fact]
    public void PublicInventorySeparatesImplementationsDeclarationsAndImportedDefinitions()
    {
        var manifest = new SunderPackageManifest
        {
            ContractBundles = [Bundle("private", "owned", "package"), Bundle("service"), Bundle("extension"),
                Bundle("narrowed"), Bundle("imported", "imported"), Bundle("implemented", "imported")],
            Provides = [Provider("private", "owner"), Provider("service"), Provider("narrowed", "owner"), Provider("implemented")],
            UsesContracts = [Use("private"), Use("imported")],
        };
        var facts = SunderPackageInformation.FromManifest(manifest);
        Assert.Equal(["example.extension", "example.implemented", "example.service"], facts.PublicBindings.Select(value => value.ContractId));
        Assert.Equal("extension", facts.PublicBindings[0].Kind);
        Assert.Empty(facts.PublicBindings[0].Providers);
        Assert.Equal("service", facts.PublicBindings[1].Kind);
        Assert.Equal("example.imported", Assert.Single(facts.PublicUses).ContractId);
    }

    [Fact]
    public void OptionalPackagePresenceIsNotOverwrittenByRequiredServiceReadinessAndDirectionIsPreserved()
    {
        var manifest = new SunderPackageManifest
        {
            DependsOn = [new() { PackageId = "example.owner", VersionRange = ">=1.0.0 <2.0.0", Presence = "optional" }],
            ContractBundles = [Bundle("extension", "imported")],
            UsesContracts = [Use("extension")],
            Provides = [Provider("extension")],
            PackageApiImports = [new() { PackageId = "example.owner", UseRefs = [new() { Role = "runtime", ContractId = "example.extension", ContractVersion = "1.0.0" }] }],
            ProviderImports = [new() { PackageId = "example.owner", ProviderRefs = [new() { Role = "runtime", ProviderId = "extension.provider", ContractId = "example.extension", ContractVersion = "1.0.0" }] }],
        };
        var facts = SunderPackageInformation.FromManifest(manifest);
        var dependency = Assert.Single(facts.Dependencies);
        Assert.Equal("optional", dependency.Presence);
        Assert.Equal(["implements", "uses"], dependency.Bindings.Select(value => value.Kind));
        Assert.True(Assert.Single(facts.PublicUses).Required);
        Assert.Equal("example.extension", Assert.Single(facts.PublicBindings).ContractId);
    }

    private static SunderPackageContractBundleManifest Bundle(string name, string source = "owned", string exposure = "public")
        => new() { Source = source, ContractId = "example." + name, Version = "1.0.0", Exposure = exposure };
    private static SunderPackageProviderManifest Provider(string name, string audience = "public")
        => new() { Role = "runtime", ProviderId = name + ".provider", ContractId = "example." + name, ContractVersion = "1.0.0", Audience = audience };
    private static SunderPackageContractUseManifest Use(string name)
        => new() { Role = "runtime", ContractId = "example." + name, ContractVersion = "1.0.0", Required = true, Actions = ["discover", "invoke"] };
}
