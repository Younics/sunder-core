using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Sunder.Package.Build.Tasks;
using Sunder.Package.Format;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed class SunderRpcCSharpMethodPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(600)]
    [InlineData(2100)]
    public void DurationIsSourcePolicyWithoutDescriptorOrAbiChanges(int seconds)
    {
        var baseline = Compile(0); var result = Compile(seconds);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var contract = Assert.Single(result.Contracts);
        Assert.Equal(Assert.Single(baseline.Contracts).Descriptor.GetCanonicalUtf8(), contract.Descriptor.GetCanonicalUtf8());
        Assert.Equal(baseline.Contracts[0].Descriptor.AbiSha256, contract.Descriptor.AbiSha256);
        Assert.Equal(seconds, Assert.Single(contract.Methods).TimeoutSeconds);
        Assert.Equal(seconds == 0, contract.MethodPolicies.IsEmpty);
        if (seconds != 0) Assert.Equal(seconds, contract.MethodPolicies.Services["long"]["run"].TimeoutSeconds);
        var roles = PackageContractBuilder.ExtendedDeadlineRoles(new SunderPackageManifest
        {
            Provides = [new() { ContractId = contract.Descriptor.ContractId, Role = "runtime" }],
            UsesContracts = [new() { ContractId = contract.Descriptor.ContractId, Role = "app", Actions = ["invoke"] }],
        }, new Dictionary<string, SunderRpcMethodPolicies> { [contract.Descriptor.ContractId] = contract.MethodPolicies });
        Assert.Equal(seconds > 600 ? ["app", "runtime"] : Array.Empty<string>(), roles.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2101)]
    public void DurationOutsideTheRequestedBoundIsRejected(int seconds)
    {
        var result = Compile(seconds);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.method-invalid" && diagnostic.Path == "source.cs");
    }

    private static SunderRpcCSharpCompilationResult Compile(int seconds)
        => new SunderRpcCSharpSourceCompiler().Compile(new("example.duration", "1.0.0", [new("source.cs", $$"""
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)]
            public interface LongService { [SunderMethod(TimeoutSeconds = {{seconds}})] ValueTask<bool> RunAsync(); }
            """)], ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(SunderServiceAttribute).Assembly.Location).Distinct(StringComparer.Ordinal).ToArray()));
}
