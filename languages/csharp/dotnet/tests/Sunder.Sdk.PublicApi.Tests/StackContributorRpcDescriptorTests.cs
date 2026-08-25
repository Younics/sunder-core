using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class StackContributorRpcDescriptorTests
{
    [Fact]
    public void Descriptor_ParsesEmbeddedAuthoritativeContract()
    {
        var descriptor = SunderStackContributorRpc.Descriptor;

        Assert.Equal(SunderStackContributorRpc.DescriptorVersion, descriptor.DescriptorVersion);
        Assert.Equal(SunderStackContributorRpc.ContractId, descriptor.ContractId);
        Assert.Equal(SunderStackContributorRpc.ContractVersion, descriptor.Version);
        Assert.Equal(SunderStackContributorRpc.DescriptorSha256, descriptor.Sha256);
        Assert.Equal(SunderStackContributorRpc.AbiSha256, descriptor.AbiSha256);
        Assert.Equal(
            descriptor.Sha256,
            Convert.ToHexString(SHA256.HashData(descriptor.GetCanonicalUtf8()))
                .ToLowerInvariant());
        var abiProjection = Assert.IsType<byte[]>(descriptor.GetCanonicalAbiProjectionUtf8());
        Assert.Equal(
            descriptor.AbiSha256,
            Convert.ToHexString(SHA256.HashData([.. "sunder.rpc.abi.v2\0"u8, .. abiProjection]))
                .ToLowerInvariant());
        Assert.Equal(
            "The contributor does not implement the requested optional Stack operation.",
            descriptor.Errors["stack.method.unsupported"].Description);
        var service = Assert.Single(descriptor.Services);
        Assert.Equal(SunderStackContributorRpc.ServiceId, service.ServiceId);
        Assert.Equal(
            ["metadata", "list-export-items", "export", "preview-import", "import", "import-applied"],
            service.Methods.Select(static method => method.MethodId));
        Assert.All(service.Methods, static method => Assert.Equal(SunderRpcMethodKind.Unary, method.Kind));
        Assert.All(
            service.Methods.Take(4),
            static method => Assert.Equal(SunderRpcMethodIdempotency.Idempotent, method.Idempotency));
        Assert.All(
            service.Methods.Skip(4),
            static method => Assert.Equal(SunderRpcMethodIdempotency.None, method.Idempotency));
        Assert.Empty(service.Methods[0].Errors);
        Assert.All(
            service.Methods.Skip(1),
            static method => Assert.Equal(["stack.method.unsupported"], method.Errors));
    }

    [Fact]
    public void Descriptor_GeneratesCompilableV2Bindings()
    {
        var descriptor = SunderStackContributorRpc.Descriptor;
        var source = SunderRpcCSharpGenerator.Generate(descriptor, "Sunder.Stacks.Generated");
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                                ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        var referencePaths = trustedAssemblies.Split(Path.PathSeparator).ToHashSet(StringComparer.Ordinal);
        referencePaths.Add(typeof(SunderRpcContractDescriptor).Assembly.Location);
        var compilation = CSharpCompilation.Create(
            "GeneratedStackRpcBindings",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            referencePaths.Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: ReportDiagnostic.Error));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(
            string.Equals("afba64ffe12464db28ecffd2a95d9eb19d2ea1c8ec09ce6a6a6ce518e2643fc4", sourceHash, StringComparison.Ordinal),
            $"Stack generator golden changed to {sourceHash}.");
        Assert.Contains(
            $"public const string AbiSha256 = \"{SunderStackContributorRpc.AbiSha256}\";",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public required global::Sunder.Sdk.Rpc.SunderRpcContentReference Content",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredInputSensitivity_RoundTripsThroughRpcBinding()
    {
        var schema = SunderStackContributorRpc.Descriptor.Definitions[nameof(StackRequiredInputDescriptor)];
        Assert.Contains(
            schema.GetProperty("required").EnumerateArray(),
            property => property.GetString() == "sensitivity");

        var input = new StackRequiredInputDescriptor(
            "api-key",
            "API key",
            StackValueSensitivity.Secret,
            Description: "Provider credential.");
        var json = System.Text.Json.JsonSerializer.Serialize(input, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<StackRequiredInputDescriptor>(
            json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.NotNull(roundTrip);
        Assert.Equal(StackValueSensitivity.Secret, roundTrip.Sensitivity);
        Assert.Contains("\"sensitivity\":1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ContributorAdapters_RequireTheCommonContributorContract()
    {
        Assert.Contains(typeof(IPackageStackContributor), typeof(IPackageStackExporter).GetInterfaces());
        Assert.Contains(typeof(IPackageStackContributor), typeof(IPackageStackImporter).GetInterfaces());
        Assert.Contains(typeof(IPackageStackContributor), typeof(IPackageStackImportAppliedHandler).GetInterfaces());
        Assert.Equal(
            typeof(IPackageStackContributor),
            typeof(SunderStackContributorRpc).GetMethod(nameof(SunderStackContributorRpc.CreateHandler))!
                .GetParameters()[0].ParameterType);
        Assert.Null(typeof(SunderStackContributorRpc).GetMethod("CreateWorkerProviderRegistration"));
        Assert.DoesNotContain(
            typeof(SunderStackContributorRpc).Assembly.GetReferencedAssemblies(),
            assembly => string.Equals(assembly.Name, "Sunder.Sdk.Worker", StringComparison.Ordinal));

        var handler = Assert.IsAssignableFrom<ISunderRpcDeclaredErrorMetadata>(
            SunderStackContributorRpc.CreateHandler(new TestContributor()));
        Assert.Empty(handler.DeclaredErrors[SunderStackContributorRpc.ServiceId]["metadata"]);
        Assert.Equal(
            ["stack.method.unsupported"],
            handler.DeclaredErrors[SunderStackContributorRpc.ServiceId]["import-applied"]);
    }

    private sealed class TestContributor : IPackageStackImportAppliedHandler
    {
        public string ContributorId => "test";
        public string DisplayName => "Test";

        public ValueTask OnStackImportAppliedAsync(
            StackImportAppliedContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
