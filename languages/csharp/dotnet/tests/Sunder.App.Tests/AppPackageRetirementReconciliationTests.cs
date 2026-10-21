using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using Sunder.App.Models;
using Sunder.App.Services;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppPackageRetirementReconciliationTests
{
    private static readonly AsyncLocal<string?> PackageContext = new();

    [Theory]
    [InlineData(RuntimePackageStageState.Committed)]
    [InlineData(RuntimePackageStageState.Failed)]
    [InlineData(RuntimePackageStageState.Discarded)]
    public async Task TerminalStageExplicitlyReconcilesUnchangedGenerationWithoutBorrowingCleanupContext(RuntimePackageStageState terminal)
    {
        await using var fixture = new Fixture();
        PackageContext.Value = "retiring-package";
        using var cleanup = new AppPackageLoadAdmission(Guid.NewGuid(), null).EnterCleanup();
        try
        {
            fixture.Start();
            var pending = await fixture.Handler.NextAsync();
            Assert.Null(pending.Context);
            Assert.False(pending.CleaningActivation);
            Assert.True(pending.Token.CanBeCanceled);
            pending.Reply(ProbeHandler.Status(RuntimePackageStageState.Pending));
            var committing = await fixture.Handler.NextAsync();
            Assert.Equal(0, fixture.Handler.SnapshotReads);
            Assert.Equal(0, fixture.Dispatcher.Reconciliations);
            committing.Reply(ProbeHandler.Status(RuntimePackageStageState.Committing));
            var finishing = await fixture.Handler.NextAsync();
            Assert.False(fixture.Work.IsCompleted);
            finishing.Reply(ProbeHandler.Status(terminal));
            await fixture.Work.WaitAsync(TimeSpan.FromSeconds(5));

            // The fixed authoritative snapshot is generation 7 throughout; failed
            // and discarded stages still need the same explicit lifecycle refresh.
            Assert.Equal(2, fixture.Handler.SnapshotReads);
            Assert.Equal(1, fixture.Handler.SourceReads);
            Assert.Equal(1, fixture.Dispatcher.Reconciliations);
            Assert.Null(fixture.Dispatcher.Context);
            Assert.False(fixture.Dispatcher.CleaningActivation);
            fixture.Backend.QueueRetirementReconciliation(ProbeHandler.StageId);
            Assert.Same(fixture.Work, fixture.Launcher.QueueRetirementReconciliation(ProbeHandler.StageId,
                _ => throw new InvalidOperationException("A completed stage must not be queued twice.")));
            Assert.Equal(3, fixture.Handler.StatusReads);
        }
        finally { PackageContext.Value = null; }
    }

    [Fact]
    public async Task ForeignReceiptMissingStatusTransportFailureAndExpiryCannotReadmitBeforeExactTerminalProof()
    {
        await using var fixture = new Fixture();
        fixture.Start();
        var request = await fixture.Handler.NextAsync();
        request.Reply(ProbeHandler.Status(RuntimePackageStageState.Committed) with { StageId = "another-stage" });
        request = await fixture.Handler.NextAsync();
        Assert.Equal(0, fixture.Handler.SnapshotReads);
        request.Response.SetResult(new(HttpStatusCode.NotFound));
        request = await fixture.Handler.NextAsync();
        Assert.Equal(0, fixture.Handler.SnapshotReads);
        request.Response.SetResult(new(HttpStatusCode.ServiceUnavailable));
        request = await fixture.Handler.NextAsync();
        Assert.Equal(0, fixture.Handler.SnapshotReads);
        request.Reply(ProbeHandler.Status(RuntimePackageStageState.Pending) with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) });
        request = await fixture.Handler.NextAsync();
        Assert.Equal(0, fixture.Handler.SnapshotReads);
        Assert.Equal(0, fixture.Dispatcher.Reconciliations);
        Assert.False(fixture.Work.IsCompleted);
        request.Reply(ProbeHandler.Status(RuntimePackageStageState.Failed));
        await fixture.Work.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Dispatcher.Reconciliations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownCancelsPendingStageAndPreventsAlreadyQueuedUiFromReopening(bool uiAlreadyQueued)
    {
        await using var fixture = new Fixture();
        if (uiAlreadyQueued) fixture.Dispatcher.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Start();
        var request = await fixture.Handler.NextAsync();
        if (uiAlreadyQueued)
        {
            request.Reply(ProbeHandler.Status(RuntimePackageStageState.Discarded));
            await fixture.Dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        fixture.Launcher.CloseForShutdown();
        fixture.Dispatcher.Hold?.TrySetResult();
        await fixture.Work.WaitAsync(TimeSpan.FromSeconds(5));
        if (!uiAlreadyQueued) Assert.True(request.Token.IsCancellationRequested);
        fixture.Backend.QueueRetirementReconciliation("after-shutdown");
        Assert.Equal(1, fixture.Handler.StatusReads);
        Assert.Equal(0, fixture.Dispatcher.Reconciliations);
    }

    [Fact]
    public async Task PolicyHandlerQueuesOnlyLocallyAcknowledgedOwnedStagesAndReturnsBeforeStageCompletion()
    {
        using var temporary = TemporaryDirectoryScope.Create("app-retirement-handler");
        var policy = new RetirementPolicy();
        var backend = new RecordingBackend();
        var notifications = new NotificationCenterService(temporary.GetPath("notifications.json"));
        await using var host = new PackageViewHostService(new(), [], [], [], temporary.RootPath,
            uiDispatcher: new ProbeDispatcher(), packagePolicy: policy, developerServices: backend, notificationCenter: notifications);
        var first = new RuntimeAppActivationRetirementRequirement("ours", Guid.NewGuid(), "permit", new("package", "1.0.0", new('a', 64))) { StageId = "first" };
        var second = first with { AppActivationId = Guid.NewGuid(), StageId = "second" };
        policy.Retired.Add(first.AppActivationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.Handler!(
            [first with { PolicySessionId = "foreign", StageId = "foreign" }, first, first, second], default));
        Assert.Equal(new[] { "first" }, backend.Queued);
        policy.Retired.Add(second.AppActivationId);
        await policy.Handler!([second, first with { StageId = null }], default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "first", "second" }, backend.Queued);
        Assert.Equal(new[] { first.AppActivationId }, backend.Unavailable);
        await policy.Handler!([first with { StageId = null }], default);
        var notification = Assert.Single(notifications.ListNotifications());
        Assert.Contains("provider unavailable", notification.Title);
        Assert.Contains("other packages remain available", notification.Message);
        Assert.Contains("developer logs", notification.Message);
    }

    [Fact]
    public async Task AcknowledgedUnplannedRetirementReconcilesWithoutInventingAStageReceipt()
    {
        await using var fixture = new Fixture();
        var activationId = Guid.NewGuid();
        PackageContext.Value = "unavailable-package";
        using var cleanup = new AppPackageLoadAdmission(activationId, null).EnterCleanup();
        try
        {
            fixture.Backend.QueueAvailabilityReconciliation(activationId);
            var work = fixture.Launcher.QueueRetirementReconciliation($"availability:{activationId:N}",
                _ => throw new InvalidOperationException("The exact activation must join its existing reconciliation."));
            await work.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, fixture.Handler.StatusReads);
            Assert.Equal(2, fixture.Handler.SnapshotReads);
            Assert.Equal(1, fixture.Dispatcher.Reconciliations);
            Assert.Null(fixture.Dispatcher.Context);
            Assert.False(fixture.Dispatcher.CleaningActivation);
        }
        finally { PackageContext.Value = null; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TemporaryDirectoryScope _temporary = TemporaryDirectoryScope.Create("app-stage-reconciliation");
        private readonly RuntimeClientTransport _transport;
        private readonly BackgroundProcessQueueService _queue = new();
        internal ProbeHandler Handler { get; } = new();
        internal ProbeDispatcher Dispatcher { get; } = new();
        internal WindowLauncher Launcher { get; }
        internal IAppPackageDevelopmentBackend Backend { get; }
        internal Task Work { get; private set; } = Task.CompletedTask;

        internal Fixture()
        {
            _transport = new(() => new(new Uri("http://runtime.test/"), "test-token"), Handler);
            var clients = new RuntimeApiClientFactory(_transport);
            var developer = new AppPackageDeveloperServices(_transport, clients, _queue, Dispatcher);
            Backend = developer;
            Launcher = new(PackageViewHostService.Empty, clients, new CliInstallationService(),
                new NotificationCenterService(_temporary.GetPath("notifications.json")),
                new ShellStateService(_temporary.GetPath("shell-state.json")), new ShellState(), null!, null!, null!,
                uiDispatcher: Dispatcher, backgroundProcessQueue: _queue, developerServices: developer);
        }

        internal void Start()
        {
            Backend.QueueRetirementReconciliation(ProbeHandler.StageId);
            Work = Launcher.QueueRetirementReconciliation(ProbeHandler.StageId,
                _ => throw new InvalidOperationException("A duplicate stage must join the existing shell-owned task."));
        }

        public async ValueTask DisposeAsync()
        {
            Launcher.Dispose();
            Dispatcher.Hold?.TrySetResult();
            await Work.WaitAsync(TimeSpan.FromSeconds(5));
            await _queue.DisposeAsync();
            _transport.Dispose();
            _temporary.Dispose();
        }
    }

    private sealed class ProbeHandler : HttpMessageHandler
    {
        internal const string StageId = "originating-stage";
        private static readonly Guid RuntimeId = Guid.NewGuid();
        private readonly Channel<ProbeRequest> _requests = Channel.CreateUnbounded<ProbeRequest>();
        internal int StatusReads;
        internal int SnapshotReads;
        internal int SourceReads;
        internal Task<ProbeRequest> NextAsync() => _requests.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        internal static RuntimePackageStageStatus Status(RuntimePackageStageState state)
            => new(StageId, RuntimePackageStageKind.PackageSession, state, DateTimeOffset.UtcNow,
                state == RuntimePackageStageState.Committed ? new(RuntimeId, 7) : null, state == RuntimePackageStageState.Committed, false, null)
            { ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15) };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/handshake":
                    return Json(new RuntimeHandshakeResponse(RuntimeProtocol.Identity, 1, 1, 1, RuntimeId,
                        [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1,
                            RuntimeProtocolFeatures.PackageStageStatusV1, RuntimeProtocolFeatures.AtomicPackageSnapshotV1,
                            RuntimeProtocolFeatures.TargetAwarePackageSnapshotsV1], new("Sunder.Runtime.Host", "1.0.0", "1.0.0")));
                case "/api/v1/packages/stages/" + StageId:
                    Interlocked.Increment(ref StatusReads);
                    var probe = new ProbeRequest(token, PackageContext.Value, AppPackageLoadAdmission.IsCleaningActivation);
                    await _requests.Writer.WriteAsync(probe, token);
                    return await probe.Response.Task.WaitAsync(token);
                case "/api/v1/packages/snapshot":
                    Interlocked.Increment(ref SnapshotReads);
                    return Json(new RuntimePackageSnapshot(RuntimeId, 7, 1, RuntimeBootstrapState.Ready, [], [], [], []));
                case "/api/v1/packages/ui-snapshots":
                    Interlocked.Increment(ref SourceReads);
                    return Json(Array.Empty<PackageUiSnapshotDescriptor>());
                default: throw new InvalidOperationException($"Unexpected reconciliation route: {request.RequestUri.AbsolutePath}");
            }
        }
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        internal sealed record ProbeRequest(CancellationToken Token, string? Context, bool CleaningActivation)
        {
            internal TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal void Reply(RuntimePackageStageStatus status) => Response.SetResult(Json(status));
        }
    }

    private sealed class ProbeDispatcher : IUiDispatcher
    {
        private readonly AsyncLocal<bool> _access = new();
        internal TaskCompletionSource? Hold;
        internal TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reconciliations;
        internal string? Context;
        internal bool CleaningActivation;
        public bool CheckAccess() => _access.Value;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public async Task InvokeAsync(Func<Task> action)
        {
            Queued.TrySetResult();
            if (Hold is not null) await Hold.Task;
            var previous = _access.Value;
            _access.Value = true;
            Context = PackageContext.Value;
            CleaningActivation = AppPackageLoadAdmission.IsCleaningActivation;
            try { await action(); Interlocked.Increment(ref Reconciliations); }
            finally { _access.Value = previous; }
        }
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }

    private sealed class RetirementPolicy : IAppPackagePolicy
    {
        internal HashSet<Guid> Retired { get; } = [];
        internal Func<IReadOnlyList<RuntimeAppActivationRetirementRequirement>, CancellationToken, Task>? Handler;
        public Task RequireLoadAsync(PackageUiSnapshotDescriptor snapshot, CancellationToken token) => throw new NotSupportedException();
        public bool OwnsRetirement(RuntimeAppActivationRetirementRequirement requirement) => requirement.PolicySessionId == "ours";
        public bool HasRetiredActivation(RuntimeAppActivationRetirementRequirement requirement) => Retired.Contains(requirement.AppActivationId);
        public void SetRetirementHandler(Func<IReadOnlyList<RuntimeAppActivationRetirementRequirement>, CancellationToken, Task> handler) => Handler = handler;
    }

    private sealed class RecordingBackend : IAppPackageDevelopmentBackend
    {
        internal List<string> Queued { get; } = [];
        internal List<Guid> Unavailable { get; } = [];
        public void QueueRetirementReconciliation(string stageId) => Queued.Add(stageId);
        public void QueueAvailabilityReconciliation(Guid activationId) => Unavailable.Add(activationId);
        public IDevPackageOwnerSession CreateOwner(string ownerPrefix) => throw new NotSupportedException();
        public Task<string> PrepareAsync(string folder, CancellationToken token) => throw new NotSupportedException();
        public Task<RuntimePackageSnapshot> GetSnapshotAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<bool> IsInstalledAsync(string packageId, CancellationToken token) => throw new NotSupportedException();
        public Task<string> GetAppReadinessAsync(string packageId, RuntimePackageStamp stamp, CancellationToken token) => throw new NotSupportedException();
        public Task OpenLogsAsync(string packageId, AppPackageGenerationPublication publication, CancellationToken token) => throw new NotSupportedException();
    }
}
