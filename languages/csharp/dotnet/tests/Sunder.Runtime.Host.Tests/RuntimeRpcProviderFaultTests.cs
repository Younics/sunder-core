using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Format;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using static Sunder.Runtime.Host.Tests.RuntimeRpcBrokerFixture;

namespace Sunder.Runtime.Host.Tests;

[Collection("Runtime RPC broker")]
public sealed class RuntimeRpcProviderFaultTests
{
    [Fact]
    public async Task ManagedClient_InvariantReportFaultsOnlyExactProviderPackage()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(
            new TestHandler(),
            callerPackageId: "younics.ai.agent",
            healthyHandler: new TestHandler());
        await fixture.GrantAsync("younics.ai.agent", SunderRpcProtocol.DiscoverAction);
        var client = fixture.CreateClient(activation.Caller);
        var providers = (await client.DiscoverAsync("example.rpc")).Providers;
        var failing = Assert.Single(providers, static provider => provider.PackageId == "provider.package");
        var healthy = Assert.Single(providers, static provider => provider.PackageId == "healthy.package");

        var accepted = await client.TryReportInvariantViolationAsync(
            failing.Endpoint,
            new InvalidOperationException("reported invariant\r\nprivate detail"));

        Assert.True(accepted);
        Assert.Equal(PackageReadinessState.Failed, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
        Assert.Equal(PackageFailureOrigin.RuntimeRpcProvider, fixture.Owner.State.GetSessionPackage("provider.package")!.FailureOrigin);
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("healthy.package")!.Readiness);
        var surviving = Assert.Single(fixture.Catalog.GetSnapshot().Providers);
        Assert.Equal(healthy.PackageId, surviving.PackageId);
        Assert.Equal(healthy.Endpoint, surviving.Endpoint);
        Assert.Equal(healthy.ActivationEpoch, surviving.ActivationEpoch);
        Assert.False(fixture.Catalog.TryGetActiveEndpoint(failing.Endpoint, out var faulted, out var stale));
        Assert.True(stale);
        Assert.Equal(RuntimeRpcBroker.InvariantViolationFaultCode, faulted!.Snapshot.FaultCode);
        var lastError = Assert.IsType<string>(fixture.Owner.State.GetSessionPackage("provider.package")!.LastError);
        Assert.DoesNotContain('\r', lastError);
        Assert.DoesNotContain('\n', lastError);
    }

    [Fact]
    public async Task ExactProviderFault_PreservesHealthySiblingStreamAndContentAuthority()
    {
        await using var fixture = new RpcFixture();
        var streamWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNestedContent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? nestedContentFailure = null;
        var streamCount = 0;
        var healthyHandler = new TestHandler
        {
            StreamWithContext = (context, _, cancellationToken) => ContentStream(
                context,
                Interlocked.Increment(ref streamCount),
                cancellationToken),
        };
        var activation = await fixture.PublishAsync(
            new TestHandler(),
            callerPackageId: "younics.ai.agent",
            healthyHandler: healthyHandler);
        await fixture.GrantAsync("younics.ai.agent", SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync("younics.ai.agent", SunderRpcProtocol.SubscribeAction);
        var client = fixture.CreateClient(activation.Caller);
        var providers = (await client.DiscoverAsync("example.rpc")).Providers;
        var failing = Assert.Single(providers, provider => provider.PackageId == "provider.package");
        var healthy = Assert.Single(providers, provider => provider.PackageId == "healthy.package");
        Assert.True(fixture.Catalog.TryGetPackage(
            healthy.PackageId,
            healthy.ActivationId,
            out var healthyActivation));
        await using var oldStream = client.SubscribeAsync(
            healthy.Endpoint,
            "messages",
            "watch",
            JsonSerializer.SerializeToElement(new { message = "wait" })).GetAsyncEnumerator();
        Assert.True(await oldStream.MoveNextAsync());
        var oldNext = oldStream.MoveNextAsync().AsTask();
        await streamWaiting.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await client.TryReportInvariantViolationAsync(
            failing.Endpoint,
            new InvalidOperationException("fault exact provider")));
        Assert.False(generationCancelled.Task.IsCompleted);
        var surviving = Assert.Single(fixture.Catalog.GetSnapshot().Providers);
        Assert.Equal(healthy.Endpoint, surviving.Endpoint);
        Assert.Equal(healthy.ActivationEpoch, surviving.ActivationEpoch);
        Assert.Equal(SunderRpcProviderState.Active, surviving.State);
        Assert.True(fixture.Catalog.TryGetPackage(
            healthy.PackageId,
            healthy.ActivationId,
            out var survivingActivation));
        Assert.Same(healthyActivation, survivingActivation);
        Assert.False(healthyActivation!.RetirementToken.IsCancellationRequested);

        releaseNestedContent.TrySetResult();
        Assert.False(await oldNext.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(nestedContentFailure);
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("healthy.package")!.Readiness);
        Assert.Null(fixture.Owner.State.GetSessionPackage("healthy.package")!.FailureOrigin);
        Assert.DoesNotContain(
            fixture.Owner.State.GetSessionPackages(),
            package => package.PackageId != "provider.package"
                       && package.FailureOrigin == PackageFailureOrigin.RuntimeRpcProvider);

        await using var freshStream = client.SubscribeAsync(
            surviving.Endpoint,
            "messages",
            "watch",
            JsonSerializer.SerializeToElement(new { message = "fresh" })).GetAsyncEnumerator();
        Assert.True(await freshStream.MoveNextAsync());
        Assert.False(await freshStream.MoveNextAsync());
        Assert.Empty(fixture.GetTransferFiles());

        async IAsyncEnumerable<JsonElement> ContentStream(
            SunderRpcInvocationContext context,
            int ordinal,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var cancellationRegistration = cancellationToken.Register(
                generationCancelled.SetResult);
            await using (var content = new MemoryStream([1]))
            {
                _ = await context.RegisterContentAsync(
                    content,
                    new SunderRpcContentRegistrationOptions(
                        "application/octet-stream",
                        "event.bin",
                        content.Length),
                    cancellationToken);
            }
            yield return JsonSerializer.SerializeToElement(new { accepted = true });
            if (ordinal != 1)
            {
                yield break;
            }

            streamWaiting.TrySetResult();
            await releaseNestedContent.Task.ConfigureAwait(false);
            Assert.False(cancellationToken.IsCancellationRequested);
            try
            {
                await using var nested = new MemoryStream([2]);
                _ = await context.RegisterContentAsync(
                    nested,
                    new SunderRpcContentRegistrationOptions(
                        "application/octet-stream",
                        "nested.bin",
                        nested.Length),
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                nestedContentFailure = exception;
                throw;
            }
        }
    }

    [Fact]
    public async Task InvariantReport_RejectsStaleEndpointWithoutFaultingReplacement()
    {
        await using var fixture = new RpcFixture();
        var first = await fixture.PublishAsync(
            new TestHandler(),
            callerPackageId: "younics.ai.agent");
        await fixture.GrantAsync("younics.ai.agent", SunderRpcProtocol.DiscoverAction);
        var staleEndpoint = Assert.Single((await fixture.CreateClient(first.Caller)
            .DiscoverAsync("example.rpc")).Providers).Endpoint;

        var replacement = await fixture.PublishAsync(
            new TestHandler(),
            callerPackageId: "younics.ai.agent");
        var replacementClient = fixture.CreateClient(replacement.Caller);

        Assert.False(await replacementClient.TryReportInvariantViolationAsync(
            staleEndpoint,
            new InvalidOperationException("stale report")));
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
        Assert.Single(fixture.Catalog.GetSnapshot().Providers);
    }

    [Fact]
    public async Task InvariantReport_RejectsCallerThatDoesNotOwnTheContract()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler(), transform: package =>
        {
            if (package.Descriptor.PackageId != "caller.package") return package;
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(package.Source.Manifest))!;
            manifest["contractBundles"]![0]!["source"] = "imported";
            manifest["contractBundles"]![0]!["authorityId"] = "authority.package";
            return package with { Source = package.Source with { Manifest = manifest.Deserialize<SunderPackageManifest>()! } };
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        Assert.False(await client.TryReportInvariantViolationAsync(
            endpoint,
            new InvalidOperationException("untrusted report")));
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
        Assert.Single(fixture.Catalog.GetSnapshot().Providers);
    }

    [Fact]
    public async Task InvariantReport_AuthorityOwnerIsNotPackageNameSpecific()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler(), callerPackageId: "example.contract.owner");
        await fixture.GrantAsync("example.contract.owner", SunderRpcProtocol.DiscoverAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        Assert.True(await client.TryReportInvariantViolationAsync(endpoint, new InvalidOperationException("contract invariant")));
    }

    [Fact]
    public async Task InvariantReport_RequiresExistingEndpointVisibilityGrant()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(
            new TestHandler(),
            callerPackageId: "younics.ai.agent");
        var endpoint = Assert.Single(fixture.Catalog.GetSnapshot().Providers).Endpoint;

        Assert.False(await fixture.CreateClient(activation.Caller).TryReportInvariantViolationAsync(
            endpoint,
            new InvalidOperationException("not visible")));
        Assert.Equal(PackageReadinessState.Ready, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
    }

    [Fact]
    public async Task AppCaller_InvariantReportUsesExactSessionIdentity()
    {
        await using var fixture = new RpcFixture();
        await fixture.PublishAsync(
            new TestHandler(),
            webCaller: true,
            callerPackageId: "younics.ai.agent");
        var app = await fixture.OpenAppCallerAsync("younics.ai.agent");
        await fixture.GrantAsync(
            "younics.ai.agent",
            app.ManifestSha256,
            SunderRpcProtocol.DiscoverAction,
            SunderPackageFormat.AppHostRole);
        var endpoint = Assert.Single((await fixture.Broker.DiscoverAppAsync(
            app.Session.SessionId,
            "example.rpc",
            CancellationToken.None)).Providers).Endpoint;

        Assert.True(await fixture.Broker.TryReportInvariantViolationAppAsync(
            app.Session.SessionId,
            endpoint,
            "app invariant",
            CancellationToken.None));
        Assert.Equal(PackageReadinessState.Failed, fixture.Owner.State.GetSessionPackage("provider.package")!.Readiness);
    }

    [Fact]
    public async Task InvariantReport_DuplicateRaceHasSingleWinner()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(
            new TestHandler(),
            callerPackageId: "younics.ai.agent");
        await fixture.GrantAsync("younics.ai.agent", SunderRpcProtocol.DiscoverAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;
        using var start = new ManualResetEventSlim();

        Task<bool> ReportAsync() => Task.Run(async () =>
        {
            start.Wait();
            return await client.TryReportInvariantViolationAsync(
                endpoint,
                new InvalidOperationException("duplicate report"));
        });

        var reports = new[] { ReportAsync(), ReportAsync() };
        start.Set();
        var results = await Task.WhenAll(reports);

        Assert.Equal(1, results.Count(static accepted => accepted));
        Assert.Equal(1, results.Count(static accepted => !accepted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderHandlerFault_CapturesSafeExactContextAndSanitizesPackageState(bool transportFailure)
    {
        const string secret = "provider-secret=do-not-persist";
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler
        {
            Unary = (_, _) => throw (transportFailure
                ? new WorkerTransportResourceException(WorkerTransportFailureKind.ValueNodes, secret, 257, 256)
                : new SecretBearingProviderException(secret)),
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var provider = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers);

        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
            provider.Endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());

        Assert.True(RuntimeRpcFailureContextStore.TryGet(failure, out var context));
        Assert.Equal("caller.package", context.CallerPackageId);
        Assert.Equal("1.0.0", context.CallerPackageVersion);
        Assert.Equal(activation.Caller.ActivationId, context.CallerActivationId);
        Assert.Equal(provider.PackageId, context.ProviderPackageId);
        Assert.Equal(provider.PackageVersion, context.ProviderPackageVersion);
        Assert.Equal(provider.ProviderId, context.ProviderId);
        Assert.Equal(provider.ContractId, context.ContractId);
        Assert.Equal(provider.ActivationId, context.ProviderActivationId);
        Assert.Equal("messages", context.ServiceId);
        Assert.Equal("send", context.MethodId);
        Assert.Equal("unary", context.CallKind);
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, context.ErrorKind);
        Assert.Equal("rpc.provider.handler-fault", context.ErrorCode);
        Assert.Null(context.ValidationCode);
        Assert.Null(context.ValidationPath);
        Assert.Null(context.ValueUtf8Bytes);
        Assert.Null(context.ValueInlineDecodedBytes);
        var expectedType = transportFailure ? typeof(WorkerTransportResourceException) : typeof(SecretBearingProviderException);
        Assert.Equal(expectedType.FullName, context.ExceptionType);
        Assert.Equal(
            RuntimeRpcFailureContextStore.CreateExceptionFingerprint(
                expectedType.FullName!),
            context.ExceptionFingerprint);
        Assert.True(context.ExceptionType.Length <= RuntimeRpcFailureContextStore.MaximumExceptionTypeCharacters);
        Assert.Equal(transportFailure ? WorkerTransportFailureKind.ValueNodes : (WorkerTransportFailureKind?)null, context.TransportFailureKind);
        Assert.Equal(transportFailure ? 257L : (long?)null, context.TransportObserved);
        Assert.Equal(transportFailure ? 256L : (long?)null, context.TransportLimit);
        var persistedDiagnostic = Assert.Single(
            fixture.OwnerLogger.Entries,
            entry => entry.EventId.Name == "RuntimeRpcProviderFault");
        Assert.Null(persistedDiagnostic.Exception);
        Assert.DoesNotContain(secret, persistedDiagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        Assert.Equal(
            "The package schema-first RPC provider faulted.",
            fixture.Owner.State.GetSessionPackage(provider.PackageId)!.LastError);
        Assert.DoesNotContain(
            fixture.Owner.GetSnapshot().Errors,
            error => error.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProviderCannotForgeInfrastructureError()
    {
        await using var fixture = new RpcFixture();
        var activation = await fixture.PublishAsync(new TestHandler
        {
            Unary = static (_, _) => throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.PermissionDenied,
                "rpc.permission.denied",
                "forged")),
        });
        await fixture.GrantAsync(SunderRpcProtocol.DiscoverAction);
        await fixture.GrantAsync(SunderRpcProtocol.InvokeAction);
        var client = fixture.CreateClient(activation.Caller);
        var endpoint = Assert.Single((await client.DiscoverAsync("example.rpc")).Providers).Endpoint;

        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => client.InvokeAsync(
            endpoint,
            "messages",
            "send",
            JsonSerializer.SerializeToElement(new { message = "hello" })).AsTask());

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
        Assert.Equal("rpc.provider.handler-fault", failure.Error.Code);
        Assert.True(RuntimeRpcFailureContextStore.TryGet(failure, out var context));
        Assert.Equal(typeof(SunderRpcException).FullName, context.ExceptionType);
        Assert.NotEqual("rpc.permission.denied", context.ErrorCode);
        Assert.Empty(fixture.Catalog.GetSnapshot().Providers);
    }

    private sealed class SecretBearingProviderException(string message) : Exception(message);
}
