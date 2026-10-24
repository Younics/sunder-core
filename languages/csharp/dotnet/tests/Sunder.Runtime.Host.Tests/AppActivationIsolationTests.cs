using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class AppActivationIsolationTests
{
    [Theory]
    [InlineData("runtime", true)]
    [InlineData("app", false)]
    public void OptionalOrOtherRoleProviderDoesNotInvalidateAppActivation(string role, bool required)
    {
        var consumer = Source("consumer", new SunderPackageManifest { UsesContracts = [Use(role, required)] });
        var provider = Source("maker", new SunderPackageManifest { Provides = [Provider()] });
        var ready = States(consumer, provider);
        var failed = ready.Select(state => state.PackageId == "maker" ? state with { IsEnabled = false, Readiness = PackageReadinessState.Failed } : state).ToArray();
        var before = AppActivationFingerprint.Create(Guid.Empty, "consumer", [consumer, provider], ready);
        Assert.Equal(before, AppActivationFingerprint.Create(Guid.Empty, "consumer", [consumer, provider], failed));
        Assert.Equal(before, AppActivationFingerprint.Create(Guid.Empty, "consumer", [consumer], ready));
        Assert.DoesNotContain("maker", AppActivationFingerprint.DependencyClosure("consumer", [consumer, provider]));
    }

    [Fact]
    public void RequiredAppProviderAndImportedSourceBytesRemainIdentityDependencies()
    {
        var consumer = Source("consumer", new SunderPackageManifest { UsesContracts = [Use("app", true)] });
        var provider = Source("maker", new SunderPackageManifest { Provides = [Provider()] });
        var before = AppActivationFingerprint.Create(Guid.Empty, "consumer", [consumer, provider], States(consumer, provider));
        Assert.NotEqual(before, AppActivationFingerprint.Create(Guid.Empty, "consumer", [consumer], States(consumer)));
        Assert.NotEqual(before, AppActivationFingerprint.Create(Guid.Empty, "consumer", [consumer, provider with { ContentIdentity = new('b', 64) }], States(consumer, provider)));
        Assert.Contains("maker", AppActivationFingerprint.DependencyClosure("consumer", [consumer, provider]));
    }

    [Fact]
    public void ContractOwnerImportDoesNotImportItsWorkerOptionalProviderLivenessIntoAnotherApp()
    {
        var consumer = Source("consumer", new SunderPackageManifest { PackageArtifactImports = [new() { PackageId = "agent" }] });
        var agent = Source("agent", new SunderPackageManifest { UsesContracts = [Use("runtime", false)] });
        var maker = Source("maker", new SunderPackageManifest { Provides = [Provider()] });
        var states = States(consumer, agent, maker);
        var before = AppActivationFingerprint.Create(Guid.Empty, "consumer", [consumer, agent, maker], states);
        var failed = states.Select(state => state.PackageId == "agent" ? state with { IsEnabled = false, Readiness = PackageReadinessState.Failed } : state).ToArray();
        Assert.Equal(before, AppActivationFingerprint.Create(Guid.Empty, "consumer", [consumer, agent, maker], failed));
        Assert.Equal(before, AppActivationFingerprint.Create(Guid.Empty, "consumer", [consumer, agent], states));
        Assert.NotEqual(before, AppActivationFingerprint.Create(Guid.Empty, "consumer", [consumer, agent with { ContentIdentity = new('b', 64) }, maker], states));
    }

    private static RuntimePackageSource Source(string id, SunderPackageManifest manifest)
        => new(id, PackageSourceKind.Dev, "/" + id, ContentIdentity: new('a', 64), Manifest: manifest);
    private static SunderPackageContractUseManifest Use(string role, bool required)
        => new() { Role = role, ContractId = "example.tools", ContractVersion = "1.0.0", Required = required, AbiSha256 = new('a', 64) };
    private static SunderPackageProviderManifest Provider()
        => new() { Role = "runtime", ProviderId = "maker.tools", ContractId = "example.tools", ContractVersion = "1.0.0", Audience = "public", AbiSha256 = new('a', 64) };
    private static SessionPackageDescriptor[] States(params RuntimePackageSource[] sources)
        => sources.Select(source => new SessionPackageDescriptor(source.PackageId, source.PackageId, "1.0.0", source.HostRoles,
            null, true, PackageReadinessState.Ready, [], null, null, null, 0)).ToArray();
}
