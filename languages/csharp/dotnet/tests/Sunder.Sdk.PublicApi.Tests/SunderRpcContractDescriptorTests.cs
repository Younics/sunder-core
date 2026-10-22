using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed class SunderRpcContractDescriptorTests
{
    [Fact]
    public void Parse_ValidatesSchemasAndCanonicalizesDeterministically()
    {
        var first = SunderRpcContractDescriptor.Parse(Descriptor());
        var second = SunderRpcContractDescriptor.Parse(Descriptor(reordered: true));

        Assert.Equal("example.messages", first.ContractId);
        Assert.Equal("1.2.3", first.Version);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.GetCanonicalUtf8(), second.GetCanonicalUtf8());
        Assert.Equal("send", Assert.Single(Assert.Single(first.Services).Methods).MethodId);

        using var valid = JsonDocument.Parse("""{"message":"hello"}""");
        using var missing = JsonDocument.Parse("{}");
        Assert.True(first.IsValid("#/$defs/Request", valid.RootElement, out var validError), validError);
        Assert.False(first.IsValid("#/$defs/Request", missing.RootElement, out var missingError));
        Assert.Contains("required", missingError, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RetainsImmutableDomainErrorDeclarationsAndDetailSchemas()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "RpcV1Conformance",
            "valid",
            "complete.rpc.json");
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(fixturePath));

        var declaration = descriptor.Errors["example.not-found"];
        Assert.Same(declaration, descriptor.FindError("example.not-found"));
        Assert.Equal("example.not-found", declaration.Code);
        Assert.Equal("The requested item does not exist.", declaration.Description);
        Assert.Equal("#/$defs/ErrorDetails", declaration.DetailsSchema);
        Assert.Null(descriptor.Errors["example.conflict"].DetailsSchema);
        Assert.Null(descriptor.FindError("example.missing"));
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, SunderRpcErrorDeclaration>)descriptor.Errors).Clear());
    }

    [Fact]
    public void RpcError_ClonesDomainDetailsAndRejectsInfrastructureDetails()
    {
        SunderRpcError error;
        using (var document = JsonDocument.Parse("""{"resource":"item-1"}"""))
        {
            error = new SunderRpcError(
                SunderRpcErrorKind.Domain,
                "example.not-found",
                "Not found.",
                details: document.RootElement);
        }

        Assert.Equal("item-1", error.Details!.Value.GetProperty("resource").GetString());
        Assert.Throws<ArgumentException>(() => new SunderRpcError(
            SunderRpcErrorKind.PermissionDenied,
            "rpc.permission.denied",
            "Denied.",
            details: JsonSerializer.SerializeToElement(new { reason = "forged" })));
        Assert.Throws<ArgumentException>(() => new SunderRpcError(
            SunderRpcErrorKind.Domain,
            "example.invalid",
            "Invalid.",
            details: default(JsonElement)));
    }

    [Theory]
    [InlineData("\uFEFF{")]
    [InlineData("{\"descriptorVersion\":1,\"DescriptorVersion\":1}")]
    [InlineData("{\"descriptorVersion\":1,\"descriptorVersion\":1}")]
    public void Parse_RejectsNonStrictJson(string json)
        => Assert.Throws<SunderRpcDescriptorException>(() =>
            SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Parse_RejectsOpenUnboundedAndUnsupportedSchemas()
    {
        var descriptor = Encoding.UTF8.GetString(Descriptor());
        AssertRejected(descriptor.Replace(
            "\"additionalProperties\":false",
            "\"additionalProperties\":true",
            StringComparison.Ordinal));
        AssertRejected(descriptor.Replace(
            "\"maxLength\":128",
            "\"maxLength\":128,\"pattern\":\".*\"",
            StringComparison.Ordinal));
        AssertRejected(descriptor.Replace(
            "#/$defs/Request",
            "https://example.invalid/request.json",
            StringComparison.Ordinal));
        AssertRejected(descriptor.Replace(
            "\"descriptorVersion\":1",
            "\"descriptorVersion\":1.0",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RejectsAmbiguousUntaggedOneOf()
    {
        var descriptor = Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.union",
              "version":"1.0.0",
              "errors":{},
              "services":{"union":{"methods":{"read":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Union"},"responseSchema":{"$ref":"#/$defs/A"}}}}},
              "$defs":{
                "Union":{"oneOf":[{"$ref":"#/$defs/A"},{"$ref":"#/$defs/B"}]},
                "A":{"type":"object","properties":{"value":{"type":"string","minLength":0,"maxLength":10}},"required":["value"],"additionalProperties":false},
                "B":{"type":"object","properties":{"other":{"type":"string","minLength":0,"maxLength":10}},"required":["other"],"additionalProperties":false}
              }
            }
            """);

        var exception = Assert.Throws<SunderRpcDescriptorException>(() =>
            SunderRpcContractDescriptor.Parse(descriptor));
        Assert.Contains("discriminator", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpGenerator_IsDeterministicAndEmitsBindingScaffolding()
    {
        // Moving implementation back into the runtime SDK would re-expand its
        // public contract even if a candidate snapshot was subsequently updated.
        Assert.Equal("Sunder.Package.Build", typeof(SunderRpcCSharpGenerator).Assembly.GetName().Name);
        Assert.False(typeof(SunderRpcCSharpGenerator).IsVisible);
        Assert.Null(typeof(SunderRpcContractDescriptor).Assembly.GetType("Sunder.Sdk.Rpc.SunderRpcCSharpGenerator"));
        var descriptor = SunderRpcContractDescriptor.Parse(Descriptor());

        var first = SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated");
        var second = SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated");

        Assert.Equal(first, second);
        Assert.Contains("using global::System.Threading.Tasks;", first, StringComparison.Ordinal);
        Assert.Contains("public static class SunderRpcContractIdentity_", first, StringComparison.Ordinal);
        Assert.Contains("public const int DescriptorVersion = 1;", first, StringComparison.Ordinal);
        Assert.Contains("public const string ContractId = \"example.messages\";", first, StringComparison.Ordinal);
        Assert.Contains("public const string ContractVersion = \"1.2.3\";", first, StringComparison.Ordinal);
        Assert.Contains($"public const string DescriptorSha256 = \"{descriptor.Sha256}\";", first, StringComparison.Ordinal);
        Assert.Contains($"public const string AbiSha256 = \"{descriptor.AbiSha256}\";", first, StringComparison.Ordinal);
        Assert.Contains("public sealed class Request", first, StringComparison.Ordinal);
        Assert.Contains("public interface IMessagesProvider", first, StringComparison.Ordinal);
        Assert.Contains("public sealed class MessagesClient", first, StringComparison.Ordinal);
        Assert.Contains("global::Sunder.Sdk.Rpc.SunderRpcInvocationContext context", first, StringComparison.Ordinal);
        Assert.Contains("global::Sunder.Sdk.Rpc.ISunderRpcCallScope scope", first, StringComparison.Ordinal);
        Assert.Contains("public sealed class ExampleMessagesRpcHandler", first, StringComparison.Ordinal);

        var taggedUnion = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.union",
              "version":"1.0.0",
              "errors":{},
              "services":{"union":{"methods":{"read":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Command"},"responseSchema":{"$ref":"#/$defs/Response"}}}}},
              "$defs":{
                "Command":{"oneOf":[{"$ref":"#/$defs/Create"},{"$ref":"#/$defs/Delete"}]},
                "Create":{"type":"object","properties":{"kind":{"type":"string","minLength":6,"maxLength":6,"const":"create"}},"required":["kind"],"additionalProperties":false},
                "Delete":{"type":"object","properties":{"kind":{"type":"string","minLength":6,"maxLength":6,"const":"delete"}},"required":["kind"],"additionalProperties":false},
                "Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}
              }
            }
            """));
        var taggedUnionSource = SunderRpcCSharpGenerator.Generate(taggedUnion, "Example.Generated");
        Assert.Contains(
            "global::System.Threading.Tasks.ValueTask<Response> ReadAsync(global::Sunder.Sdk.Rpc.SunderRpcInvocationContext context, Command request",
            taggedUnionSource,
            StringComparison.Ordinal);
        Assert.Contains("public abstract record Command", taggedUnionSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpGenerator_AccessibilityModesAreDeterministicGoldenOutputsAndCompile()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Descriptor());

        var defaultSource = SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated");
        var publicSource = SunderRpcCSharpGenerator.Generate(
            descriptor,
            "Example.Generated",
            SunderRpcCSharpAccessibility.Public);
        var internalSource = SunderRpcCSharpGenerator.Generate(
            descriptor,
            "Example.Generated",
            SunderRpcCSharpAccessibility.Internal);

        Assert.Equal(defaultSource, publicSource);
        var publicHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicSource))).ToLowerInvariant();
        var internalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(internalSource))).ToLowerInvariant();
        Assert.True(
            string.Equals("ac3789caa5aaf6c78b4c7e96c7a3de08301ab044a0d3fe6f712e3cae113211bf", publicHash, StringComparison.Ordinal),
            $"Public generator golden changed to {publicHash}.");
        Assert.True(
            string.Equals("8fe9485ad4225feac2f3f9759f396b33b135eb29f615364983bf87dbe1bc3254", internalHash, StringComparison.Ordinal),
            $"Internal generator golden changed to {internalHash}.");
        Assert.Equal(
            internalSource,
            SunderRpcCSharpGenerator.Generate(
                descriptor,
                "Example.Generated",
                SunderRpcCSharpAccessibility.Internal));

        Assert.Contains("public sealed class Request", publicSource, StringComparison.Ordinal);
        Assert.Contains("public interface IMessagesProvider", publicSource, StringComparison.Ordinal);
        Assert.Contains("public sealed class MessagesClient", publicSource, StringComparison.Ordinal);
        Assert.Contains("public sealed class ExampleMessagesRpcHandler", publicSource, StringComparison.Ordinal);
        Assert.Contains("internal sealed class Request", internalSource, StringComparison.Ordinal);
        Assert.Contains("internal interface IMessagesProvider", internalSource, StringComparison.Ordinal);
        Assert.Contains("internal sealed class MessagesClient", internalSource, StringComparison.Ordinal);
        Assert.Contains("internal sealed class ExampleMessagesRpcHandler", internalSource, StringComparison.Ordinal);

        AssertCompiles(publicSource);
        AssertCompiles(internalSource);
        Assert.Empty(CompileAndLoad(internalSource).GetExportedTypes());
    }

    [Fact]
    public void CSharpGenerator_RejectsUndefinedAccessibility()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Descriptor());

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SunderRpcCSharpGenerator.Generate(
                descriptor,
                "Example.Generated",
                (SunderRpcCSharpAccessibility)42));

        Assert.Equal("accessibility", exception.ParamName);
        Assert.Contains("must be Public or Internal", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpGenerator_IdentityTypeNamesDoNotCollideAcrossContractsInOneNamespace()
    {
        var first = SunderRpcContractDescriptor.Parse(Descriptor());
        var second = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.other",
              "version":"1.0.0",
              "errors":{},
              "services":{"other":{"methods":{"read":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/OtherRequest"},"responseSchema":{"$ref":"#/$defs/OtherResponse"}}}}},
              "$defs":{
                "OtherRequest":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "OtherResponse":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}
              }
            }
            """));

        _ = CompileAndLoad(
            SunderRpcCSharpGenerator.Generate(first, "Example.Shared"),
            SunderRpcCSharpGenerator.Generate(second, "Example.Shared"));
    }

    [Fact]
    public void CSharpGenerator_RejectsDefinitionNameCollisions()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.generator",
              "version":"1.0.0",
              "errors":{},
              "services":{"foo-bar":{"methods":{
                "do-it":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/foo-bar"},"responseSchema":{"$ref":"#/$defs/foo_bar"}},
                "do_it":{"kind":"server-stream","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/StringMap"},"eventSchema":{"$ref":"#/$defs/Empty"}}
              }}},
              "$defs":{
                "Empty":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "StringMap":{"type":"object","properties":{},"additionalProperties":{"type":"string","minLength":0,"maxLength":8},"propertyNames":{"type":"string","minLength":1,"maxLength":16},"minProperties":0,"maxProperties":4},
                "foo-bar":{"type":"object","properties":{"class":{"type":"string","minLength":0,"maxLength":8},"foo-bar":{"type":"integer","minimum":0,"maximum":8},"foo_bar":{"type":"boolean"},"line\u0001break":{"type":"string","minLength":0,"maxLength":8},"map":{"$ref":"#/$defs/StringMap"}},"required":["class","foo-bar","foo_bar","line\u0001break","map"],"additionalProperties":false},
                "foo_bar":{"type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}
              }
            }
            """));

        var exception = Assert.Throws<InvalidOperationException>(
            () => SunderRpcCSharpGenerator.Generate(descriptor, "namespace.Generated"));

        Assert.Contains("rpc-authoring.generated-name-collision", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FooBar", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpGenerator_RejectsDescriptorSymbolsThatShadowGeneratedSymbols()
    {
        const string contractId = "example.collisions";
        const string version = "2.0.0";
        var identityHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(contractId + "\0" + version))).ToLowerInvariant();
        var identityName = "SunderRpcContractIdentity_" + identityHash;
        var descriptorJson = """
            {
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"CONTRACT_ID",
              "version":"CONTRACT_VERSION",
              "errors":{},
              "services":{
                "value-task":{"methods":{"invoke":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/ValueTask"},"responseSchema":{"$ref":"#/$defs/Task"}}}},
                "task":{"methods":{"invoke":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Guid"},"responseSchema":{"$ref":"#/$defs/DateTimeOffset"}}}},
                "json-element":{"methods":{"invoke":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/JsonElement"},"responseSchema":{"$ref":"#/$defs/SunderRpcOptional"}}}}
              },
              "$defs":{
                "ValueTask":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "Task":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "Guid":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "DateTimeOffset":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "JsonElement":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "SunderRpcOptional":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "Exception":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "CancellationToken":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "IAsyncEnumerable":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "JsonSerializer":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "SunderRpcJsonConverterFactory":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "SunderRpcClientExtensions":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "IValueTaskProvider":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "ExampleCollisionsRpcHandler":{"type":"object","properties":{},"required":[],"additionalProperties":false},
                "IDENTITY_NAME":{"type":"object","properties":{},"required":[],"additionalProperties":false}
              }
            }
            """
            .Replace("CONTRACT_ID", contractId, StringComparison.Ordinal)
            .Replace("CONTRACT_VERSION", version, StringComparison.Ordinal)
            .Replace("IDENTITY_NAME", identityName, StringComparison.Ordinal);
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(descriptorJson));

        var exception = Assert.Throws<InvalidOperationException>(
            () => SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated.Collisions"));

        Assert.Contains("rpc-authoring.generated-name-collision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpGenerator_CompilesCompleteV1FixtureWithFormatsAndBinary64Types()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "RpcV1Conformance",
            "valid",
            "complete.rpc.json");
        var descriptor = SunderRpcContractDescriptor.Parse(File.ReadAllBytes(fixturePath));

        var source = SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated");
        var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();

        Assert.True(
            string.Equals("c5cb04142eea3db84eb85c51e3b884552bf73d70908999333476cf91056ab786", sourceHash, StringComparison.Ordinal),
            $"Complete V1 generator golden changed to {sourceHash}.");
        Assert.Contains("public required global::System.Guid CorrelationId", source, StringComparison.Ordinal);
        Assert.Contains(
            "public required global::Sunder.Sdk.Rpc.SunderRpcOffsetDateTime ObservedAt",
            source,
            StringComparison.Ordinal);
        Assert.Contains("public required global::Sunder.Sdk.Rpc.SunderRpcContentReference Content", source, StringComparison.Ordinal);
        Assert.Contains("public required double Ratio", source, StringComparison.Ordinal);
        Assert.Contains("public global::Sunder.Sdk.Rpc.SunderRpcOptional<string?> OptionalNote", source, StringComparison.Ordinal);
        Assert.Contains("Converters = { new global::Sunder.Sdk.Rpc.SunderRpcJsonConverterFactory() }", source, StringComparison.Ordinal);
        Assert.Contains($"public const string AbiSha256 = \"{descriptor.AbiSha256}\";", source, StringComparison.Ordinal);
        Assert.Contains("ISunderRpcDeclaredErrorMetadata", source, StringComparison.Ordinal);
        Assert.Contains("public static class ExampleNotFoundRpcError", source, StringComparison.Ordinal);
        Assert.Contains("public static global::Sunder.Sdk.Rpc.SunderRpcException Create(ErrorDetails details", source, StringComparison.Ordinal);
        Assert.Contains("public static bool TryGetDetails", source, StringComparison.Ordinal);
        var internalSource = SunderRpcCSharpGenerator.Generate(
            descriptor,
            "Example.Generated.Internal",
            SunderRpcCSharpAccessibility.Internal);
        Assert.Contains("internal static class ExampleNotFoundRpcError", internalSource, StringComparison.Ordinal);
        Assert.Empty(CompileAndLoad(internalSource).GetExportedTypes());
        var assembly = CompileAndLoad(source);
        var detailsType = assembly.GetType("Example.Generated.ErrorDetails", throwOnError: true)!;
        var details = Activator.CreateInstance(detailsType)!;
        detailsType.GetProperty("Resource")!.SetValue(details, "item-1");
        var errorType = assembly.GetType("Example.Generated.ExampleNotFoundRpcError", throwOnError: true)!;
        var domainError = Assert.IsType<SunderRpcException>(
            errorType.GetMethod("Create")!.Invoke(null, [details, "Not found."]));
        Assert.Equal("item-1", domainError.Error.Details!.Value.GetProperty("resource").GetString());
        object?[] detailArguments = [domainError, null];
        Assert.True((bool)errorType.GetMethod("TryGetDetails")!.Invoke(null, detailArguments)!);
        Assert.Equal("item-1", detailsType.GetProperty("Resource")!.GetValue(detailArguments[1]));
        var handlerType = Assert.Single(
            assembly.GetTypes(),
            static type => typeof(ISunderRpcDeclaredErrorMetadata).IsAssignableFrom(type));
        var metadata = Assert.IsAssignableFrom<ISunderRpcDeclaredErrorMetadata>(
            RuntimeHelpers.GetUninitializedObject(handlerType));
        foreach (var service in descriptor.Services)
        {
            Assert.True(metadata.DeclaredErrors.TryGetValue(service.ServiceId, out var methods));
            foreach (var method in service.Methods)
            {
                Assert.Equal(method.Errors, methods[method.MethodId]);
            }
        }
    }

    [Fact]
    public void JsonConverters_PreserveExactLogicalWireForms()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new SunderRpcJsonConverterFactory() },
        };
        var uuid = Guid.Parse("0192c4f0-8d3c-7a61-9b3f-6a9b89f4b8d2");
        var expiresAt = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
        var content = new SunderRpcContentReference(
            "opaque",
            42,
            new string('a', 64),
            "application/octet-stream",
            "display-name.bin",
            expiresAt,
            SunderRpcContentRepeatability.SingleUse);

        Assert.Equal(
            "\"0192c4f0-8d3c-7a61-9b3f-6a9b89f4b8d2\"",
            JsonSerializer.Serialize(uuid, options));
        Assert.Equal("\"2030-01-02T01:04:05Z\"", JsonSerializer.Serialize(expiresAt, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Guid>(
            "\"0192C4F0-8D3C-7A61-9B3F-6A9B89F4B8D2\"",
            options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DateTimeOffset>(
            "\"2030-01-02T01:04:05+00:00\"",
            options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DateTimeOffset>(
            "\"2030-01-02T01:04:05.123456789Z\"",
            options));

        var contentJson = JsonSerializer.SerializeToElement(content, options);
        Assert.Equal(7, contentJson.GetPropertyCount());
        Assert.Equal("2030-01-02T01:04:05Z", contentJson.GetProperty("expiresAtUtc").GetString());
        Assert.Equal("single-use", contentJson.GetProperty("repeatability").GetString());
        Assert.Equal(content, contentJson.Deserialize<SunderRpcContentReference>(options));

        const string exactExpiry = "2030-01-02T01:04:05.123456789Z";
        var exactContent = JsonSerializer.Deserialize<SunderRpcContentReference>(
            $$$"""
              {"id":"opaque","length":42,"sha256":"{{{new string('a', 64)}}}","mediaType":"application/octet-stream","fileName":"display-name.bin","expiresAtUtc":"{{{exactExpiry}}}","repeatability":"single-use"}
              """,
            options)!;
        var exactContentJson = JsonSerializer.SerializeToElement(exactContent, options);
        Assert.Equal(exactExpiry, exactContentJson.GetProperty("expiresAtUtc").GetString());
    }

    [Fact]
    public void CSharpGenerator_OffsetDateTimeRoundTripsNineFractionalDigits()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.rpc.v1.instant",
              "version":"2.0.0",
              "errors":{},
              "services":{"instant":{"methods":{"echo":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Payload"},"responseSchema":{"$ref":"#/$defs/Payload"}}}}},
              "$defs":{
                "Instant":{"type":"string","format":"date-time"},
                "Payload":{"type":"object","properties":{"observedAt":{"$ref":"#/$defs/Instant"}},"required":["observedAt"],"additionalProperties":false}
              }
            }
            """));
        var source = SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated.Instant");
        var assembly = CompileAndLoad(source);
        var payloadType = assembly.GetType("Example.Generated.Instant.Payload", throwOnError: true)!;
        var observedAt = payloadType.GetProperty("ObservedAt")!;
        const string exact = "2000-02-29T23:59:59.123456789Z";
        var payload = Activator.CreateInstance(payloadType)!;
        observedAt.SetValue(payload, new SunderRpcOffsetDateTime(exact));

        var json = JsonSerializer.Serialize(payload, payloadType);
        var roundTrip = JsonSerializer.Deserialize(json, payloadType)!;

        Assert.Equal(exact, JsonDocument.Parse(json).RootElement.GetProperty("observedAt").GetString());
        Assert.Equal(exact, Assert.IsType<SunderRpcOffsetDateTime>(observedAt.GetValue(roundTrip)).Value);
    }

    [Fact]
    public void RestrictedProfile_ValidatesTaggedUnionMapsArraysNumbersBooleanAndNull()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.profile",
              "version":"1.0.0",
              "errors":{},
              "services":{"profile":{"methods":{"apply":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Command"},"responseSchema":{"$ref":"#/$defs/Details"}}}}},
              "$defs":{
                "Command":{"oneOf":[{"$ref":"#/$defs/Create"},{"$ref":"#/$defs/Delete"}]},
                "Create":{"type":"object","properties":{"kind":{"type":"string","minLength":6,"maxLength":6,"const":"create"},"name":{"type":"string","minLength":1,"maxLength":32}},"required":["kind","name"],"additionalProperties":false},
                "Delete":{"type":"object","properties":{"kind":{"type":"string","minLength":6,"maxLength":6,"const":"delete"},"name":{"type":"string","minLength":1,"maxLength":32}},"required":["kind","name"],"additionalProperties":false},
                "Details":{"type":"object","properties":{"count":{"type":"integer","minimum":0,"maximum":100},"score":{"type":"number","format":"float64","minimum":0,"maximum":100},"enabled":{"type":"boolean"},"nothing":{"type":"null"},"flags":{"type":"array","items":{"type":"boolean"},"minItems":0,"maxItems":4},"labels":{"type":"object","additionalProperties":{"type":"string","minLength":0,"maxLength":20},"propertyNames":{"type":"string","minLength":1,"maxLength":20},"minProperties":0,"maxProperties":4}},"required":["count","score","enabled","nothing","flags","labels"],"additionalProperties":false}
              }
            }
            """));
        using var command = JsonDocument.Parse("""{"kind":"create","name":"test"}""");
        using var details = JsonDocument.Parse("""{"count":1,"score":1.5,"enabled":true,"nothing":null,"flags":[true],"labels":{"a":"b"}}""");
        using var wrongTag = JsonDocument.Parse("""{"kind":"CREATE","name":"test"}""");

        Assert.True(descriptor.IsValid("#/$defs/Command", command.RootElement, out var commandError), commandError);
        Assert.True(descriptor.IsValid("#/$defs/Details", details.RootElement, out var detailsError), detailsError);
        Assert.False(descriptor.IsValid("#/$defs/Command", wrongTag.RootElement, out _));
        Assert.Contains(
            "public required double Score",
            SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void RestrictedProfile_ValidatesNullableValuesAndGeneratorEmitsNullableTypes()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.nullable",
              "version":"1.0.0",
              "errors":{},
              "services":{"nullable":{"methods":{"read":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Request"}}}}},
              "$defs":{
                "NullableText":{"oneOf":[{"type":"null"},{"type":"string","minLength":0,"maxLength":20}]},
                "Request":{"type":"object","properties":{"value":{"$ref":"#/$defs/NullableText"}},"required":["value"],"additionalProperties":false}
              }
            }
            """));
        using var nullValue = JsonDocument.Parse("""{"value":null}""");
        using var textValue = JsonDocument.Parse("""{"value":"test"}""");

        Assert.True(descriptor.IsValid("#/$defs/Request", nullValue.RootElement, out var nullError), nullError);
        Assert.True(descriptor.IsValid("#/$defs/Request", textValue.RootElement, out var textError), textError);
        Assert.Contains(
            "public required string? Value { get; init; }",
            SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void CSharpGenerator_OptionalNullableMembersRoundTripAllPresenceStates()
    {
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes("""
            {
              "descriptorVersion":1,
              "schemaProfile":"sunder.rpc.values.v1",
              "contractId":"example.rpc.v1.optional",
              "version":"2.0.0",
              "errors":{},
              "services":{"optional":{"methods":{"echo":{"kind":"unary","idempotency":"idempotent","errors":[],"requestSchema":{"$ref":"#/$defs/Payload"},"responseSchema":{"$ref":"#/$defs/Payload"}}}}},
              "$defs":{
                "NullableText":{"oneOf":[{"type":"null"},{"type":"string","minLength":0,"maxLength":32}]},
                "Payload":{"type":"object","properties":{"value":{"$ref":"#/$defs/NullableText"}},"required":[],"additionalProperties":false}
              }
            }
            """));
        var source = SunderRpcCSharpGenerator.Generate(descriptor, "Example.Generated.Optional");

        Assert.Contains(
            "[global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]\n    public global::Sunder.Sdk.Rpc.SunderRpcOptional<string?> Value",
            source,
            StringComparison.Ordinal);
        var assembly = CompileAndLoad(source);
        var payloadType = assembly.GetType("Example.Generated.Optional.Payload", throwOnError: true)!;
        var valueProperty = payloadType.GetProperty("Value")!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var absent = Activator.CreateInstance(payloadType)!;
        var explicitNull = Activator.CreateInstance(payloadType)!;
        valueProperty.SetValue(explicitNull, new SunderRpcOptional<string?>(null));
        var presentValue = Activator.CreateInstance(payloadType)!;
        valueProperty.SetValue(presentValue, new SunderRpcOptional<string?>("text"));

        var absentJson = JsonSerializer.Serialize(absent, payloadType, options);
        var explicitNullJson = JsonSerializer.Serialize(explicitNull, payloadType, options);
        var presentValueJson = JsonSerializer.Serialize(presentValue, payloadType, options);
        Assert.Equal("{}", absentJson);
        Assert.Equal("{\"value\":null}", explicitNullJson);
        Assert.Equal("{\"value\":\"text\"}", presentValueJson);

        var absentRoundTrip = JsonSerializer.Deserialize(absentJson, payloadType, options)!;
        var nullRoundTrip = JsonSerializer.Deserialize(explicitNullJson, payloadType, options)!;
        var valueRoundTrip = JsonSerializer.Deserialize(presentValueJson, payloadType, options)!;
        var absentState = (SunderRpcOptional<string?>)valueProperty.GetValue(absentRoundTrip)!;
        var nullState = (SunderRpcOptional<string?>)valueProperty.GetValue(nullRoundTrip)!;
        var valueState = (SunderRpcOptional<string?>)valueProperty.GetValue(valueRoundTrip)!;

        Assert.False(absentState.IsPresent);
        Assert.Throws<InvalidOperationException>(() => absentState.Value);
        Assert.True(nullState.IsPresent);
        Assert.Null(nullState.Value);
        Assert.True(valueState.IsPresent);
        Assert.Equal("text", valueState.Value);
    }

    private static void AssertCompiles(string source)
    {
        var compilation = CreateCompilation([source]);
        var errors = compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(static error => error.ToString())));
    }

    private static Assembly CompileAndLoad(params string[] sources)
    {
        var compilation = CreateCompilation(sources, warningsAsErrors: true);
        using var assemblyStream = new MemoryStream();
        var result = compilation.Emit(assemblyStream);
        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        assemblyStream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(assemblyStream);
    }

    private static CSharpCompilation CreateCompilation(string[] sources, bool warningsAsErrors = false)
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))
                                ?? throw new InvalidOperationException("Trusted platform assemblies are unavailable.");
        var referencePaths = trustedAssemblies.Split(Path.PathSeparator).ToHashSet(StringComparer.Ordinal);
        referencePaths.Add(typeof(SunderRpcContractDescriptor).Assembly.Location);
        return CSharpCompilation.Create(
            "GeneratedRpcBindings_" + Guid.NewGuid().ToString("N"),
            sources.Select(source => CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))),
            referencePaths.Select(static path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                generalDiagnosticOption: warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default));
    }

    private static void AssertRejected(string json)
        => Assert.Throws<SunderRpcDescriptorException>(() =>
            SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(json)));

    private static byte[] Descriptor(bool reordered = false)
    {
        var json = reordered
            ? """
              {"version":"1.2.3","services":{"messages":{"methods":{"send":{"responseSchema":{"$ref":"#/$defs/Response"},"requestSchema":{"$ref":"#/$defs/Request"},"errors":[],"idempotency":"none","kind":"unary"}}}},"schemaProfile":"sunder.rpc.values.v1","errors":{},"contractId":"example.messages","descriptorVersion":1,"$defs":{"Response":{"required":["accepted"],"properties":{"accepted":{"type":"boolean"}},"additionalProperties":false,"type":"object"},"Request":{"required":["message"],"additionalProperties":false,"properties":{"message":{"maxLength":128,"minLength":1,"type":"string"}},"type":"object"}}}
              """
            : """
              {"descriptorVersion":1,"schemaProfile":"sunder.rpc.values.v1","contractId":"example.messages","version":"1.2.3","errors":{},"services":{"messages":{"methods":{"send":{"kind":"unary","idempotency":"none","errors":[],"requestSchema":{"$ref":"#/$defs/Request"},"responseSchema":{"$ref":"#/$defs/Response"}}}}},"$defs":{"Request":{"type":"object","properties":{"message":{"type":"string","minLength":1,"maxLength":128}},"required":["message"],"additionalProperties":false},"Response":{"type":"object","properties":{"accepted":{"type":"boolean"}},"required":["accepted"],"additionalProperties":false}}}
              """;
        return Encoding.UTF8.GetBytes(json);
    }

}
