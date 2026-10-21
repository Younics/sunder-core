using Sunder.Package.Build.Rpc.Authoring.CSharp;
using Xunit;

namespace Sunder.Package.Build.Tests;

public sealed partial class SunderRpcCSharpModelProjectionTests
{
    [Fact]
    public void PinnedPublicNamePreservesDescriptorAbiAndExportAcrossSourceRename()
    {
        const string source = """
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)]
            public interface GreetingService { ValueTask<ImplementationValue> EchoAsync(ImplementationValue value); }
            [SunderValue("WireGreeting", PublicName = "HTTPGreeting")]
            public sealed record ImplementationValue(string Message);
            """;
        var before = Compile(source);
        var after = Compile(source.Replace("ImplementationValue", "RenamedValue", StringComparison.Ordinal));
        Assert.True(before.Success, string.Join(Environment.NewLine, before.Diagnostics));
        Assert.True(after.Success, string.Join(Environment.NewLine, after.Diagnostics));
        var original = Assert.Single(before.Contracts);
        var renamed = Assert.Single(after.Contracts);
        Assert.Equal(original.Descriptor.GetCanonicalUtf8(), renamed.Descriptor.GetCanonicalUtf8());
        Assert.Equal(original.Descriptor.Sha256, renamed.Descriptor.Sha256);
        Assert.Equal(original.Descriptor.AbiSha256, renamed.Descriptor.AbiSha256);
        Assert.Equal(original.ModelProjection.ToCanonicalUtf8(), renamed.ModelProjection.ToCanonicalUtf8());
        Assert.Equal("HTTPGreeting", renamed.ModelProjection.Models["WireGreeting"]);
        Assert.Equal("RenamedValue", Assert.Single(renamed.SourceTypes).SourceType.Name);
        Assert.Equal("global::RenamedValue", SunderRpcCSharpMarshallingPlan.Owned(renamed).Layouts["WireGreeting"].TypeName);

        var imported = SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(ProjectExport(after), "1.0.0");
        Assert.Equal(SunderRpcCSharpImportPrepass.PrepareOwnedPackageApi(ProjectExport(before), "1.0.0"), imported);
        Assert.Single(imported, file => file.Path == "models/example.shared/HTTPGreeting.g.cs");
        AssertCompiles(imported);

        // Changing only the public spelling is projection metadata, never wire identity.
        var changed = Compile(source.Replace("HTTPGreeting", "AnotherGreeting", StringComparison.Ordinal));
        Assert.True(changed.Success, string.Join(Environment.NewLine, changed.Diagnostics));
        var changedContract = Assert.Single(changed.Contracts);
        Assert.Equal(original.Descriptor.GetCanonicalUtf8(), changedContract.Descriptor.GetCanonicalUtf8());
        Assert.Equal(original.Descriptor.AbiSha256, changedContract.Descriptor.AbiSha256);
        Assert.Equal("AnotherGreeting", changedContract.ModelProjection.Models["WireGreeting"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("_Greeting2")]
    [InlineData("lower_name")]
    [InlineData("Class")]
    public void PublicNameUsesExactPortableSpellingOrExistingSourceDefault(string? publicName)
    {
        var result = Compile(PublicNameSource(publicName));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(publicName ?? "ImplementationValue", Assert.Single(result.Contracts).ModelProjection.Models["WireGreeting"]);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public void PublicNameHasInclusivePortableLengthLimit(int length, bool valid)
    {
        var result = Compile(PublicNameSource(new string('A', length)));
        Assert.Equal(valid, result.Success);
        if (!valid) Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "rpc-authoring.model-projection-invalid");
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad.name")]
    [InlineData("bad-name")]
    [InlineData("9Name")]
    [InlineData("class")]
    [InlineData("héllo")]
    [InlineData(" Name")]
    public void InvalidPublicNamesFailSourceCompilation(string publicName)
    {
        var result = Compile(PublicNameSource(publicName));
        Assert.False(result.Success);
        Assert.Empty(result.Contracts);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Id == "rpc-authoring.model-projection-invalid");
        Assert.Equal("source.cs", diagnostic.Path);
        Assert.NotNull(diagnostic.Line);
    }

    [Theory]
    [InlineData("SharedValue")]
    [InlineData("sharedvalue")]
    public void DuplicateExplicitPublicNamesAcrossContractsReportBothSourceLocations(string secondName)
    {
        var result = Compile($$"""
            using System.Threading.Tasks;
            using Sunder.Sdk.Services;
            [SunderService(SunderServiceExposure.Public)] public interface FirstService { ValueTask<FirstValue> ReadAsync(); }
            [SunderService(SunderServiceExposure.Public)] public interface SecondService { ValueTask<SecondValue> ReadAsync(); }
            [SunderValue("FirstWire", PublicName = "SharedValue")] public sealed record FirstValue(string Message);
            [SunderValue("SecondWire", PublicName = "{{secondName}}")] public sealed record SecondValue(string Message);
            """);
        Assert.False(result.Success);
        Assert.Empty(result.Contracts);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Id == "rpc-authoring.generated-name-collision");
        Assert.Contains("FirstValue", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("SecondValue", diagnostic.Message, StringComparison.Ordinal);
        Assert.NotEmpty(diagnostic.RelatedSpans!);
    }

    private static string PublicNameSource(string? publicName) => $$"""
        using System.Threading.Tasks;
        using Sunder.Sdk.Services;
        [SunderService(SunderServiceExposure.Public)] public interface GreetingService { ValueTask<ImplementationValue> ReadAsync(); }
        [SunderValue("WireGreeting"{{(publicName is null ? string.Empty : ", PublicName = " + System.Text.Json.JsonSerializer.Serialize(publicName))}})]
        public sealed record ImplementationValue(string Message);
        """;
}
