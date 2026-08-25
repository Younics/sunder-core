using System.Text;
using System.Text.Json;
using Sunder.Sdk.Packaging;

namespace Sunder.Sdk.Rpc;

internal static partial class SunderRpcDescriptorParser
{
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private const int MaximumJsonDepth = 256;
    private const int MaximumServices = 64;
    private const int MaximumMethods = 512;
    private const int MaximumDefinitions = 256;
    private const int MaximumErrors = 256;
    private const int MaximumAnnotationCharacters = 2048;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static SunderRpcContractDescriptor Parse(ReadOnlySpan<byte> descriptorUtf8)
    {
        if (descriptorUtf8.Length == 0 || descriptorUtf8.Length > SunderRpcProtocol.MaximumDescriptorBytes)
        {
            throw Error($"An RPC descriptor must be non-empty and no larger than {SunderRpcProtocol.MaximumDescriptorBytes} bytes.");
        }
        if (descriptorUtf8.StartsWith(Encoding.UTF8.Preamble))
        {
            throw Error("An RPC descriptor must be UTF-8 without a byte-order mark.", "descriptor-json");
        }

        try
        {
            _ = StrictUtf8.GetString(descriptorUtf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw Error(
                $"The RPC descriptor is not strict UTF-8 JSON: {exception.Message}",
                exception,
                "descriptor-json");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(descriptorUtf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumJsonDepth,
            });
        }
        catch (JsonException exception)
        {
            throw Error($"The RPC descriptor is invalid JSON: {exception.Message}", exception, "descriptor-json");
        }

        using (document)
        {
            var root = document.RootElement;
            RequireKind(root, JsonValueKind.Object, "$", "an object");

            var descriptorVersion = Required(root, "descriptorVersion", "$", JsonValueKind.Number);
            if (!descriptorVersion.TryGetInt32(out var descriptorVersionValue)
                || descriptorVersionValue is not (SunderRpcProtocol.DescriptorVersionV1 or SunderRpcProtocol.DescriptorVersionV2))
            {
                throw Error(
                    $"$.descriptorVersion must be {SunderRpcProtocol.DescriptorVersionV1} or {SunderRpcProtocol.DescriptorVersionV2}.",
                    "descriptor-version");
            }

            try
            {
                ValidateTokenStream(descriptorUtf8, descriptorVersionValue);
            }
            catch (SunderRpcDescriptorException)
            {
                throw;
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw Error(
                    $"The RPC descriptor is not strict UTF-8 JSON: {exception.Message}",
                    exception,
                    "descriptor-json");
            }

            var isV2 = descriptorVersionValue == SunderRpcProtocol.DescriptorVersionV2;
            if (isV2)
            {
                RequireOnlyProperties(
                    root,
                    "$",
                    "$schema",
                    "descriptorVersion",
                    "schemaProfile",
                    "contractId",
                    "version",
                    "errors",
                    "services",
                    "$defs");
            }

            if (isV2)
            {
                if (!root.TryGetProperty("schemaProfile", out _))
                {
                    throw Error("$ is missing required property 'schemaProfile'.", "schema-profile");
                }
                var schemaProfile = RequiredString(root, "schemaProfile", "$", 128);
                if (!string.Equals(
                        schemaProfile,
                        SunderRpcProtocol.ValueSchemaProfile,
                        StringComparison.Ordinal))
                {
                    throw Error(
                        $"$.schemaProfile must be '{SunderRpcProtocol.ValueSchemaProfile}'.",
                        "schema-profile");
                }
            }
            else
            {
                if (root.TryGetProperty("schemaProfile", out _)
                    || root.TryGetProperty("errors", out _))
                {
                    throw Error(
                        "A descriptor using V2 root fields must set descriptorVersion to 2.",
                        "descriptor-version");
                }
                RequireOnlyProperties(
                    root,
                    "$",
                    "$schema",
                    "descriptorVersion",
                    "contractId",
                    "version",
                    "services",
                    "$defs");
            }

            if (root.TryGetProperty("$schema", out var dialect))
            {
                RequireKind(dialect, JsonValueKind.String, "$.$schema", "a string");
                if (!string.Equals(dialect.GetString(), SunderRpcProtocol.JsonSchemaDialect, StringComparison.Ordinal))
                {
                    throw Error($"$.$schema must be '{SunderRpcProtocol.JsonSchemaDialect}'.");
                }
            }

            var contractId = RequiredString(root, "contractId", "$", isV2 ? 128 : 256);
            if (isV2 ? !IsContractIdV2(contractId) : !PackageId.TryParse(contractId, out _))
            {
                throw Error("$.contractId must use lowercase dot-separated ASCII identifiers.");
            }

            var version = RequiredString(root, "version", "$", SemanticVersion.MaximumLength);
            if (!SemanticVersion.TryParse(version, out _))
            {
                throw Error("$.version must be an independent strict SemVer 2.0 version.");
            }

            var definitionsElement = Required(root, "$defs", "$", JsonValueKind.Object);
            if (definitionsElement.GetPropertyCount() == 0
                || definitionsElement.GetPropertyCount() > MaximumDefinitions)
            {
                throw Error($"$.$defs must contain between 1 and {MaximumDefinitions} schemas.");
            }
            var definitions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var definition in definitionsElement.EnumerateObject())
            {
                if (!IsDefinitionName(definition.Name))
                {
                    throw Error($"$.$defs contains invalid schema name '{definition.Name}'.");
                }
                definitions.Add(definition.Name, definition.Value.Clone());
            }

            SunderRpcSchemaProfile.ValidateDefinitions(definitions, descriptorVersionValue);
            var errorDeclarations = isV2
                ? ParseErrors(Required(root, "errors", "$", JsonValueKind.Object), definitions)
                : new Dictionary<string, SunderRpcErrorDeclaration>(StringComparer.Ordinal);
            var servicesElement = isV2
                ? Required(root, "services", "$", JsonValueKind.Object)
                : Required(root, "services", "$", JsonValueKind.Array, JsonValueKind.Object);
            var services = ParseServices(
                servicesElement,
                definitions,
                errorDeclarations.Keys.ToHashSet(StringComparer.Ordinal),
                descriptorVersionValue);
            var projection = SunderRpcDescriptorProjector.Project(root, definitions, isV2);
            return new SunderRpcContractDescriptor(
                descriptorVersionValue,
                isV2 ? SunderRpcProtocol.ValueSchemaProfile : null,
                contractId,
                version,
                errorDeclarations.Values,
                services,
                definitions,
                projection.CanonicalUtf8,
                projection.Sha256,
                projection.CanonicalAbiProjectionUtf8,
                projection.AbiSha256);
        }
    }
}
