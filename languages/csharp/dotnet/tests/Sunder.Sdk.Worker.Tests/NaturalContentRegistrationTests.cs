using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker.Tests.TestSupport;

namespace Sunder.Sdk.Worker.Tests;

public sealed class NaturalContentRegistrationTests
{
    private static readonly Action<SunderRpcContentRegistrationOptions> ValidateHostOptions = LoadHostOptionsValidator();
    private static readonly SunderRpcContractDescriptor ContentDescriptor = SunderRpcContractDescriptor.Parse("""
        {"descriptorVersion":1,"schemaProfile":"sunder.rpc.values.v1","contractId":"test.content","version":"1.0.0",
         "errors":{},"services":{"content":{"methods":{"echo":{"kind":"unary","idempotency":"none","errors":[],
         "requestSchema":{"$ref":"#/$defs/Content"},"responseSchema":{"$ref":"#/$defs/Content"}}}}},
         "$defs":{"Content":{"type":"object","logicalType":"content-reference"}}}
        """u8);

    [Theory]
    [InlineData(false, 0, "")]
    [InlineData(true, 0, "")]
    [InlineData(false, 1_200_000, ".12")]
    [InlineData(true, 1_200_000, ".12")]
    [InlineData(false, 1_234_567, ".1234567")]
    [InlineData(true, 1_234_567, ".1234567")]
    public async Task NaturalDefaultsRegisterThroughWorkerFramesAndPreserveCanonicalLogicalExpiry(
        bool fromStream, long fractionTicks, string canonicalFraction)
    {
        byte[] bytes = [1, 2, 3];
        var second = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30);
        var expiry = second.AddTicks(fractionTicks);
        var canonicalExpiry = second.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + canonicalFraction + "Z";
        JsonElement logical = default;
        await using var worker = new WorkerHarness(context => new SunderWorkerOptions(
            [WorkerHarness.Registration(new TestRpcHandler())])
        {
            OnActivated = async (_, token) =>
            {
                await using var resources = new SunderRpcResourceLifetime();
                await using var operation = await resources.CreateCallAsync(context.Rpc, new("rpc1_example"), cancellationToken: token);
                await using var content = fromStream
                    ? SunderContent.FromStream(new MemoryStream(bytes))
                    : SunderContent.FromBytes(bytes);
                logical = await operation.SerializeAsync(content,
                    static (marshalling, value, cancellation) => marshalling.RegisterContentAsync(value, cancellation), token);
            },
        });
        await worker.HandshakeAsync();
        await worker.StartCandidateAndCommitAsync();
        await worker.PrepareStorageMigrationsAsync();
        await worker.ActivateAsync();

        const string scopeId = "natural-content-scope";
        using (var request = await worker.ReadAsync())
        {
            Assert.Equal("worker.scope-open", request.RootElement.GetProperty("type").GetString());
            await worker.SendAsync(new
            {
                type = "host.result", id = request.RootElement.GetProperty("id").GetString(),
                value = new { scopeId, deadlineUtc = second.AddMinutes(1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture) },
            });
        }

        string stagingPath;
        using (var request = await worker.ReadAsync())
        {
            var frame = request.RootElement;
            Assert.Equal("worker.content-register", frame.GetProperty("type").GetString());
            Assert.Equal(scopeId, frame.GetProperty("scopeId").GetString());
            Assert.Equal("rpc1_example", frame.GetProperty("endpointReference").GetString());
            stagingPath = frame.GetProperty("filePath").GetString()!;
            Assert.StartsWith(worker.DataPath, stagingPath, StringComparison.Ordinal);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(stagingPath));
            var fields = frame.GetProperty("options");
            var options = new SunderRpcContentRegistrationOptions(
                fields.GetProperty("mediaType").GetString()!, fields.GetProperty("fileName").GetString()!,
                fields.GetProperty("length").ValueKind == JsonValueKind.Null ? null : fields.GetProperty("length").GetInt64(),
                fields.GetProperty("expiresAtUtc").ValueKind == JsonValueKind.Null ? null : fields.GetProperty("expiresAtUtc").GetDateTimeOffset(),
                fields.GetProperty("repeatability").GetString() switch
                {
                    "single-use" => SunderRpcContentRepeatability.SingleUse,
                    "repeatable" => SunderRpcContentRepeatability.Repeatable,
                    _ => throw new InvalidOperationException("Unexpected Worker repeatability."),
                }, fields.GetProperty("maximumUses").GetInt32());

            // This is the production Host method, not a permissive registration stub or a copied predicate.
            ValidateHostOptions(options);
            Assert.Equal("content.bin", options.FileName);
            Assert.Equal("application/octet-stream", options.MediaType);
            Assert.Throws<ArgumentException>(() => ValidateHostOptions(options with { FileName = "" }));
            Assert.Throws<ArgumentException>(() => ValidateHostOptions(options with { FileName = "\t" }));
            await worker.SendAsync(new
            {
                type = "host.result", id = frame.GetProperty("id").GetString(),
                value = new
                {
                    id = "natural-content", length = bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    mediaType = options.MediaType, fileName = options.FileName,
                    expiresAtUtc = expiry.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), repeatability = "single-use",
                },
            });
        }
        using (var request = await worker.ReadAsync())
        {
            Assert.False(File.Exists(stagingPath));
            Assert.Equal("worker.scope-close", request.RootElement.GetProperty("type").GetString());
            Assert.Equal(scopeId, request.RootElement.GetProperty("scopeId").GetString());
            await worker.SendAsync(new { type = "host.result", id = request.RootElement.GetProperty("id").GetString(), value = (object?)null });
        }
        using (var activated = await worker.ReadAsync())
            Assert.Equal("worker.activated", activated.RootElement.GetProperty("type").GetString());

        Assert.Equal("content.bin", logical.GetProperty("fileName").GetString());
        Assert.Equal(canonicalExpiry, logical.GetProperty("expiresAtUtc").GetString());
        Assert.True(ContentDescriptor.IsValid("#/$defs/Content", logical, out var error), error);
        var optionsForLogical = new JsonSerializerOptions { Converters = { new SunderRpcJsonConverterFactory() } };
        var reference = logical.Deserialize<SunderRpcContentReference>(optionsForLogical)!;
        Assert.Equal(expiry, reference.ExpiresAtUtc);
        Assert.Equal(canonicalExpiry, JsonSerializer.SerializeToElement(reference, optionsForLogical).GetProperty("expiresAtUtc").GetString());
        await worker.ShutdownAsync();
    }

    private static Action<SunderRpcContentRegistrationOptions> LoadHostOptionsValidator()
    {
        const string relativePath = "languages/csharp/dotnet/host/Sunder.Runtime.Host/Services/RuntimeRpcContentClient.cs";
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, relativePath))) root = root.Parent;
        Assert.NotNull(root);
        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root.FullName, relativePath))).GetRoot();
        var authority = syntax.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(type => type.Identifier.ValueText == "RuntimeRpcInvocationAuthority");
        var validator = authority.Members.OfType<MethodDeclarationSyntax>()
            .Single(method => method.Identifier.ValueText == "ValidateOptions");
        var source = "using System; using Sunder.Sdk.Rpc; internal static class HostValidator {\n" + validator.ToFullString() + "\n}";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(SunderRpcContentRegistrationOptions).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("NaturalContentHostValidator", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        image.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(image).GetType("HostValidator")!
            .GetMethod("ValidateOptions", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Action<SunderRpcContentRegistrationOptions>>();
    }
}
