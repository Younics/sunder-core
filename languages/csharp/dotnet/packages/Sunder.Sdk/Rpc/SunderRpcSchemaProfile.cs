using System.Text.Json;

namespace Sunder.Sdk.Rpc;

internal static partial class SunderRpcSchemaProfile
{
    private const int MaximumSchemaDepth = 64;
    private const int MaximumSchemaNodes = 4096;
    private const int MaximumProperties = 256;
    private const int MaximumV1CollectionItems = 1_000_000;
    private const int MaximumV1StringCharacters = 1_000_000;
    private const int MaximumUnionVariants = 16;
    private const int MaximumEnumValues = 256;
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly string[] AnnotationProperties = ["title", "description"];

    public static void ValidateDefinitions(
        IReadOnlyDictionary<string, JsonElement> definitions,
        int descriptorVersion)
    {
        try
        {
            ValidateDefinitionsCore(definitions, descriptorVersion);
        }
        catch (SunderRpcDescriptorException exception)
            when (exception.ErrorCode == "descriptor-representation")
        {
            throw Error(exception.Message);
        }
    }

    public static bool IsInstanceValid(
        SunderRpcContractDescriptor descriptor,
        string schemaReference,
        JsonElement value,
        out string? error)
        => IsInstanceValid(descriptor, schemaReference, value, out error, out _);

    internal static bool IsInstanceValid(
        SunderRpcContractDescriptor descriptor,
        string schemaReference,
        JsonElement value,
        out string? error,
        out SunderRpcValidationFailure? failure)
    {
        if (!SunderRpcDescriptorParser.TryGetReferenceName(schemaReference, out var name)
            || !descriptor.TryGetDefinition(name, out var schema))
        {
            error = "The schema reference is not a definition in this contract.";
            failure = new SunderRpcValidationFailure("schema-reference", "$");
            return false;
        }

        var state = new InstanceValidationState(descriptor);
        var valid = ValidateInstance(schema, value, "$", state, out error);
        failure = state.Failure;
        return valid;
    }
}
