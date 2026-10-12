using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed partial class SunderServiceResourceTests
{
    [Fact]
    public async Task InvocationUsesExistingRequestAndResponseAuthorityAndDoesNotExtendHandlerLifetime()
    {
        using var authority = new InvocationAuthority();
        var invocation = new SunderRpcInvocationContext("caller.package", "1.0.0", ServiceResourceTestClient.Provider,
            DateTimeOffset.UtcNow.AddMinutes(5), 1, CancellationToken.None, authority);
        var receiver = new ServiceResourceTestClient();
        var operation = SunderRpcMarshallingContext.ForInvocation(invocation, receiver);
        var received = await operation.DeserializeAsync<SunderContent>(ServiceResourceTestClient.ContentJson());
        var read = await received.OpenReadAsync();
        Assert.Equal(4, read.ReadByte());
        await using var response = SunderContent.FromBytes(new byte[] { 9, 8, 7 });
        var json = await operation.SerializeAsync(response,
            static (context, content, token) => context.RegisterContentAsync(content, token));
        Assert.Equal("provider-output", json.GetProperty("id").GetString());
        Assert.Equal(new byte[] { 9, 8, 7 }, authority.Output);
        Assert.Empty(receiver.Scopes);
        await operation.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await received.OpenReadAsync());
        await read.DisposeAsync();
        await received.DisposeAsync();
        Assert.Equal(1, authority.Read!.Disposals);
        Assert.False(authority.RevocationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task HostProviderRetirementRevokesBorrowedInvocationContent()
    {
        using var authority = new InvocationAuthority();
        var invocation = new SunderRpcInvocationContext("caller.package", "1.0.0", ServiceResourceTestClient.Provider,
            DateTimeOffset.UtcNow.AddMinutes(5), 1, CancellationToken.None, authority);
        await using var operation = SunderRpcMarshallingContext.ForInvocation(invocation);
        await using var content = await operation.DeserializeAsync<SunderContent>(ServiceResourceTestClient.ContentJson());
        var read = await content.OpenReadAsync();
        invocation.Revoke();
        // Awaiting the operation cleanup drains the cancellation path without relying on callback scheduling.
        await operation.DisposeAsync();
        await read.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await content.OpenReadAsync());
        Assert.Equal(1, authority.Read!.Disposals);
    }

    [Fact]
    public async Task ConcurrentCallsUseIndependentMapsAndForwardedContentIsCopiedForNewTarget()
    {
        var client = new ServiceResourceTestClient();
        await using var lifetime = new SunderRpcResourceLifetime();
        var first = await lifetime.CreateCallAsync(client, new("first-target"));
        await using var second = await lifetime.CreateCallAsync(client, new("second-target"));
        await using var bytes = SunderContent.FromBytes(new byte[] { 1, 2, 3 });
        await Task.WhenAll(
            first.SerializeAsync(bytes, static (context, value, token) => context.RegisterContentAsync(value, token)).AsTask(),
            second.SerializeAsync(bytes, static (context, value, token) => context.RegisterContentAsync(value, token)).AsTask());
        Assert.Equal("first-target", Assert.Single(client.Scopes[0].Registrations).Target.Value);
        Assert.Equal("second-target", Assert.Single(client.Scopes[1].Registrations).Target.Value);
        var received = await first.DeserializeAsync<SunderContent>(ServiceResourceTestClient.ContentJson("original-reference"));
        await first.DisposeAsync();
        var forwarded = await second.SerializeAsync(received,
            static (context, value, token) => context.RegisterContentAsync(value, token));
        Assert.NotEqual("original-reference", forwarded.GetProperty("id").GetString());
        Assert.Equal(2, client.Scopes[1].Registrations.Count);
        Assert.Single(client.Scopes[0].Reads);
        await received.DisposeAsync();
        Assert.Equal(1, client.Scopes[0].Disposals);
    }

    [Theory]
    [InlineData(false, "content")]
    [InlineData(true, "content")]
    [InlineData(false, "caller")]
    [InlineData(true, "caller")]
    [InlineData(false, "enumeration")]
    [InlineData(true, "enumeration")]
    [InlineData(false, "lifetime")]
    [InlineData(true, "lifetime")]
    [InlineData(false, "concurrent")]
    [InlineData(true, "concurrent")]
    public async Task RetainedStreamContentOwnsOriginalCancellationAfterEnumerationEnds(bool earlyExit, string release)
    {
        var client = new ServiceResourceTestClient();
        using var callerCancellation = new CancellationTokenSource();
        using var enumerationCancellation = new CancellationTokenSource();
        await using var lifetime = new SunderRpcResourceLifetime();
        var enumerationEnded = false;
        var sourceExhausted = false;
        SunderContent? retained = null;
        await foreach (var item in ReadEvents(callerCancellation.Token).WithCancellation(enumerationCancellation.Token))
        {
            retained = item;
            if (earlyExit) break;
        }
        Assert.True(enumerationEnded);
        Assert.Equal(!earlyExit, sourceExhausted);
        Assert.False(callerCancellation.IsCancellationRequested);
        Assert.False(enumerationCancellation.IsCancellationRequested);
        var scope = Assert.Single(client.Scopes);
        Assert.Equal(0, scope.Disposals);
        await using var content = Assert.IsType<SunderContent>(retained);
        await using var read = await content.OpenReadAsync();
        Assert.Equal(1, read.ReadByte());

        // Each cancellation happens only after the source iterator and its operation context have ended.
        switch (release)
        {
            case "content": await content.DisposeAsync(); break;
            case "caller": await callerCancellation.CancelAsync(); break;
            case "enumeration": await enumerationCancellation.CancelAsync(); break;
            case "lifetime": await lifetime.DisposeAsync(); break;
            case "concurrent":
                await Task.WhenAll(
                    Task.Run(() => callerCancellation.Cancel()),
                    Task.Run(() => enumerationCancellation.Cancel()),
                    Task.Run(async () => await lifetime.DisposeAsync()));
                break;
            default: throw new InvalidOperationException();
        }
        await scope.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (release != "content")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await content.OpenReadAsync());
        await read.DisposeAsync();
        await content.DisposeAsync();
        await lifetime.DisposeAsync();
        Assert.Equal(1, scope.Disposals);
        Assert.Equal(1, Assert.Single(scope.Reads).Disposals);

        async IAsyncEnumerable<SunderContent> ReadEvents(CancellationToken callerToken,
            [EnumeratorCancellation] CancellationToken enumerationToken = default)
        {
            await using var operation = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint,
                cancellationToken: callerToken, enumerationCancellationToken: enumerationToken);
            try
            {
                await foreach (var json in operation.Client!.SubscribeAsync(ServiceResourceTestClient.Provider.Endpoint,
                                   "resource", "get", ServiceResourceTestClient.ContentJson()))
                    yield return await operation.DeserializeAsync<SunderContent>(json);
                sourceExhausted = true;
            }
            finally { enumerationEnded = true; }
        }
    }

    private sealed class InvocationAuthority : ISunderRpcInvocationAuthority, IDisposable
    {
        private readonly CancellationTokenSource _revoked = new();
        internal byte[]? Output { get; private set; }
        internal ServiceResourceTestClient.TrackingStream? Read { get; private set; }
        public CancellationToken RevocationToken => _revoked.Token;
        public async ValueTask<SunderRpcContentReference> RegisterContentAsync(Stream source,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken)
        {
            using var copy = new MemoryStream();
            await source.CopyToAsync(copy, cancellationToken);
            Output = copy.ToArray();
            return ServiceResourceTestClient.Content("provider-output");
        }
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(string filePath,
            SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Read = new([4, 5, 6]);
            return ValueTask.FromResult<Stream>(Read);
        }
        public void Revoke() => _revoked.Cancel();
        public void Dispose() => _revoked.Dispose();
    }
}
