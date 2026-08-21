using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Runtime.Host.Services;

internal enum RuntimeRpcValueLimitKind
{
    Bytes,
    Depth,
    Nodes,
    ObjectMembers,
    InlineDecodedBytes,
}

internal sealed class RuntimeRpcValueLimitException(RuntimeRpcValueLimitKind limitKind)
    : Exception($"The RPC value exceeds its {limitKind} limit.")
{
    public RuntimeRpcValueLimitKind LimitKind { get; } = limitKind;
}

internal readonly record struct RuntimeRpcValueMeasurement(
    int Utf8Bytes,
    int MaximumDepth,
    int TotalNodes,
    int MaximumObjectMembers)
{
    public long InlineDecodedBytes { get; init; }
}

internal static class RuntimeRpcValueMeasurer
{
    private const int EncoderBufferSlack = 4096;

    public static RuntimeRpcValueMeasurement Measure(
        JsonElement value,
        RuntimeRpcPolicyOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("An undefined JSON value cannot be measured.", nameof(value));
        }

        using var output = new BoundedPooledBufferWriter(
            limits.MaxPayloadBytes,
            checked(limits.MaxPayloadBytes + EncoderBufferSlack));
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            var totalNodes = 0;
            var maximumDepth = 0;
            var maximumObjectMembers = 0;
            var stack = new Stack<TraversalFrame>();
            var current = value;
            var currentDepth = 1;

            while (true)
            {
                if (++totalNodes > limits.MaxPayloadNodes)
                {
                    throw new RuntimeRpcValueLimitException(RuntimeRpcValueLimitKind.Nodes);
                }
                if (currentDepth > limits.MaxPayloadDepth)
                {
                    throw new RuntimeRpcValueLimitException(RuntimeRpcValueLimitKind.Depth);
                }
                maximumDepth = Math.Max(maximumDepth, currentDepth);

                var frame = current.ValueKind switch
                {
                    JsonValueKind.Array => TraversalFrame.ForArray(current, currentDepth),
                    JsonValueKind.Object => TraversalFrame.ForObject(current, currentDepth),
                    _ => default,
                };
                JsonElement child;
                string? propertyName;
                if (frame.IsContainer)
                {
                    if (frame.IsObject) writer.WriteStartObject();
                    else writer.WriteStartArray();

                    if (frame.MoveNext(out child, out propertyName))
                    {
                        ValidateObjectMembers(frame, limits, ref maximumObjectMembers);
                        if (frame.IsObject) writer.WritePropertyName(propertyName!);
                        stack.Push(frame);
                        current = child;
                        currentDepth++;
                        continue;
                    }

                    if (frame.IsObject) writer.WriteEndObject();
                    else writer.WriteEndArray();
                }
                else
                {
                    current.WriteTo(writer);
                }

                var moved = false;
                while (stack.TryPop(out frame))
                {
                    if (frame.MoveNext(out child, out propertyName))
                    {
                        ValidateObjectMembers(frame, limits, ref maximumObjectMembers);
                        if (frame.IsObject) writer.WritePropertyName(propertyName!);
                        stack.Push(frame);
                        current = child;
                        currentDepth = frame.Depth + 1;
                        moved = true;
                        break;
                    }

                    if (frame.IsObject) writer.WriteEndObject();
                    else writer.WriteEndArray();
                }
                if (!moved) break;
            }
            writer.Flush();

            if (output.WrittenCount > limits.MaxPayloadBytes)
            {
                throw new RuntimeRpcValueLimitException(RuntimeRpcValueLimitKind.Bytes);
            }
            return new RuntimeRpcValueMeasurement(
                output.WrittenCount,
                maximumDepth,
                totalNodes,
                maximumObjectMembers);
        }
    }

    private static void ValidateObjectMembers(
        TraversalFrame frame,
        RuntimeRpcPolicyOptions limits,
        ref int maximumObjectMembers)
    {
        if (!frame.IsObject) return;
        if (frame.ObjectMembers > limits.MaxObjectMembers)
        {
            throw new RuntimeRpcValueLimitException(RuntimeRpcValueLimitKind.ObjectMembers);
        }
        maximumObjectMembers = Math.Max(maximumObjectMembers, frame.ObjectMembers);
    }

    private struct TraversalFrame
    {
        private JsonElement.ArrayEnumerator _array;
        private JsonElement.ObjectEnumerator _object;

        private TraversalFrame(JsonElement value, int depth, bool isObject)
        {
            IsContainer = true;
            IsObject = isObject;
            Depth = depth;
            ObjectMembers = 0;
            _array = isObject ? default : value.EnumerateArray();
            _object = isObject ? value.EnumerateObject() : default;
        }

        public bool IsContainer { get; }
        public bool IsObject { get; }
        public int Depth { get; }
        public int ObjectMembers { get; private set; }

        public static TraversalFrame ForArray(JsonElement value, int depth)
            => new(value, depth, isObject: false);

        public static TraversalFrame ForObject(JsonElement value, int depth)
            => new(value, depth, isObject: true);

        public bool MoveNext(out JsonElement value, out string? propertyName)
        {
            if (IsObject)
            {
                if (_object.MoveNext())
                {
                    ObjectMembers++;
                    value = _object.Current.Value;
                    propertyName = _object.Current.Name;
                    return true;
                }
            }
            else if (_array.MoveNext())
            {
                value = _array.Current;
                propertyName = null;
                return true;
            }

            value = default;
            propertyName = null;
            return false;
        }
    }

    private sealed class BoundedPooledBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private readonly int _maximumWrittenBytes;
        private readonly int _maximumBufferBytes;
        private byte[]? _buffer;
        private int _exposedCapacity;
        private bool _disposed;

        public BoundedPooledBufferWriter(
            int maximumWrittenBytes,
            int maximumBufferBytes)
        {
            if (maximumWrittenBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumWrittenBytes));
            }
            if (maximumBufferBytes < maximumWrittenBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumBufferBytes));
            }
            _maximumWrittenBytes = maximumWrittenBytes;
            _maximumBufferBytes = maximumBufferBytes;
        }

        public int WrittenCount { get; private set; }

        public void Advance(int count)
        {
            if (count < 0 || count > _exposedCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            if ((long)WrittenCount + count > _maximumWrittenBytes)
            {
                throw new RuntimeRpcValueLimitException(RuntimeRpcValueLimitKind.Bytes);
            }
            WrittenCount += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer!.AsMemory(0, _exposedCapacity);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer!.AsSpan(0, _exposedCapacity);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        private void EnsureCapacity(int sizeHint)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
            if (sizeHint == 0) sizeHint = 1;
            if (sizeHint > _maximumBufferBytes)
            {
                throw new RuntimeRpcValueLimitException(RuntimeRpcValueLimitKind.Bytes);
            }
            if (_buffer is not null && sizeHint <= _exposedCapacity) return;

            var next = ArrayPool<byte>.Shared.Rent(sizeHint);
            var previous = Interlocked.Exchange(ref _buffer, next);
            if (previous is not null) ArrayPool<byte>.Shared.Return(previous, clearArray: true);
            _exposedCapacity = Math.Min(next.Length, _maximumBufferBytes);
        }
    }
}

internal static class RuntimeRpcInlineDecodedMeasurer
{
    public static long Measure(
        SunderRpcContractDescriptor contract,
        string schemaReference,
        JsonElement value,
        int maximumDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaReference);
        if (maximumDecodedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumDecodedBytes));
        if (!TryResolveReference(contract, schemaReference, out var root)) return 0;

        long total = 0;
        MeasureSingleValue(contract, root, value, maximumDecodedBytes, ref total);
        return total;
    }

    private static void MeasureSingleValue(
        SunderRpcContractDescriptor contract,
        JsonElement schema,
        JsonElement value,
        int maximumDecodedBytes,
        ref long total)
    {
        HashSet<JsonElement>? expandedUnions = null;
        while (true)
        {
            if (!TryResolveReferences(contract, schema, out schema)) return;
            if (!schema.TryGetProperty("oneOf", out var oneOf)) break;
            if (!(expandedUnions ??= []).Add(schema)) return;
            if (TrySelectUnionVariant(contract, oneOf, value, out var selected))
            {
                schema = selected;
                continue;
            }
            MeasureValue(
                contract,
                oneOf.EnumerateArray().ToArray(),
                value,
                maximumDecodedBytes,
                ref total);
            return;
        }

        if (value.ValueKind == JsonValueKind.String && IsBase64Schema(schema))
        {
            AddDecodedBytes(value.GetString()!, maximumDecodedBytes, ref total);
            return;
        }
        if (value.ValueKind == JsonValueKind.Object
            && HasType(schema, "object")
            && schema.TryGetProperty("additionalProperties", out var additional))
        {
            if (additional.ValueKind == JsonValueKind.False)
            {
                if (!schema.TryGetProperty("properties", out var properties)) return;
                foreach (var property in value.EnumerateObject())
                {
                    if (properties.TryGetProperty(property.Name, out var propertySchema))
                    {
                        MeasureSingleValue(
                            contract,
                            propertySchema,
                            property.Value,
                            maximumDecodedBytes,
                            ref total);
                    }
                }
            }
            else if (additional.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in value.EnumerateObject())
                {
                    MeasureSingleValue(
                        contract,
                        additional,
                        property.Value,
                        maximumDecodedBytes,
                        ref total);
                }
            }
            return;
        }
        if (value.ValueKind == JsonValueKind.Array
            && HasType(schema, "array")
            && schema.TryGetProperty("items", out var items))
        {
            foreach (var item in value.EnumerateArray())
            {
                MeasureSingleValue(contract, items, item, maximumDecodedBytes, ref total);
            }
        }
    }

    private static void MeasureValue(
        SunderRpcContractDescriptor contract,
        IReadOnlyList<JsonElement> candidateSchemas,
        JsonElement value,
        int maximumDecodedBytes,
        ref long total)
    {
        if (candidateSchemas.Count == 1)
        {
            MeasureSingleValue(
                contract,
                candidateSchemas[0],
                value,
                maximumDecodedBytes,
                ref total);
            return;
        }
        var schemas = ResolveApplicableSchemas(contract, candidateSchemas, value);
        if (schemas.Length == 1)
        {
            MeasureSingleValue(contract, schemas[0], value, maximumDecodedBytes, ref total);
            return;
        }
        if (value.ValueKind == JsonValueKind.String
            && schemas.Any(IsBase64Schema))
        {
            AddDecodedBytes(value.GetString()!, maximumDecodedBytes, ref total);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var childSchemas = new List<JsonElement>();
                foreach (var schema in schemas)
                {
                    if (!HasType(schema, "object")
                        || !schema.TryGetProperty("additionalProperties", out var additional))
                    {
                        continue;
                    }
                    if (additional.ValueKind == JsonValueKind.False)
                    {
                        if (schema.TryGetProperty("properties", out var properties)
                            && properties.TryGetProperty(property.Name, out var propertySchema))
                        {
                            childSchemas.Add(propertySchema);
                        }
                    }
                    else if (additional.ValueKind == JsonValueKind.Object)
                    {
                        childSchemas.Add(additional);
                    }
                }
                if (childSchemas.Count > 0)
                {
                    if (childSchemas.Count == 1)
                    {
                        MeasureSingleValue(
                            contract,
                            childSchemas[0],
                            property.Value,
                            maximumDecodedBytes,
                            ref total);
                    }
                    else
                    {
                        MeasureValue(
                            contract,
                            childSchemas,
                            property.Value,
                            maximumDecodedBytes,
                            ref total);
                    }
                }
            }
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var itemSchemas = schemas
                .Where(static schema => HasType(schema, "array"))
                .Select(static schema => schema.GetProperty("items"))
                .ToArray();
            if (itemSchemas.Length == 0) return;
            foreach (var item in value.EnumerateArray())
            {
                if (itemSchemas.Length == 1)
                {
                    MeasureSingleValue(
                        contract,
                        itemSchemas[0],
                        item,
                        maximumDecodedBytes,
                        ref total);
                }
                else
                {
                    MeasureValue(contract, itemSchemas, item, maximumDecodedBytes, ref total);
                }
            }
        }
    }

    private static JsonElement[] ResolveApplicableSchemas(
        SunderRpcContractDescriptor contract,
        IReadOnlyList<JsonElement> candidateSchemas,
        JsonElement value)
    {
        var pending = new Stack<JsonElement>(candidateSchemas.Reverse());
        var expanded = new HashSet<JsonElement>();
        var resolved = new HashSet<JsonElement>();
        while (pending.TryPop(out var candidate))
        {
            if (!TryResolveReferences(contract, candidate, out var schema)
                || !expanded.Add(schema))
            {
                continue;
            }
            if (!schema.TryGetProperty("oneOf", out var oneOf))
            {
                resolved.Add(schema);
                continue;
            }
            if (TrySelectUnionVariant(contract, oneOf, value, out var selected))
            {
                pending.Push(selected);
                continue;
            }
            foreach (var variant in oneOf.EnumerateArray()) pending.Push(variant);
        }
        return resolved.ToArray();
    }

    private static bool TrySelectUnionVariant(
        SunderRpcContractDescriptor contract,
        JsonElement oneOf,
        JsonElement value,
        out JsonElement selected)
    {
        var variants = oneOf.EnumerateArray().ToArray();
        var resolved = new JsonElement[variants.Length];
        for (var index = 0; index < variants.Length; index++)
        {
            if (!TryResolveReferences(contract, variants[index], out resolved[index]))
            {
                selected = default;
                return false;
            }
        }

        var nullIndex = Array.FindIndex(resolved, static schema =>
            schema.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && type.GetString() == "null");
        if (variants.Length == 2 && nullIndex >= 0)
        {
            selected = value.ValueKind == JsonValueKind.Null
                ? variants[nullIndex]
                : variants[1 - nullIndex];
            return true;
        }
        if (value.ValueKind != JsonValueKind.Object
            || !resolved[0].TryGetProperty("properties", out var firstProperties))
        {
            selected = default;
            return false;
        }
        var firstRequired = resolved[0].GetProperty("required")
            .EnumerateArray()
            .Select(static item => item.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in firstProperties.EnumerateObject())
        {
            if (!firstRequired.Contains(candidate.Name)
                || !value.TryGetProperty(candidate.Name, out var discriminator)
                || discriminator.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            var tags = new HashSet<string>(StringComparer.Ordinal);
            var matchingIndex = -1;
            var validDiscriminator = true;
            for (var index = 0; index < resolved.Length; index++)
            {
                if (!resolved[index].TryGetProperty("properties", out var properties)
                    || !resolved[index].GetProperty("required").EnumerateArray().Any(item =>
                        string.Equals(item.GetString(), candidate.Name, StringComparison.Ordinal))
                    || !properties.TryGetProperty(candidate.Name, out var propertySchema)
                    || !TryResolveReferences(contract, propertySchema, out var resolvedPropertySchema)
                    || !HasType(resolvedPropertySchema, "string")
                    || !resolvedPropertySchema.TryGetProperty("const", out var constant)
                    || constant.ValueKind != JsonValueKind.String
                    || !tags.Add(constant.GetString()!))
                {
                    validDiscriminator = false;
                    break;
                }
                if (string.Equals(
                        discriminator.GetString(),
                        constant.GetString(),
                        StringComparison.Ordinal))
                {
                    matchingIndex = index;
                }
            }
            if (validDiscriminator && matchingIndex >= 0)
            {
                selected = variants[matchingIndex];
                return true;
            }
        }

        selected = default;
        return false;
    }

    private static bool TryResolveReferences(
        SunderRpcContractDescriptor contract,
        JsonElement schema,
        out JsonElement resolved)
    {
        HashSet<string>? visited = null;
        while (schema.TryGetProperty("$ref", out var reference))
        {
            if (reference.ValueKind != JsonValueKind.String
                || !SunderRpcDescriptorParser.TryGetReferenceName(reference.GetString(), out var name)
                || !(visited ??= new HashSet<string>(StringComparer.Ordinal)).Add(name)
                || !contract.Definitions.TryGetValue(name, out schema))
            {
                resolved = default;
                return false;
            }
        }
        resolved = schema;
        return true;
    }

    private static bool TryResolveReference(
        SunderRpcContractDescriptor contract,
        string reference,
        out JsonElement schema)
    {
        if (SunderRpcDescriptorParser.TryGetReferenceName(reference, out var name)
            && contract.Definitions.TryGetValue(name, out schema))
        {
            return true;
        }
        schema = default;
        return false;
    }

    private static long GetDecodedLength(string value)
    {
        if (value.Length == 0) return 0;
        var padding = value.EndsWith("==", StringComparison.Ordinal) ? 2
            : value.EndsWith('=') ? 1
            : 0;
        return Math.Max(0, ((long)value.Length + 3) / 4 * 3 - padding);
    }

    private static void AddDecodedBytes(
        string value,
        int maximumDecodedBytes,
        ref long total)
    {
        var decodedBytes = GetDecodedLength(value);
        if (decodedBytes > maximumDecodedBytes - total)
        {
            throw new RuntimeRpcValueLimitException(RuntimeRpcValueLimitKind.InlineDecodedBytes);
        }
        total += decodedBytes;
    }

    private static bool IsBase64Schema(JsonElement schema)
        => HasType(schema, "string")
           && schema.TryGetProperty("contentEncoding", out var encoding)
           && encoding.ValueKind == JsonValueKind.String
           && encoding.GetString() == "base64";

    private static bool HasType(JsonElement schema, string expected)
        => schema.TryGetProperty("type", out var type)
           && type.ValueKind == JsonValueKind.String
           && type.GetString() == expected;
}
