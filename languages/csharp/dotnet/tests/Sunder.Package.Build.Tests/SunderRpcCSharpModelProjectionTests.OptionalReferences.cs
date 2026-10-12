using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpModelProjectionTests
{
    private const string OptionalReferenceSource = """
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        using Api = Sunder.Generated.Packages.Example_Shared_Services;
        [SunderService(SunderServiceExposure.Public)] public interface TargetService { ValueTask<bool> ReadyAsync(); }
        [SunderService(SunderServiceExposure.Public)] public interface HolderService { ValueTask<Payload> EchoAsync(Payload request); }
        public sealed record Payload {
            [SunderOptionalServiceReference] public required ServiceReference<Api.ITargetService>? OptionalTarget { get; init; }
            public required ServiceReference<Api.ITargetService>? RequiredTarget { get; init; }
        }
        """;

    [Fact]
    public void OptionalReferences_AreMemberEvidenceNotTargetOrWirePolicyAndBothRolesCompile()
    {
        var prepass = OptionalReferencePrepass(OptionalReferenceSource);
        var strict = OptionalReferencePrepass(OptionalReferenceSource.Replace("[SunderOptionalServiceReference] ", "", StringComparison.Ordinal));
        Assert.True(prepass.Compilation.Success, string.Join(Environment.NewLine, prepass.Compilation.Diagnostics));
        Assert.True(strict.Compilation.Success, string.Join(Environment.NewLine, strict.Compilation.Diagnostics));
        var holder = prepass.Compilation.Contracts.Single(contract => contract.ContractKey == "holder");
        var strictHolder = strict.Compilation.Contracts.Single(contract => contract.ContractKey == "holder");
        Assert.Equal(strictHolder.Descriptor.GetCanonicalUtf8(), holder.Descriptor.GetCanonicalUtf8());
        Assert.Equal(strictHolder.Descriptor.AbiSha256, holder.Descriptor.AbiSha256);
        Assert.Equal(new[] { "optionalTarget" }, Assert.Single(holder.ModelProjection.OptionalServiceReferenceMembers).Value);
        Assert.Empty(strictHolder.ModelProjection.OptionalServiceReferenceMembers);
        Assert.Single(holder.ModelProjection.ServiceReferences); // Both occurrences share the same unchanged target definition.
        AssertCompiles(prepass.Sources.Append(new("source.cs", OptionalReferenceSource)));
        var imported = SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(ProjectExport(prepass.Compilation), "1.0.0");
        AssertCompiles(imported);
        var text = string.Join('\n', imported.Select(source => source.Text));
        Assert.Contains("RegisterOptionalServiceReferenceAsync", text, StringComparison.Ordinal);
        Assert.Contains("RegisterServiceReferenceAsync", text, StringComparison.Ordinal);
        Assert.Contains("PrepareOptionalServiceReferenceAsync", text, StringComparison.Ordinal);
        Assert.Contains("PrepareServiceReferenceAsync", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ServiceReference<Api.ITargetService>", "public required", "{ get; init; }")]
    [InlineData("System.Collections.Generic.IReadOnlyList<ServiceReference<Api.ITargetService>?>", "public required", "{ get; init; }")]
    [InlineData("Sunder.Sdk.Rpc.SunderRpcOptional<ServiceReference<Api.ITargetService>?>", "public", "{ get; init; }")]
    [InlineData("ServiceReference<Api.ITargetService>?", "public static", "{ get; set; }")]
    public void OptionalReferences_RejectNonnullableContainersWrappersAndStaticMembers(string type, string modifiers, string accessors)
    {
        var source = OptionalReferenceSource.Replace("public required ServiceReference<Api.ITargetService>? OptionalTarget { get; init; }",
            modifiers + " " + type + " OptionalTarget " + accessors, StringComparison.Ordinal);
        var result = OptionalReferencePrepass(source).Compilation;
        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
    }

    private static SunderRpcCSharpOwnedClientPrepassResult OptionalReferencePrepass(string source)
        => SunderRpcCSharpOwnedClientPrepass.Compile(new(), new("example.shared", "1.0.0", [new("source.cs", source)], References(), GenerateServiceInterfaces: true));
}
