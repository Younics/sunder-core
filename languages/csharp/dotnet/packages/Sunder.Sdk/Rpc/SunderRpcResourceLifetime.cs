using System.ComponentModel;
using Sunder.Sdk.Compatibility;

namespace Sunder.Sdk.Rpc;

/// <summary>Owns outstanding generated service-call resources for a receiver's dependency-injection lifetime.</summary>
/// <remarks>For generated bindings. Completed calls without retained content are removed immediately.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public sealed class SunderRpcResourceLifetime : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly HashSet<SunderRpcResourceOwner> _owners = [];
    private SunderRpcServiceBindingLifetime? _serviceBindings;
    private readonly bool _ownsServiceBindings;
    private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _creating;
    private Task? _dispose;

    /// <summary>Creates a generated-resource owner for one receiving DI lifetime.</summary>
    public SunderRpcResourceLifetime() => _ownsServiceBindings = true;

    /// <summary>Creates resource ownership borrowing the receiver's shared DI binding observer.</summary>
    /// <remarks>The receiver's DI scope owns and disposes <paramref name="serviceBindings"/> separately.</remarks>
    public SunderRpcResourceLifetime(SunderRpcServiceBindingLifetime serviceBindings)
        => _serviceBindings = serviceBindings ?? throw new ArgumentNullException(nameof(serviceBindings));

    /// <summary>Creates and owns a bounded call scope, registering request content only for the exact target.</summary>
    /// <remarks>
    /// Dispose the returned context at operation completion. Received content retains shared leases until
    /// disposed; cancellation, the scope deadline and disposal of this lifetime revoke them earlier.
    /// Pass the original method and enumeration tokens separately. Their link to this DI lifetime is
    /// owned by the shared resource lease, so ending an iterator does not sever later cancellation.
    /// A generated exact proxy also supplies its provider retirement token, which remains linked for retained content.
    /// </remarks>
    public async ValueTask<SunderRpcMarshallingContext> CreateCallAsync(ISunderRpcClient client,
        SunderRpcEndpointReference target, SunderRpcCallOptions? options = null,
        CancellationToken cancellationToken = default, CancellationToken enumerationCancellationToken = default,
        CancellationToken providerRetirementToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(target);
        CancellationTokenSource cancellation;
        SunderRpcServiceBindingLifetime bindings;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            bindings = _serviceBindings ??= new(client);
            if (!ReferenceEquals(bindings.Client, client))
                throw new ArgumentException("The resource lifetime belongs to a different receiving RPC client.", nameof(client));
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, enumerationCancellationToken, providerRetirementToken, _stopping.Token, bindings.LifetimeToken);
            _creating++;
        }
        ISunderRpcCallScope? scope = null;
        SunderRpcResourceOwner? owner = null;
        try
        {
            scope = await client.CreateCallScopeAsync(options, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            owner = new(scope, scope.DeadlineUtc, cancellation, Remove);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_dispose is not null, this);
                _owners.Add(owner);
            }
            owner.ObserveCancellation();
            owner.Token.ThrowIfCancellationRequested();
            return new(owner, scope, target, client, bindings);
        }
        catch
        {
            if (owner is not null) await owner.DisposeAsync().ConfigureAwait(false);
            else
            {
                try { if (scope is not null) await scope.DisposeAsync().ConfigureAwait(false); }
                finally { cancellation.Dispose(); }
            }
            throw;
        }
        finally
        {
            lock (_gate) if (--_creating == 0 && _dispose is not null) _created.TrySetResult();
        }
    }

    private void Remove(SunderRpcResourceOwner owner)
    {
        lock (_gate) _owners.Remove(owner);
    }

    /// <summary>Revokes and drains every outstanding generated call resource.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>Revokes and drains every outstanding generated call resource.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        SunderRpcResourceOwner[] owners;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispose = completion.Task;
            owners = _owners.ToArray();
            if (_creating == 0) _created.TrySetResult();
        }
        _ = DisposeCoreAsync(owners, completion);
        return new(completion.Task);
    }

    private async Task DisposeCoreAsync(SunderRpcResourceOwner[] owners, TaskCompletionSource completion)
    {
        try
        {
            try { await _stopping.CancelAsync().ConfigureAwait(false); }
            finally
            {
                try { await Task.WhenAll(owners.Select(static owner => owner.DisposeAsync().AsTask())).ConfigureAwait(false); }
                finally
                {
                    try
                    {
                        await _created.Task.ConfigureAwait(false);
                        if (_ownsServiceBindings && _serviceBindings is not null)
                            await _serviceBindings.DisposeAsync().ConfigureAwait(false);
                    }
                    finally { _stopping.Dispose(); }
                }
            }
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
}

internal sealed class SunderRpcResourceOwner : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ISunderRpcCallScope? _scope;
    private readonly CancellationTokenSource _cancellation;
    private readonly Action<SunderRpcResourceOwner> _removed;
    private readonly Timer _deadline;
    private readonly DateTimeOffset _deadlineUtc;
    private CancellationTokenRegistration _registration;
    private readonly Dictionary<SunderRpcServiceBinding, CancellationTokenRegistration> _participants = [];
    private int _references = 1;
    private Task? _dispose;

    internal SunderRpcResourceOwner(ISunderRpcCallScope? scope, DateTimeOffset deadline,
        CancellationTokenSource cancellation, Action<SunderRpcResourceOwner> removed)
    {
        _scope = scope;
        _cancellation = cancellation;
        _removed = removed;
        Token = cancellation.Token;
        _deadlineUtc = deadline;
        _deadline = new Timer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal CancellationToken Token { get; }

    internal void RetainParticipant(SunderRpcServiceBinding binding)
    {
        binding.ThrowIfRetired();
        lock (_gate)
        {
            Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            if (_participants.ContainsKey(binding)) return;
            if (_participants.Count >= 4096) throw new SunderRpcException(new(SunderRpcErrorKind.ResourceExhausted,
                "rpc.binding.participant-limit", "The operation's service participant limit was reached."));
            _participants.Add(binding, default);
        }
        // Retain the binding and cancellation registration until the last resource lease, not just
        // the request serializer or iterator. Registration observes an already-retired binding too.
        var registration = binding.Token.UnsafeRegister(static state => ((SunderRpcResourceOwner)state!).Expire(), this);
        lock (_gate)
        {
            if (_dispose is null) { _participants[binding] = registration; return; }
        }
        registration.Unregister();
        Token.ThrowIfCancellationRequested();
    }

    internal void ObserveCancellation()
    {
        var registration = Token.UnsafeRegister(static state => ((SunderRpcResourceOwner)state!).Expire(), this);
        lock (_gate)
        {
            if (_dispose is null)
            {
                _registration = registration;
                var remaining = _deadlineUtc - DateTimeOffset.UtcNow;
                // Host call deadlines are bounded; cap exceptionally distant clocks without overflowing Timer.
                _deadline.Change(remaining <= TimeSpan.Zero ? TimeSpan.Zero
                    : TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, uint.MaxValue - 1)), Timeout.InfiniteTimeSpan);
                return;
            }
        }
        registration.Unregister();
    }

    private async void Expire()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch { } // The shared disposal task is observed by the operation/lifetime disposer.
    }

    internal void Retain()
    {
        lock (_gate)
        {
            Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_dispose is not null, this);
            checked { _references++; }
        }
    }

    internal ValueTask ReleaseAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            if (--_references != 0) return ValueTask.CompletedTask;
            // Publish retirement while the final reference is protected, but never
            // dispose a linked CTS under this gate: it can wait for a callback
            // concurrently entering DisposeAsync on this same owner.
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispose = completion.Task;
        }
        _ = DisposeCoreAsync(completion);
        return new(completion.Task);
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispose = completion.Task;
        }
        _ = DisposeCoreAsync(completion);
        return new(completion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            try
            {
                _deadline.Dispose();
                _registration.Unregister();
                CancellationTokenRegistration[] participants;
                lock (_gate)
                {
                    participants = _participants.Values.ToArray();
                    _participants.Clear();
                }
                foreach (var participant in participants) participant.Unregister();
                try
                {
                    if (!_cancellation.IsCancellationRequested) await _cancellation.CancelAsync().ConfigureAwait(false);
                }
                finally { if (_scope is not null) await _scope.DisposeAsync().ConfigureAwait(false); }
            }
            finally { _cancellation.Dispose(); _removed(this); }
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }
}
