using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Sdk.PublicApi.Tests;

public sealed partial class SunderServiceResourceTests
{
    [Fact]
    public async Task LastLeaseRelease_AllowsSynchronousScopeCleanupToDrainConcurrentCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var scope = new ServiceResourceTestClient.Scope(new(), DateTimeOffset.UtcNow.AddMinutes(1));
        var removed = 0;
        var owner = new SunderRpcResourceOwner(scope, scope.DeadlineUtc, linked, _ => Interlocked.Increment(ref removed));
        owner.ObserveCancellation();
        var callbackReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = cancellation.Token.Register(() =>
        {
            _ = owner.DisposeAsync();
            callbackReturned.TrySetResult();
        });
        // Linked-token disposal can synchronously drain an in-flight callback.
        // Reproduce that reentry without timing-dependent sleeps or private-field reflection.
        scope.Disposing = () => Task.Run(() => cancellation.Cancel()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        await owner.ReleaseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await callbackReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.DisposeAsync();
        Assert.Equal(1, scope.Disposals);
        Assert.Equal(1, removed);
        Assert.True(owner.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    [InlineData("\u2003")]
    public void LocalContentRejectsExplicitBlankRegistrationFileNames(string fileName)
    {
        Assert.Throws<ArgumentException>(() => SunderContent.FromBytes(new byte[] { 1 }, fileName: fileName));
        using var source = new MemoryStream([1]);
        Assert.Throws<ArgumentException>(() => SunderContent.FromStream(source, fileName: fileName));
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task NestedContentRegistrationIsTargetBoundDeduplicatedAndExplicit()
    {
        var client = new ServiceResourceTestClient();
        await using var lifetime = new SunderRpcResourceLifetime();
        await using var operation = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint);
        await using var content = SunderContent.FromBytes(new byte[] { 1, 2, 3 }, "text/plain", "value.txt");
        var value = new Dictionary<string, SunderContent[]> { ["nested"] = [content, content] };
        var json = await operation.SerializeAsync(value, static async (context, model, token) =>
        {
            foreach (var item in model["nested"]) await context.RegisterContentAsync(item, token);
        });
        var registration = Assert.Single(Assert.Single(client.Scopes).Registrations);
        Assert.Equal(ServiceResourceTestClient.Provider.Endpoint, registration.Target);
        Assert.Equal(new byte[] { 1, 2, 3 }, registration.Bytes);
        Assert.Equal(json.GetProperty("nested")[0].GetRawText(), json.GetProperty("nested")[1].GetRawText());
        Assert.Equal(7, json.GetProperty("nested")[0].EnumerateObject().Count());
        await Assert.ThrowsAsync<JsonException>(async () => await operation.SerializeAsync(content));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(content));
    }

    [Fact]
    public async Task UnaryResultContentSharesOwnerAfterOperationAndReleasesLastLeaseImmediately()
    {
        var client = new ServiceResourceTestClient();
        await using var lifetime = new SunderRpcResourceLifetime();
        var operation = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint);
        var wire = JsonSerializer.SerializeToElement(new[] { ServiceResourceTestClient.ContentJson("one"),
            ServiceResourceTestClient.ContentJson("two") });
        var response = await operation.Client!.InvokeAsync(ServiceResourceTestClient.Provider.Endpoint, "resource", "get", wire);
        var values = await operation.DeserializeAsync<SunderContent[]>(response);
        await operation.DisposeAsync();
        var scope = Assert.Single(client.Scopes);
        Assert.Equal(0, scope.Disposals);
        await using (var read = await values[0].OpenReadAsync()) Assert.Equal(1, read.ReadByte());
        await values[0].DisposeAsync();
        Assert.Equal(0, scope.Disposals);
        await using (var read = await values[1].OpenReadAsync()) Assert.Equal(1, read.ReadByte());
        await values[1].DisposeAsync();
        Assert.Equal(1, scope.Disposals);
        await lifetime.DisposeAsync();
        Assert.Equal(1, scope.Disposals);
        Assert.All(scope.Reads, read => Assert.Equal(1, read.Disposals));
    }

    [Fact]
    public async Task FailedDecodeRollsBackAlreadyDecodedContentAndDoesNotRetainScope()
    {
        var client = new ServiceResourceTestClient();
        await using var lifetime = new SunderRpcResourceLifetime();
        var operation = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint);
        var wire = JsonSerializer.SerializeToElement(new { content = ServiceResourceTestClient.ContentJson(), count = "invalid" });
        await Assert.ThrowsAsync<JsonException>(async () => await operation.DeserializeAsync<ContentAndCount>(wire));
        await operation.DisposeAsync();
        Assert.Equal(1, Assert.Single(client.Scopes).Disposals);
    }

    [Fact]
    public async Task CompletedStreamValuesDoNotKeepDisposedContentOrEmptyScopesAlive()
    {
        var client = new ServiceResourceTestClient();
        await using var lifetime = new SunderRpcResourceLifetime();
        var operation = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint);
        for (var index = 0; index < 32; index++)
        {
            await using var item = await operation.DeserializeAsync<SunderContent>(ServiceResourceTestClient.ContentJson());
            await using var read = await item.OpenReadAsync();
            Assert.Equal(1, read.ReadByte());
        }
        await operation.DisposeAsync();
        Assert.Equal(1, Assert.Single(client.Scopes).Disposals);
        for (var index = 0; index < 16; index++)
        {
            await using var empty = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint);
            Assert.Equal(3, await empty.DeserializeAsync<int>(JsonSerializer.SerializeToElement(3)));
        }
        Assert.All(client.Scopes, scope => Assert.Equal(1, scope.Disposals));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamInputsHaveSingleReadAndExplicitSourceOwnership(bool leaveOpen)
    {
        var input = new ServiceResourceTestClient.TrackingStream([1, 2, 3]);
        var content = SunderContent.FromStream(input, leaveOpen: leaveOpen);
        await using (var read = await content.OpenReadAsync()) Assert.Equal(1, read.ReadByte());
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await content.OpenReadAsync());
        await content.DisposeAsync();
        Assert.Equal(leaveOpen ? 0 : 1, input.Disposals);

        var unread = new ServiceResourceTestClient.TrackingStream([4]);
        await SunderContent.FromStream(unread, leaveOpen: leaveOpen).DisposeAsync();
        Assert.Equal(leaveOpen ? 0 : 1, unread.Disposals);
        if (leaveOpen) { input.Dispose(); unread.Dispose(); }
    }

    [Fact]
    public async Task BytesArePrivateCopiesAndFileDisposalNeverDeletesSource()
    {
        byte[] bytes = [1, 2, 3];
        await using var content = SunderContent.FromBytes(bytes);
        bytes[0] = 9;
        await using (var first = await content.OpenReadAsync()) Assert.Equal(1, first.ReadByte());
        await using (var second = await content.OpenReadAsync()) Assert.Equal(1, second.ReadByte());
        Assert.Equal(64, content.Sha256!.Length);
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            await using (var file = SunderContent.FromFile(path))
            {
                Assert.Equal(3L, file.Length);
                Assert.Equal(Path.GetFileName(path), file.FileName);
                await using var read = await file.OpenReadAsync();
                Assert.Equal(9, read.ReadByte());
            }
            Assert.True(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("deadline")]
    [InlineData("lifetime")]
    public async Task RetainedContentIsRevokedByCancellationDeadlineOrDiDisposal(string reason)
    {
        var client = new ServiceResourceTestClient();
        using var cancellation = new CancellationTokenSource();
        await using var lifetime = new SunderRpcResourceLifetime();
        if (reason == "deadline") client.Deadline = DateTimeOffset.UtcNow.AddSeconds(1);
        var operation = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint,
            cancellationToken: cancellation.Token);
        await using var content = await operation.DeserializeAsync<SunderContent>(ServiceResourceTestClient.ContentJson());
        var read = await content.OpenReadAsync();
        await operation.DisposeAsync();
        if (reason == "cancel") await cancellation.CancelAsync();
        if (reason == "lifetime") await lifetime.DisposeAsync();
        var scope = Assert.Single(client.Scopes);
        await scope.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await read.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await content.OpenReadAsync());
        Assert.Equal(1, Assert.Single(scope.Reads).Disposals);
        Assert.Equal(1, scope.Disposals);
    }

    [Fact]
    public async Task CancellationDuringOpenPropagatesAndReleasesRetainedOwner()
    {
        var client = new ServiceResourceTestClient { BlockOpen = true, OpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var lifetime = new SunderRpcResourceLifetime();
        var operation = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint);
        var content = await operation.DeserializeAsync<SunderContent>(ServiceResourceTestClient.ContentJson());
        await operation.DisposeAsync();
        using var cancellation = new CancellationTokenSource();
        var open = content.OpenReadAsync(cancellation.Token).AsTask();
        await client.OpenEntered.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        await content.DisposeAsync();
        Assert.Equal(1, Assert.Single(client.Scopes).Disposals);
    }

    public sealed record ContentAndCount(SunderContent Content, int Count);

    [Fact]
    public async Task ContentDisposalCancelsAndDrainsAnOpenBeforeReleasingItsScope()
    {
        var client = new ServiceResourceTestClient { BlockOpen = true, OpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var lifetime = new SunderRpcResourceLifetime();
        var operation = await lifetime.CreateCallAsync(client, ServiceResourceTestClient.Provider.Endpoint);
        var content = await operation.DeserializeAsync<SunderContent>(ServiceResourceTestClient.ContentJson());
        await operation.DisposeAsync();
        var open = content.OpenReadAsync().AsTask();
        await client.OpenEntered.Task;
        await content.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        Assert.Equal(1, Assert.Single(client.Scopes).Disposals);
    }
}
