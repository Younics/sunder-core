using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed partial class SunderServiceResourceTests
{
    [Theory]
    [InlineData("unary")]
    [InlineData("forged")]
    [InlineData("kind")]
    [InlineData("code")]
    [InlineData("unsafe")]
    [InlineData("dispatched")]
    [InlineData("visitor")]
    [InlineData("other-operation")]
    [InlineData("source-open")]
    [InlineData("source-read")]
    public async Task ResponseRetirement_DoesNotHideUnqualifiedErrorsOrLoseTheirDispatchMetadata(string scenario)
    {
        var error = new SunderRpcException(new(
            scenario == "kind" ? SunderRpcErrorKind.PermissionDenied : SunderRpcErrorKind.Unavailable,
            scenario == "code" ? "rpc.other.operation" : "rpc.content.authority-revoked", "original",
            scenario == "dispatched" ? SunderRpcDispatchState.PossiblyDispatched : SunderRpcDispatchState.NotDispatched,
            scenario == "unsafe" ? SunderRpcRetrySafety.Unsafe : SunderRpcRetrySafety.Safe), hostAuthenticated: scenario != "forged");
        var authority = new ResponseAuthority(async (source, _) =>
        {
            if (scenario == "source-read") await source.CopyToAsync(Stream.Null);
            throw error;
        });
        await using var context = SunderRpcMarshallingContext.ForInvocation(authority.Context());
        await using var content = scenario == "source-open"
            ? new SunderContent("text/plain", "item.txt", null, null, null, true, _ => ValueTask.FromException<Stream>(error))
            : scenario == "source-read" ? SunderContent.FromStream(new FailingResponseStream(error), fileName: "item.txt")
            : SunderContent.FromBytes(new byte[] { 1 }, fileName: "item.txt");
        async ValueTask Register(SunderRpcMarshallingContext current, SunderContent value, CancellationToken token)
        {
            if (scenario == "visitor") throw error;
            if (scenario == "other-operation")
            {
                await using var other = SunderRpcMarshallingContext.ForInvocation(authority.Context());
                await other.RegisterContentAsync(value, token);
                return;
            }
            await current.RegisterContentAsync(value, token);
        }
        var thrown = await Assert.ThrowsAsync<SunderRpcException>(async () =>
        {
            if (scenario == "unary") await context.SerializeAsync(content, Register);
            else await context.SerializeResponseStreamItemAsync(content, Register);
        });
        Assert.Same(error, thrown);
        Assert.Same(error.Error, thrown.Error);
        Assert.Equal(error.IsHostAuthenticated, thrown.IsHostAuthenticated);
    }

    [Fact]
    public async Task ResponseRetirement_ProofCannotBeReusedForAnotherItemOrInvocation()
    {
        var error = SunderRpcException.Infrastructure(SunderRpcErrorKind.Unavailable, "rpc.content.authority-revoked", "retired");
        var authority = new ResponseAuthority((_, _) => ValueTask.FromException<SunderRpcContentReference>(error));
        await using var first = SunderRpcMarshallingContext.ForInvocation(authority.Context());
        await using var other = SunderRpcMarshallingContext.ForInvocation(authority.Context());
        await using var content = SunderContent.FromBytes(new byte[] { 1 }, fileName: "item.txt");
        Exception? captured = null;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await first.SerializeResponseStreamItemAsync(content,
            async (context, value, token) =>
            {
                captured = await Record.ExceptionAsync(async () => await context.RegisterContentAsync(value, token));
                throw new InvalidOperationException("visitor failed after registration");
            }));
        Assert.NotNull(captured);
        foreach (var context in new[] { first, other })
        {
            var actual = await Assert.ThrowsAsync<SunderRpcException>(async () => await context.SerializeResponseStreamItemAsync(content,
                (_, _, _) => ValueTask.FromException(captured)));
            Assert.Same(error, actual);
        }
    }

    [Fact]
    public async Task ResponseRetirement_CallerCancellationWinsAndJsonNullIsNotCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var authority = new ResponseAuthority((_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromException<SunderRpcContentReference>(SunderRpcException.Infrastructure(
                SunderRpcErrorKind.Cancelled, "rpc.call.cancelled", "retired"));
        });
        await using var context = SunderRpcMarshallingContext.ForInvocation(authority.Context());
        var jsonNull = await context.SerializeResponseStreamItemAsync<string?>(null);
        Assert.True(jsonNull.HasValue);
        Assert.Equal(JsonValueKind.Null, jsonNull.Value.ValueKind);
        await using var content = SunderContent.FromBytes(new byte[] { 1 }, fileName: "item.txt");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await context.SerializeResponseStreamItemAsync(content,
            static (context, value, token) => context.RegisterContentAsync(value, token), cancellation.Token));
    }

    [Fact]
    public async Task ResponseRetirement_DoesNotClassifyOrdinaryIoFailureAsAuthorityRetirement()
    {
        var error = new IOException("storage failure");
        var authority = new ResponseAuthority((_, _) => ValueTask.FromException<SunderRpcContentReference>(error));
        await using var context = SunderRpcMarshallingContext.ForInvocation(authority.Context());
        await using var content = SunderContent.FromBytes(new byte[] { 1 }, fileName: "item.txt");
        Assert.Same(error, await Assert.ThrowsAsync<IOException>(async () => await context.SerializeResponseStreamItemAsync(content,
            static (context, value, token) => context.RegisterContentAsync(value, token))));
    }

    [Fact]
    public async Task ResponseRetirement_ProofCannotTurnAJsonConversionFailureIntoCompletion()
    {
        var error = SunderRpcException.Infrastructure(SunderRpcErrorKind.Unavailable, "rpc.content.authority-revoked", "retired");
        var authority = new ResponseAuthority((_, _) => ValueTask.FromException<SunderRpcContentReference>(error));
        await using var context = SunderRpcMarshallingContext.ForInvocation(authority.Context());
        await using var content = SunderContent.FromBytes(new byte[] { 1 }, fileName: "item.txt");
        Exception? captured = null;
        context.JsonOptions.Converters.Insert(0, new ReplayedRegistrationProof(() => captured!));
        var actual = await Assert.ThrowsAsync<SunderRpcException>(async () => await context.SerializeResponseStreamItemAsync("value",
            async (current, _, token) => captured = await Record.ExceptionAsync(async () => await current.RegisterContentAsync(content, token))));
        Assert.Same(error, actual);
    }

    private sealed class ReplayedRegistrationProof(Func<Exception> failure) : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => throw failure();
    }

    private sealed class ResponseAuthority(Func<Stream, CancellationToken, ValueTask<SunderRpcContentReference>> register) : ISunderRpcInvocationAuthority
    {
        public CancellationToken RevocationToken => CancellationToken.None;
        internal SunderRpcInvocationContext Context() => new("caller.package", "1.0.0", ServiceResourceTestClient.Provider,
            DateTimeOffset.UtcNow.AddMinutes(5), 1, CancellationToken.None, this);
        public ValueTask<SunderRpcContentReference> RegisterContentAsync(Stream source, SunderRpcContentRegistrationOptions options, CancellationToken token) => register(source, token);
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(string path, SunderRpcContentRegistrationOptions options, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken token) => throw new NotSupportedException();
        public void Revoke() { }
    }

    private sealed class FailingResponseStream(Exception failure) : MemoryStream(new byte[] { 1 })
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(failure);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(failure);
        public override int Read(byte[] buffer, int offset, int count) => throw failure;
        public override int Read(Span<byte> buffer) => throw failure;
    }
}
