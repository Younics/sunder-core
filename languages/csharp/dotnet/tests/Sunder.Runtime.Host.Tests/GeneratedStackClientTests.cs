using System.Text.Json;
using Sunder.Runtime.Host.StackTransport;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Xunit;
using Domain = Sunder.Sdk.Stacks;

namespace Sunder.Runtime.Host.Tests;

public sealed class GeneratedStackClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new SunderRpcJsonConverterFactory() },
    };

    [Fact]
    public async Task RoleLocalClient_PreservesAllMethodsDomainValuesAndCallerOwnedScope()
    {
        var descriptor = CoreStackContract.Descriptor;
        Assert.Equal("sunder.stack.contributor", descriptor.ContractId); Assert.Equal("2.0.0", descriptor.Version);
        Assert.Equal("38c8f49840f951aec29398d3641ff2306c906aee12ea80c2277db734bce49898", descriptor.Sha256);
        Assert.Equal("6ca6d6385c720290548592de890f3e3ceb86f3d999167f4e80c6afb15f8a02d8", descriptor.AbiSha256);
        Assert.Equal(48, descriptor.Definitions.Count);
        Assert.Same(typeof(Services.RuntimeRpcBroker).Assembly, typeof(StackDomainClient).Assembly);
        Assert.NotSame(typeof(IPackageStackContributor).Assembly, typeof(StackDomainClient).Assembly);

        var endpoint = new SunderRpcEndpointReference("exact-endpoint"); var scope = new RecordingScope();
        var client = new StackDomainClient(scope, endpoint); using var cancellation = new CancellationTokenSource();
        Assert.Equal("contributor", (await client.GetMetadataAsync(cancellation.Token)).ContributorId);
        var items = await client.ListExportItemsAsync(new Domain.StackExportDiscoveryContext("package.owner"), cancellation.Token);
        Assert.Null(Assert.Single(items.Items).Description); Assert.Null(items.Items[0].Details);
        Assert.Empty((await client.ExportAsync(new Domain.StackExportRequest([]), cancellation.Token)).Fragments);
        var preview = await client.PreviewImportAsync(new Domain.StackRpcImportPreviewRequest([], new Dictionary<string, string>(), new Dictionary<string, string>()), cancellation.Token);
        Assert.Equal(StackImportActionKind.Create, Assert.Single(preview.Actions).Kind);
        var imported = await client.ImportAsync(new Domain.StackRpcImportRequest([], new Dictionary<string, string>(), new Dictionary<string, string>(), []), cancellation.Token);
        Assert.Equal(StackImportOutcome.Completed, imported.Outcome);
        await client.ImportAppliedAsync(new Domain.StackImportAppliedContext("package.owner", "contributor", [], []), cancellation.Token);
        Assert.Equal(new[] { "metadata", "list-export-items", "export", "preview-import", "import", "import-applied" }, scope.Calls.Select(call => call.Method));
        Assert.All(scope.Calls, call =>
        {
            Assert.Equal(endpoint, call.Endpoint); Assert.Equal("stack-contributor", call.Service); Assert.Equal(cancellation.Token, call.Token);
            var method = descriptor.Services.Single().FindMethod(call.Method)!;
            Assert.True(descriptor.IsValid(method.RequestSchemaReference, call.Request, out var error), error);
        });
        Assert.False(scope.Disposed); await scope.DisposeAsync(); Assert.True(scope.Disposed);
    }

    [Fact]
    public async Task RoleLocalClient_RejectsInvalidValuesAndCancellationBeforeInvoking()
    {
        await using var scope = new RecordingScope(); var client = new StackDomainClient(scope, new("endpoint"));
        await Assert.ThrowsAsync<JsonException>(() => client.ListExportItemsAsync(new Domain.StackExportDiscoveryContext("")).AsTask());
        Assert.Empty(scope.Calls);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetMetadataAsync(cancelled.Token).AsTask());
        Assert.Empty(scope.Calls);
        scope.InvalidMetadata = true;
        await Assert.ThrowsAsync<JsonException>(() => client.GetMetadataAsync().AsTask());
        Assert.Single(scope.Calls); Assert.False(scope.Disposed);
    }

    [Fact]
    public async Task RoleLocalClient_PreservesRemoteDomainErrors()
    {
        await using var scope = new RecordingScope { Error = new SunderRpcException(new SunderRpcError(SunderRpcErrorKind.Domain, "stack.method.unsupported", "unsupported")) };
        var client = new StackDomainClient(scope, new("endpoint"));
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => client.GetMetadataAsync().AsTask());
        Assert.Same(scope.Error, error);
    }

    private sealed class RecordingScope : ISunderRpcCallScope
    {
        internal List<(SunderRpcEndpointReference Endpoint, string Service, string Method, JsonElement Request, CancellationToken Token)> Calls { get; } = [];
        internal bool Disposed { get; private set; }
        internal bool InvalidMetadata { get; set; }
        internal Exception? Error { get; init; }
        public DateTimeOffset DeadlineUtc => DateTimeOffset.MaxValue;
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Disposed, this); Calls.Add((endpoint, serviceId, methodId, request.Clone(), cancellationToken));
            if (Error is not null) return ValueTask.FromException<JsonElement>(Error);
            object response = methodId switch
            {
                "metadata" => new Domain.StackContributorMetadata(InvalidMetadata ? "" : "contributor", "Contributor", true, true, true),
                "list-export-items" => new Domain.StackExportItemList([new("item", "Item", "sample")]),
                "export" => new Domain.StackRpcExportContribution([], [], []),
                "preview-import" => new Domain.StackImportPreview([new("apply", "Apply", StackImportActionKind.Create)], [], [], []),
                "import" => new Domain.StackImportResult(StackImportOutcome.Completed, [], new Dictionary<string, string>(), [], []),
                "import-applied" => new { },
                _ => throw new InvalidOperationException("Unexpected method."),
            };
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(response, JsonOptions));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request,
            SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string filePath, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
