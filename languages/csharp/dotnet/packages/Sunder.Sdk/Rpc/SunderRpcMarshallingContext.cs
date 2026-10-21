using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Services;

namespace Sunder.Sdk.Rpc;

/// <summary>Provides explicit asynchronous resource preparation and JSON conversion for one generated operation.</summary>
/// <remarks>
/// For generated bindings. Visitors register known resource members, including nested members, before
/// serialization. This context never reflects over value graphs and is never ambient. Use a distinct
/// context per operation and process stream elements sequentially. Generated converters may be added
/// to <see cref="JsonOptions"/> before its first use.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed partial class SunderRpcMarshallingContext : IAsyncDisposable
{
    private readonly SunderRpcResourceOwner _owner;
    private readonly ISunderRpcCallScope? _scope;
    private readonly SunderRpcEndpointReference? _target;
    private readonly SunderRpcInvocationContext? _invocation;
    private readonly SunderRpcServiceBindingLifetime? _serviceBindingLifetime;
    private readonly OperationScope? _operation;
    private readonly Dictionary<SunderContent, SunderRpcContentReference> _outgoing = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SunderRpcContentReference, SunderContent> _incoming = [];
    private readonly SemaphoreSlim _registrationGate = new(1, 1);
    private readonly SemaphoreSlim _valueGate = new(1, 1);
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;

    internal SunderRpcMarshallingContext(SunderRpcResourceOwner owner, ISunderRpcCallScope scope,
        SunderRpcEndpointReference target, ISunderRpcClient receiver, SunderRpcServiceBindingLifetime serviceBindings)
    {
        _owner = owner;
        _scope = scope;
        _target = target;
        _operation = new(this, scope, target);
        Client = _operation;
        _owner.NormalizeDisposalFailure = error => SunderRpcDispatchEvidence.AfterDispatch(error);
        _receiver = receiver;
        _serviceBindingLifetime = serviceBindings;
        JsonOptions = CreateOptions();
    }

    private SunderRpcMarshallingContext(SunderRpcInvocationContext invocation, ISunderRpcClient? receiver,
        SunderRpcServiceBindingLifetime? serviceBindings)
    {
        _invocation = invocation;
        Client = receiver;
        _receiver = receiver;
        _serviceBindingLifetime = serviceBindings;
        if (serviceBindings is not null && !ReferenceEquals(serviceBindings.Client, receiver))
            throw new ArgumentException("The service bindings belong to a different receiver.", nameof(serviceBindings));
        _owner = new(null, invocation.DeadlineUtc, CancellationTokenSource.CreateLinkedTokenSource(
            invocation.CancellationToken, invocation.RevocationToken, serviceBindings?.LifetimeToken ?? default), _ => { });
        _owner.ObserveCancellation();
        JsonOptions = CreateOptions();
    }

    /// <summary>Borrows existing invocation authority for request reads and response registration.</summary>
    /// <remarks>The receiver client must be the provider's own DI client when decoding service references.</remarks>
    public static SunderRpcMarshallingContext ForInvocation(SunderRpcInvocationContext invocation,
        ISunderRpcClient? receiver = null, SunderRpcServiceBindingLifetime? serviceBindings = null)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return new(invocation, receiver, serviceBindings);
    }

    /// <summary>Gets the caller scope, or the receiver client supplied for a provider invocation.</summary>
    public ISunderRpcClient? Client { get; }

    /// <summary>Gets cancellation including original call tokens, DI retirement and visited request participants.</summary>
    /// <remarks>Generated provider adapters pass this token to domain methods and server-stream enumeration.</remarks>
    public CancellationToken CancellationToken => _owner.Token;

    /// <summary>Gets options containing this operation's content and generated reference maps.</summary>
    public JsonSerializerOptions JsonOptions { get; }

    private JsonSerializerOptions CreateOptions()
        => new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            Converters = { new ContentConverter(this), new SunderRpcJsonConverterFactory() },
        };

    /// <summary>Registers one visited content value once within the current serialized value.</summary>
    /// <remarks>Null is a no-op. Existing received content is copied through receiving authority, never forwarded as a grant.</remarks>
    public async ValueTask RegisterContentAsync(SunderContent? content, CancellationToken cancellationToken = default)
    {
        Check();
        if (content is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_owner.Token, cancellationToken);
        await _registrationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            content.ThrowIfUnavailable();
            if (_outgoing.ContainsKey(content)) return;
            await using var stream = await content.OpenReadAsync(linked.Token).ConfigureAwait(false);
            var options = new SunderRpcContentRegistrationOptions(content.MediaType, content.FileName, content.Length);
            var reference = _scope is not null
                ? await _scope.RegisterContentAsync(_target!, stream, options, linked.Token).ConfigureAwait(false)
                : await RegisterResponseContentAsync(stream, options, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            _outgoing.Add(content, reference);
        }
        finally { _registrationGate.Release(); }
    }

    /// <summary>Runs a generated registration visitor, then serializes using this operation's resource maps.</summary>
    public async ValueTask<JsonElement> SerializeAsync<T>(T value,
        Func<SunderRpcMarshallingContext, T, CancellationToken, ValueTask>? register = null,
        CancellationToken cancellationToken = default)
        => (await SerializeCoreAsync(value, register, cancellationToken, completeOnRetirement: false).ConfigureAwait(false))!.Value;

    private async ValueTask<JsonElement?> SerializeCoreAsync<T>(T value,
        Func<SunderRpcMarshallingContext, T, CancellationToken, ValueTask>? register,
        CancellationToken cancellationToken, bool completeOnRetirement)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_owner.Token, cancellationToken);
        await _valueGate.WaitAsync(linked.Token).ConfigureAwait(false);
        var attempt = completeOnRetirement ? new object() : null;
        _responseStreamAttempt = attempt;
        try
        {
            try
            {
                if (register is not null) await register(this, value, linked.Token).ConfigureAwait(false);
            }
            catch (ResponseRegistrationRetired exception) when (attempt is not null && ReferenceEquals(exception.Attempt, attempt))
            {
                linked.Token.ThrowIfCancellationRequested();
                return null;
            }
            linked.Token.ThrowIfCancellationRequested();
            return JsonSerializer.SerializeToElement(value, JsonOptions);
        }
        catch (ResponseRegistrationRetired exception)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.Cause).Throw();
            throw;
        }
        catch (Exception error)
        {
            var normalized = _operation?.Normalize(error) ?? error;
            if (!ReferenceEquals(normalized, error)) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(normalized).Throw();
            throw;
        }
        finally { _responseStreamAttempt = null; _outgoing.Clear(); ClearOutgoingReferences(); _valueGate.Release(); }
    }

    /// <summary>Prepares generated reference paths, then decodes a value and transfers its content leases to the result.</summary>
    /// <remarks>A failed decode releases all content created for that value. Stream elements do not accumulate in this context.</remarks>
    public async ValueTask<T> DeserializeAsync<T>(JsonElement value,
        Func<SunderRpcMarshallingContext, JsonElement, CancellationToken, ValueTask>? prepare = null,
        CancellationToken cancellationToken = default)
    {
        Check();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_owner.Token, cancellationToken);
        await _valueGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (prepare is not null) await prepare(this, value, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            var result = value.Deserialize<T>(JsonOptions);
            linked.Token.ThrowIfCancellationRequested();
            return result!;
        }
        catch (Exception error)
        {
            try { await Task.WhenAll(_incoming.Values.Select(static content => content.DisposeAsync().AsTask())).ConfigureAwait(false); }
            catch (Exception cleanup)
            {
                var failedCleanup = _operation?.Normalize(cleanup) ?? cleanup;
                if (!ReferenceEquals(failedCleanup, cleanup)) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failedCleanup).Throw();
                throw;
            }
            var normalized = _operation?.Normalize(error) ?? error;
            if (!ReferenceEquals(normalized, error)) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(normalized).Throw();
            throw;
        }
        finally { _incoming.Clear(); ClearIncomingReferences(); _valueGate.Release(); }
    }

    private SunderContent Receive(SunderRpcContentReference reference)
    {
        Check();
        if (_incoming.TryGetValue(reference, out var existing)) return existing;
        _owner.Retain();
        var scope = _scope;
        var invocation = _invocation;
        var content = new SunderContent(reference.MediaType, reference.FileName, reference.Length, reference.Sha256,
            reference.ExpiresAtUtc, reference.Repeatability == SunderRpcContentRepeatability.Repeatable,
            token => scope is not null ? scope.OpenContentAsync(reference, token) : invocation!.OpenContentAsync(reference, token),
            _owner.ReleaseAsync, _owner.Token);
        _incoming.Add(reference, content);
        return content;
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_operation is not null) _operation.ThrowIfCancelled(_owner.Token);
        else _owner.Token.ThrowIfCancellationRequested();
        CheckRequiredReferences();
    }

    /// <summary>Releases the operation lease; caller response content retains only its bounded shared owner.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            Volatile.Write(ref _disposed, 1);
            _outgoing.Clear();
            _incoming.Clear();
            ClearOutgoingReferences();
            ClearIncomingReferences();
            _disposeTask = (_invocation is null ? _owner.ReleaseAsync() : _owner.DisposeAsync()).AsTask();
            return new(_disposeTask);
        }
    }

    partial void ClearOutgoingReferences();
    partial void ClearIncomingReferences();
    partial void CheckRequiredReferences();

    private sealed class ContentConverter(SunderRpcMarshallingContext context) : JsonConverter<SunderContent>
    {
        public override SunderContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => context.Receive(JsonSerializer.Deserialize<SunderRpcContentReference>(ref reader, options)
                ?? throw new JsonException("A content reference cannot be null."));

        public override void Write(Utf8JsonWriter writer, SunderContent value, JsonSerializerOptions options)
        {
            context.Check();
            value.ThrowIfUnavailable();
            if (!context._outgoing.TryGetValue(value, out var reference))
                throw new JsonException("The generated visitor must register content before serialization.");
            JsonSerializer.Serialize(writer, reference, options);
        }
    }
}
