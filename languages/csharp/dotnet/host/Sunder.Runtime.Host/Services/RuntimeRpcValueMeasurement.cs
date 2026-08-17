using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Sunder.Runtime.Host.Services;

internal enum RuntimeRpcValueLimitKind
{
    Bytes,
    Depth,
    Nodes,
    ObjectMembers,
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
    int MaximumObjectMembers);

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
                   Encoder = JavaScriptEncoder.Default,
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
