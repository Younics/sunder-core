using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;
using static Sunder.Runtime.Host.Tests.RuntimeRpcTestPackages;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcCallScopeRaceTests
{
    [Theory]
    [InlineData("retirement", false)]
    [InlineData("deadline", true)]
    [InlineData("explicit-close", true)]
    public async Task CallScopeRegistry_PreservesPathSpecificTeardownOrder(
        string path,
        bool authorityRevokedBeforeDiscard)
    {
        var clock = new ManualTimeProvider();
        using var sessionRetirement = new CancellationTokenSource();
        using var replacementSessionRetirement = new CancellationTokenSource();
        var caller = new RuntimeRpcHostCallerActivation(Contract(), CancellationToken.None);
        var generationIdentity = new object();
        RuntimeRpcCallScopeRegistry? registry = null;
        RuntimeRpcCallerScopeState? replacement = null;
        var replacementOutcome = RuntimeRpcCallScopeRegistry.Outcome.Unavailable;
        var discardObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discardCount = 0;
        var observedAuthorityRevoked = false;
        registry = new RuntimeRpcCallScopeRegistry(
            maxScopesPerCaller: 1,
            clock,
            CancellationToken.None,
            authority =>
            {
                if (Interlocked.Increment(ref discardCount) != 1)
                {
                    authority.Revoke();
                    return;
                }
                observedAuthorityRevoked = authority.IsRevoked;
                replacementOutcome = registry!.Register(
                    caller,
                    sessionGeneration: 7,
                    generationIdentity,
                    clock.GetUtcNow().AddMinutes(5),
                    parentFrame: null,
                    requirePermission: false,
                    replacementSessionRetirement.Token,
                    generationRetirement: CancellationToken.None,
                    deadlineDelay: TimeSpan.FromMinutes(5),
                    out replacement);
                authority.Revoke();
                discardObserved.TrySetResult();
            });
        var registration = registry.Register(
            caller,
            sessionGeneration: 7,
            generationIdentity,
            clock.GetUtcNow().AddMinutes(1),
            parentFrame: null,
            requirePermission: false,
            sessionRetirement.Token,
            generationRetirement: CancellationToken.None,
            deadlineDelay: TimeSpan.FromMinutes(1),
            out var registeredScope);
        Assert.Equal(RuntimeRpcCallScopeRegistry.Outcome.Success, registration);
        var scope = Assert.IsType<RuntimeRpcCallerScopeState>(registeredScope);
        var originalRevocation = scope.RevocationToken;
        using var callbackRelease = new ManualResetEventSlim();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration callbackRegistration = default;
        Task? close = null;

        try
        {
            switch (path)
            {
                case "retirement":
                    sessionRetirement.Cancel();
                    break;
                case "deadline":
                    clock.Advance(TimeSpan.FromMinutes(1));
                    break;
                case "explicit-close":
                    callbackRegistration = scope.RevocationToken.Register(() =>
                    {
                        callbackStarted.TrySetResult();
                        callbackRelease.Wait();
                    });
                    close = Task.Run(async () => await registry.CloseAsync(scope));
                    await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown call-scope teardown path '{path}'.");
            }

            await discardObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(authorityRevokedBeforeDiscard, observedAuthorityRevoked);
            Assert.Equal(RuntimeRpcCallScopeRegistry.Outcome.Success, replacementOutcome);
            var replacementScope = Assert.IsType<RuntimeRpcCallerScopeState>(replacement);
            Assert.Equal(
                RuntimeRpcCallScopeRegistry.Outcome.Unavailable,
                registry.Get(caller, scope.Id, out _));
            Assert.Equal(
                RuntimeRpcCallScopeRegistry.Outcome.Success,
                registry.Get(caller, replacementScope.Id, out var foundReplacement));
            Assert.Same(replacementScope, foundReplacement);

            if (close is not null)
            {
                Assert.False(close.IsCompleted);
                callbackRelease.Set();
                await close.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            callbackRelease.Set();
            callbackRegistration.Dispose();
            await registry.CloseAsync(scope);
            Assert.Equal(1, discardCount);
            if (replacement is not null) await registry.CloseAsync(replacement);
        }

        Assert.Equal(originalRevocation, scope.RevocationToken);
        Assert.True(scope.RevocationToken.IsCancellationRequested);
        Assert.True(scope.IsRevoked);
        using var lateLinked = CancellationTokenSource.CreateLinkedTokenSource(scope.RevocationToken);
        Assert.True(lateLinked.IsCancellationRequested);
        Assert.Equal(path == "deadline" ? RuntimeRpcCallScopeRegistry.Outcome.DeadlineExceeded : RuntimeRpcCallScopeRegistry.Outcome.Unavailable,
            registry.Validate(scope, caller, 7, generationIdentity));
        Assert.Equal(2, discardCount);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact]
    public async Task CallScopeRegistry_AlreadyRetiredRegistrationReturnsNullAndReleasesCapacity()
    {
        var clock = new ManualTimeProvider();
        using var retired = new CancellationTokenSource();
        using var replacementRetirement = new CancellationTokenSource();
        retired.Cancel();
        var caller = new RuntimeRpcHostCallerActivation(Contract(), CancellationToken.None);
        var generationIdentity = new object();
        var discardCount = 0;
        var registry = new RuntimeRpcCallScopeRegistry(
            maxScopesPerCaller: 1,
            clock,
            CancellationToken.None,
            authority =>
            {
                Interlocked.Increment(ref discardCount);
                authority.Revoke();
            });

        var outcome = registry.Register(
            caller,
            sessionGeneration: 7,
            generationIdentity,
            clock.GetUtcNow().AddMinutes(1),
            parentFrame: null,
            requirePermission: false,
            retired.Token,
            generationRetirement: CancellationToken.None,
            deadlineDelay: TimeSpan.FromMinutes(1),
            out var failedScope);

        Assert.Equal(RuntimeRpcCallScopeRegistry.Outcome.Unavailable, outcome);
        Assert.Null(failedScope);
        Assert.Equal(1, discardCount);
        Assert.Equal(0, clock.ActiveTimerCount);

        var replacementOutcome = registry.Register(
            caller,
            sessionGeneration: 7,
            generationIdentity,
            clock.GetUtcNow().AddMinutes(1),
            parentFrame: null,
            requirePermission: false,
            replacementRetirement.Token,
            generationRetirement: CancellationToken.None,
            deadlineDelay: TimeSpan.FromMinutes(1),
            out var replacement);
        Assert.Equal(RuntimeRpcCallScopeRegistry.Outcome.Success, replacementOutcome);
        await registry.CloseAsync(Assert.IsType<RuntimeRpcCallerScopeState>(replacement));

        Assert.Equal(2, discardCount);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallScopeRegistry_RevocationDuringTimerCreationIsNotPublished(
        bool deadlineElapsed)
    {
        var clock = new ManualTimeProvider();
        using var sessionRetirement = new CancellationTokenSource();
        using var replacementRetirement = new CancellationTokenSource();
        var caller = new RuntimeRpcHostCallerActivation(Contract(), CancellationToken.None);
        var generationIdentity = new object();
        var discardCount = 0;
        var registry = new RuntimeRpcCallScopeRegistry(
            maxScopesPerCaller: 1,
            clock,
            CancellationToken.None,
            authority =>
            {
                Interlocked.Increment(ref discardCount);
                authority.Revoke();
            });
        clock.OnTimerCreated = () =>
        {
            clock.OnTimerCreated = null;
            if (deadlineElapsed)
            {
                clock.Advance(TimeSpan.FromMinutes(1));
            }
            else
            {
                sessionRetirement.Cancel();
            }
        };

        var outcome = registry.Register(
            caller,
            sessionGeneration: 7,
            generationIdentity,
            clock.GetUtcNow().AddMinutes(1),
            parentFrame: null,
            requirePermission: false,
            sessionRetirement.Token,
            generationRetirement: CancellationToken.None,
            deadlineDelay: TimeSpan.FromMinutes(1),
            out var failedScope);

        Assert.Equal(
            deadlineElapsed
                ? RuntimeRpcCallScopeRegistry.Outcome.DeadlineExceeded
                : RuntimeRpcCallScopeRegistry.Outcome.Unavailable,
            outcome);
        Assert.Null(failedScope);
        Assert.Equal(1, discardCount);
        Assert.Equal(0, clock.ActiveTimerCount);

        var replacementOutcome = registry.Register(
            caller,
            sessionGeneration: 7,
            generationIdentity,
            clock.GetUtcNow().AddMinutes(1),
            parentFrame: null,
            requirePermission: false,
            replacementRetirement.Token,
            generationRetirement: CancellationToken.None,
            deadlineDelay: TimeSpan.FromMinutes(1),
            out var replacement);
        Assert.Equal(RuntimeRpcCallScopeRegistry.Outcome.Success, replacementOutcome);
        await registry.CloseAsync(Assert.IsType<RuntimeRpcCallerScopeState>(replacement));

        Assert.Equal(2, discardCount);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallScopeRegistry_CompetingRetireDeadlineAndCloseTeardownExactlyOnce(bool byOwnerId)
    {
        var clock = new ManualTimeProvider();
        using var sessionRetirement = new CancellationTokenSource();
        using var replacementRetirement = new CancellationTokenSource();
        using var releaseDiscard = new ManualResetEventSlim();
        var discardStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new RuntimeRpcHostCallerActivation(Contract(), CancellationToken.None);
        var generationIdentity = new object();
        RuntimeRpcContentAuthority? retiringAuthority = null;
        var retiringDiscardCount = 0;
        var totalDiscardCount = 0;
        var registry = new RuntimeRpcCallScopeRegistry(
            maxScopesPerCaller: 1,
            clock,
            CancellationToken.None,
            authority =>
            {
                Interlocked.Increment(ref totalDiscardCount);
                if (ReferenceEquals(authority, retiringAuthority))
                {
                    Interlocked.Increment(ref retiringDiscardCount);
                    discardStarted.TrySetResult();
                    releaseDiscard.Wait();
                }
                authority.Revoke();
            });
        var registration = registry.Register(
            caller,
            sessionGeneration: 7,
            generationIdentity,
            clock.GetUtcNow().AddMinutes(1),
            parentFrame: null,
            requirePermission: false,
            sessionRetirement.Token,
            generationRetirement: CancellationToken.None,
            deadlineDelay: TimeSpan.FromMinutes(1),
            out var registeredScope);
        Assert.Equal(RuntimeRpcCallScopeRegistry.Outcome.Success, registration);
        var scope = Assert.IsType<RuntimeRpcCallerScopeState>(registeredScope);
        retiringAuthority = scope.ContentAuthority;
        RuntimeRpcCallerScopeState? replacement = null;
        var retirement = Task.Run(() => sessionRetirement.Cancel());
        Task close;

        try
        {
            await discardStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            close = byOwnerId ? registry.CloseAsync(caller, scope.Id).AsTask() : registry.CloseAsync(scope).AsTask();
            clock.Advance(TimeSpan.FromMinutes(1));

            Assert.False(retirement.IsCompleted);
            Assert.False(close.IsCompleted);
            Assert.Equal(1, retiringDiscardCount);
            Assert.Equal(1, totalDiscardCount);

            var replacementOutcome = registry.Register(
                caller,
                sessionGeneration: 7,
                generationIdentity,
                clock.GetUtcNow().AddMinutes(5),
                parentFrame: null,
                requirePermission: false,
                replacementRetirement.Token,
                generationRetirement: CancellationToken.None,
                deadlineDelay: TimeSpan.FromMinutes(5),
                out replacement);
            Assert.Equal(RuntimeRpcCallScopeRegistry.Outcome.Success, replacementOutcome);
            Assert.NotNull(replacement);
        }
        finally
        {
            releaseDiscard.Set();
        }

        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, retiringDiscardCount);
        Assert.Equal(1, totalDiscardCount);
        await registry.CloseAsync(Assert.IsType<RuntimeRpcCallerScopeState>(replacement));

        Assert.Equal(1, retiringDiscardCount);
        Assert.Equal(2, totalDiscardCount);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact]
    public async Task CallScope_ExpiryDuringRegistrationCleansPartialContentAndScope()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new RpcFixture(
            new RuntimeRpcPolicyOptions
            {
                DefaultDeadline = TimeSpan.FromMinutes(5),
                MaxCallScopesPerCaller = 1,
            },
            clock);
        var activation = await fixture.PublishAsync(new TestHandler());
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        var scope = await client.CreateCallScopeAsync(
            new SunderRpcCallOptions(clock.GetUtcNow().AddMinutes(1)));
        await using var source = new CancellationBlockingReadStream();
        var registration = scope.RegisterContentAsync(
            endpoint,
            source,
            new SunderRpcContentRegistrationOptions("application/octet-stream", "request.bin"))
            .AsTask();
        await source.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        clock.Advance(TimeSpan.FromMinutes(1));

        var expired = await Assert.ThrowsAsync<SunderRpcException>(async () => await registration);
        Assert.Equal(SunderRpcErrorKind.DeadlineExceeded, expired.Error.Kind);
        Assert.Empty(fixture.GetTransferFiles());
        await using var replacement = await client.CreateCallScopeAsync();
        await scope.DisposeAsync();
    }

    [Fact]
    public async Task CallScope_DeadlineAndDisposeRaceLeavesNoTimersOrScopeSlots()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = new RpcFixture(
            new RuntimeRpcPolicyOptions
            {
                DefaultDeadline = TimeSpan.FromMinutes(5),
                MaxCallScopesPerCaller = 1,
            },
            clock);
        var activation = await fixture.PublishAsync(new TestHandler());
        var client = fixture.CreateClient(activation.Caller);

        for (var iteration = 0; iteration < 20; iteration++)
        {
            var scope = await client.CreateCallScopeAsync(
                new SunderRpcCallOptions(clock.GetUtcNow().AddSeconds(1)));
            using var start = new ManualResetEventSlim();
            var dispose = Task.Run(async () =>
            {
                start.Wait();
                await scope.DisposeAsync();
            });
            var expire = Task.Run(() =>
            {
                start.Wait();
                clock.Advance(TimeSpan.FromSeconds(1));
            });
            start.Set();
            await Task.WhenAll(dispose, expire).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(0, clock.ActiveTimerCount);
        await using var finalScope = await client.CreateCallScopeAsync();
    }

    private sealed class CancellationBlockingReadStream : Stream
    {
        private int _reads;

        public TaskCompletionSource Blocked { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                buffer.Span[0] = 1;
                return 1;
            }
            Blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
