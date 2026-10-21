using System.Diagnostics;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class ExecutionRuntimeObservationStoreTests
{
    [Fact]
    public async Task CaptureIsExactAndIdempotentAndObservationNeverProbesOrTerminates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = await fixture.CaptureAsync();
        Assert.Equal(reference, await fixture.CaptureAsync());
        Assert.Equal(fixture.Execution.HostInstanceId, reference.RuntimeInstanceId);
        Assert.Equal(RuntimeWorkerExecutionState.CleanupPending, (await fixture.ObserveAsync(reference)).State);
        Assert.Equal(0, fixture.Containment.Probes);
        Assert.Equal(0, fixture.Containment.Terminations);
        await Assert.ThrowsAsync<ExecutionRuntimeObservationCorruptException>(() => fixture.Registry.CaptureExecutionAsync(
            fixture.Reservation with { ActivationId = Guid.NewGuid() }, Fixture.PackageId, default));
    }

    [Fact]
    public async Task RootExitDoesNotReplaceContainmentProofAndMissingLeaseIsUnknown()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = await fixture.CaptureAsync();
        // The fake payload may be gone while the owning containment still has descendants.
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Lease.ReleaseAfterProcessTreeTerminationAsync());
        Assert.Equal(RuntimeWorkerExecutionState.CleanupPending, (await fixture.ObserveAsync(reference)).State);
        Assert.False(File.Exists(fixture.CleanupPath));
        File.Delete(fixture.LeasePath); // Legacy/lost evidence is explicitly not a positive receipt.
        Assert.Equal(RuntimeWorkerExecutionState.Unknown, (await fixture.ObserveAsync(reference)).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReceiptWriteFailureRetainsLeaseAndIndeterminateVisibleReceiptStaysPending(bool publishBeforeFailure)
    {
        var documents = new FaultingDocuments { FailCleanup = true, PublishBeforeFailure = publishBeforeFailure };
        await using var fixture = await Fixture.CreateAsync(documents);
        var reference = await fixture.CaptureAsync();
        fixture.Containment.State = ExecutionRuntimeContainmentProbe.Empty;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Lease.ReleaseAfterProcessTreeTerminationAsync());
        Assert.True(File.Exists(fixture.LeasePath));
        Assert.Equal(publishBeforeFailure, File.Exists(fixture.CleanupPath));
        Assert.Equal(RuntimeWorkerExecutionState.CleanupPending, (await fixture.ObserveAsync(reference)).State);
        var firstReceipt = publishBeforeFailure ? await File.ReadAllBytesAsync(fixture.CleanupPath) : null;
        documents.FailCleanup = false;
        await fixture.Lease.ReleaseAfterProcessTreeTerminationAsync();
        Assert.False(File.Exists(fixture.LeasePath));
        var confirmed = await fixture.ObserveAsync(reference);
        Assert.Equal(reference, confirmed.Reference);
        Assert.Equal(RuntimeWorkerExecutionState.CleanupConfirmed, confirmed.State);
        Assert.NotNull(confirmed.ConfirmedAtUtc);
        if (firstReceipt is not null) Assert.Equal(firstReceipt, await File.ReadAllBytesAsync(fixture.CleanupPath));
    }

    [Fact]
    public async Task RecoveryConfirmsOriginalExecutionOnlyAfterDrainWithoutRequiringOriginalHost()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = await fixture.CaptureAsync();
        var successor = fixture.NewRegistry(Guid.NewGuid());
        await successor.RecoverAsync(default);
        Assert.Equal(1, fixture.Containment.Terminations);
        var result = await successor.ObserveExecutionAsync(reference, Fixture.PackageId, default);
        Assert.Equal(RuntimeWorkerExecutionState.CleanupConfirmed, result.State);
        Assert.Equal(fixture.Execution.HostInstanceId, result.Reference.RuntimeInstanceId);
    }

    [Theory]
    [InlineData((int)ExecutionRuntimeContainmentProbe.Unavailable)]
    [InlineData((int)ExecutionRuntimeContainmentProbe.IdentityMismatch)]
    public async Task UnverifiableContainmentNeverPublishesReceiptOrDeletesLease(int probe)
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = await fixture.CaptureAsync();
        fixture.Containment.State = (ExecutionRuntimeContainmentProbe)probe;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.NewRegistry(Guid.NewGuid()).RecoverAsync(default));
        Assert.True(File.Exists(fixture.LeasePath));
        Assert.False(File.Exists(fixture.CleanupPath));
        Assert.Equal(RuntimeWorkerExecutionState.CleanupPending, (await fixture.ObserveAsync(reference)).State);
        Assert.Equal(0, fixture.Containment.Terminations);
    }

    [Fact]
    public async Task ReferenceCannotBeForgedBorrowedByAnotherPackageOrTransferredToAnotherHostIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = await fixture.CaptureAsync();
        await Assert.ThrowsAsync<ExecutionRuntimeObservationOwnerException>(() => fixture.Registry.ObserveExecutionAsync(
            reference, "another.package", default));
        var tail = reference.Value[^1] == '0' ? '1' : '0';
        Assert.Equal(RuntimeWorkerExecutionState.Unknown,
            (await fixture.ObserveAsync(reference with { Value = reference.Value[..^1] + tail })).State);
        Assert.Equal(RuntimeWorkerExecutionState.Unknown,
            (await fixture.ObserveAsync(reference with { RuntimeInstanceId = Guid.NewGuid() })).State);
        Assert.Equal(RuntimeWorkerExecutionState.Unknown,
            (await fixture.ObserveAsync(new(Guid.NewGuid(), "legacy-reference"))).State);
    }

    [Fact]
    public async Task CorruptPositiveReceiptCannotConfirmCleanup()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = await fixture.CaptureAsync();
        fixture.Containment.State = ExecutionRuntimeContainmentProbe.Empty;
        await fixture.Lease.ReleaseAfterProcessTreeTerminationAsync();
        var cleanup = JsonSerializer.Deserialize<ExecutionRuntimeObservationCleanupV1>(
            await File.ReadAllBytesAsync(fixture.CleanupPath), ExecutionRuntimeStore.JsonOptions)!;
        await File.WriteAllTextAsync(fixture.CleanupPath, JsonSerializer.Serialize(cleanup with
        { Execution = cleanup.Execution with { ContainmentGeneration = Guid.NewGuid() } }, ExecutionRuntimeStore.JsonOptions));
        await Assert.ThrowsAsync<ExecutionRuntimeObservationCorruptException>(() => fixture.ObserveAsync(reference));
    }

    [Theory]
    [InlineData("malformed-json")]
    [InlineData("execution-changed")]
    public async Task CorruptCaptureCannotBeReadRecapturedOrReleasedAsPositiveEvidence(string corruption)
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = await fixture.CaptureAsync();
        var path = fixture.Layout.GetExecutionObservationCapturePath(fixture.Execution.LeaseId);
        var capture = JsonSerializer.Deserialize<ExecutionRuntimeObservationCaptureV1>(
            await File.ReadAllBytesAsync(path), ExecutionRuntimeStore.JsonOptions)!;
        var corrupt = corruption == "malformed-json" ? "{" : JsonSerializer.Serialize(capture with
        { Execution = capture.Execution with { GenerationId = "not-the-captured-preparation" } }, ExecutionRuntimeStore.JsonOptions);
        await File.WriteAllTextAsync(path, corrupt);

        await Assert.ThrowsAsync<ExecutionRuntimeObservationCorruptException>(() => fixture.ObserveAsync(reference));
        await Assert.ThrowsAsync<ExecutionRuntimeObservationCorruptException>(() => fixture.CaptureAsync());
        Assert.Equal(0, fixture.Containment.Probes);
        fixture.Containment.State = ExecutionRuntimeContainmentProbe.Empty;
        await Assert.ThrowsAsync<ExecutionRuntimeObservationCorruptException>(() => fixture.Lease.ReleaseAfterProcessTreeTerminationAsync());
        Assert.True(File.Exists(fixture.LeasePath));
        Assert.False(File.Exists(fixture.CleanupPath));
        Assert.Equal(corrupt, await File.ReadAllTextAsync(path));
        Assert.Equal(0, fixture.Containment.Terminations);
    }

    [Fact]
    public async Task LinkedCaptureSubtreeCannotBorrowOutsideEvidenceOrPermitLeaseDeletion()
    {
        if (OperatingSystem.IsWindows()) return; // Match existing store symlink qualification.
        await using var fixture = await Fixture.CreateAsync();
        var reference = await fixture.CaptureAsync();
        var link = fixture.Layout.ExecutionObservationCaptureRootPath;
        var target = Path.Combine(fixture.Layout.RuntimeStateRootPath, "outside-observation-captures");
        Directory.Move(link, target);
        var linked = false;
        try
        {
            Directory.CreateSymbolicLink(link, target);
            linked = true;
            var evidence = Path.Combine(target, fixture.Execution.LeaseId + ".json");
            var before = await File.ReadAllBytesAsync(evidence);
            await Assert.ThrowsAsync<ExecutionRuntimeObservationCorruptException>(() => fixture.ObserveAsync(reference));
            await Assert.ThrowsAsync<ExecutionRuntimeObservationCorruptException>(() => fixture.CaptureAsync());
            Assert.Equal(0, fixture.Containment.Probes);
            fixture.Containment.State = ExecutionRuntimeContainmentProbe.Empty;
            await Assert.ThrowsAsync<ExecutionRuntimeObservationCorruptException>(() => fixture.Lease.ReleaseAfterProcessTreeTerminationAsync());
            Assert.Equal(before, await File.ReadAllBytesAsync(evidence));
            Assert.True(File.Exists(fixture.LeasePath));
            Assert.False(File.Exists(fixture.CleanupPath));
            Assert.Equal(0, fixture.Containment.Terminations);
        }
        finally
        {
            if (linked) Directory.Delete(link);
            Directory.Move(target, link);
        }
    }

    [Fact]
    public async Task CapacityBackpressurePreservesEveryOutstandingReferenceAndAllowsExactRecapture()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reference = await fixture.CaptureAsync();
        for (var index = 1; index < ExecutionRuntimeLeaseRegistry.MaximumExecutionObservationsPerPackage; index++)
        {
            var id = Guid.NewGuid().ToString("N");
            var execution = fixture.Execution with { LeaseId = id, ActivationId = Guid.NewGuid(),
                ContainmentIdentity = OperatingSystem.IsWindows() ? $@"Local\Sunder.ExecutionRuntime.{id}" : fixture.Execution.ContainmentIdentity };
            var capture = new ExecutionRuntimeObservationCaptureV1(1,
                ExecutionRuntimeObservationReferences.Create(execution), execution, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(fixture.Layout.GetExecutionObservationCapturePath(id),
                JsonSerializer.Serialize(capture, ExecutionRuntimeStore.JsonOptions));
        }
        Assert.Equal(reference, await fixture.CaptureAsync());
        var activation = Guid.NewGuid();
        var nextLease = await fixture.Registry.ReserveAsync(new(Fixture.PackageId, "generation", activation),
            fixture.Execution.ContentIdentity, fixture.Execution.RuntimeRootPath!, fixture.Execution.ExecutablePath!, default);
        var extra = fixture.Execution with { LeaseId = nextLease.LeaseId, ActivationId = activation };
        if (OperatingSystem.IsWindows()) extra = extra with { ContainmentIdentity = $@"Local\Sunder.ExecutionRuntime.{extra.LeaseId}" };
        await File.WriteAllTextAsync(fixture.Layout.GetLeasePath(nextLease.LeaseId), JsonSerializer.Serialize(extra, ExecutionRuntimeStore.JsonOptions));
        await Assert.ThrowsAsync<ExecutionRuntimeObservationCapacityException>(() => fixture.Registry.CaptureExecutionAsync(
            new(extra.LeaseId, Fixture.PackageId, "generation", activation), Fixture.PackageId, default));
        Assert.Equal(RuntimeWorkerExecutionState.CleanupPending, (await fixture.ObserveAsync(reference)).State);
        Assert.Equal(reference, await fixture.CaptureAsync());
        Assert.Equal(ExecutionRuntimeLeaseRegistry.MaximumExecutionObservationsPerPackage,
            Directory.EnumerateFiles(fixture.Layout.ExecutionObservationCaptureRootPath, "*.json").Count());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string PackageId = "observation.owner";
        private readonly string root = Path.Combine(Path.GetTempPath(), "sunder-execution-observations", Guid.NewGuid().ToString("N"));
        private readonly IExecutionRuntimeObservationDocuments? documents;
        internal ExecutionRuntimeStoreLayout Layout { get; }
        internal Containment Containment { get; } = new();
        internal ExecutionRuntimeLeaseRegistry Registry { get; }
        internal IExecutionRuntimeProcessLease Lease { get; private set; } = null!;
        internal ExecutionRuntimeProcessLeaseV1 Execution { get; private set; } = null!;
        internal RuntimeExecutionReservationIdentity Reservation => new(Execution.LeaseId, PackageId, Execution.GenerationId, Execution.ActivationId);
        internal string LeasePath => Layout.GetLeasePath(Execution.LeaseId);
        internal string CleanupPath => Layout.GetExecutionObservationCleanupPath(Execution.LeaseId);

        private Fixture(IExecutionRuntimeObservationDocuments? documents)
        {
            this.documents = documents;
            Layout = new(root);
            foreach (var path in Layout.DurableRootPaths) Directory.CreateDirectory(path);
            Registry = NewRegistry(Guid.NewGuid());
        }

        internal static async Task<Fixture> CreateAsync(IExecutionRuntimeObservationDocuments? documents = null)
        {
            var fixture = new Fixture(documents);
            var content = new string('a', 64);
            var runtime = fixture.Layout.GetContentRootPath(content);
            Directory.CreateDirectory(runtime);
            var executable = Path.Combine(runtime, OperatingSystem.IsWindows() ? "worker.exe" : "worker");
            await File.WriteAllBytesAsync(executable, []);
            fixture.Lease = await fixture.Registry.ReserveAsync(new(PackageId, "generation", Guid.NewGuid()),
                content, runtime, executable, default);
            var path = fixture.Layout.GetLeasePath(fixture.Lease.LeaseId);
            var reservation = JsonSerializer.Deserialize<ExecutionRuntimeProcessLeaseV1>(await File.ReadAllBytesAsync(path), ExecutionRuntimeStore.JsonOptions)!;
            var now = reservation.ReservedAtUtc;
            fixture.Execution = reservation with
            {
                ProcessStartRequestedAtUtc = now, ContainmentGeneration = Guid.NewGuid(),
                ContainmentKind = OperatingSystem.IsWindows() ? "windows-job-v1" : "unix-process-group-v1",
                ContainmentIdentity = OperatingSystem.IsWindows() ? $@"Local\Sunder.ExecutionRuntime.{reservation.LeaseId}" : "12345",
                ContainmentProcessId = 12345, ContainmentProcessBirthIdentity = ExecutionRuntimeProcessBirthIdentity.CreateSyntheticForCurrentPlatform(1),
                ContainmentProcessStartedAtUtc = now, ContainmentExecutablePath = executable, ContainmentAttachedAtUtc = now,
                ProcessId = 12346, ProcessBirthIdentity = ExecutionRuntimeProcessBirthIdentity.CreateSyntheticForCurrentPlatform(2),
                ProcessStartedAtUtc = now, ExecutablePath = executable, RuntimeRootPath = runtime, AttachedAtUtc = now,
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(fixture.Execution, ExecutionRuntimeStore.JsonOptions));
            return fixture;
        }

        internal ExecutionRuntimeLeaseRegistry NewRegistry(Guid hostId)
            => new(Layout, hostId, TimeProvider.System, containment: Containment, observationDocuments: documents);
        internal Task<RuntimeWorkerExecutionReference> CaptureAsync() => Registry.CaptureExecutionAsync(Reservation, PackageId, default);
        internal Task<RuntimeWorkerExecutionObservation> ObserveAsync(RuntimeWorkerExecutionReference reference)
            => Registry.ObserveExecutionAsync(reference, PackageId, default);
        public ValueTask DisposeAsync() { Directory.Delete(root, recursive: true); return ValueTask.CompletedTask; }
    }

    private sealed class Containment : IExecutionRuntimeProcessContainmentController
    {
        internal ExecutionRuntimeContainmentProbe State = ExecutionRuntimeContainmentProbe.Active;
        internal int Probes;
        internal int Terminations;
        public ExecutionRuntimeContainmentProbe Probe(ExecutionRuntimeProcessLeaseV1 lease) { Probes++; return State; }
        public Task<bool> TerminateAndDrainAsync(ExecutionRuntimeProcessLeaseV1 lease, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Terminations++; State = ExecutionRuntimeContainmentProbe.Empty; return Task.FromResult(true); }
        public Task<IExecutionRuntimePendingContainedProcess> StartAsync(string leaseId, Guid generation, ProcessStartInfo start,
            string report, CancellationToken token) => throw new InvalidOperationException("Observation tests must not start processes.");
    }

    private sealed class FaultingDocuments : IExecutionRuntimeObservationDocuments
    {
        internal bool FailCleanup;
        internal bool PublishBeforeFailure;
        public Task WriteCaptureAsync(string path, ExecutionRuntimeObservationCaptureV1 capture, CancellationToken token)
            => DurableJsonDocument.WriteAsync(path, capture, ExecutionRuntimeStore.JsonOptions, token);
        public async Task WriteCleanupAsync(string path, ExecutionRuntimeObservationCleanupV1 cleanup, CancellationToken token)
        {
            if (!FailCleanup || PublishBeforeFailure)
                await DurableJsonDocument.WriteAsync(path, cleanup, ExecutionRuntimeStore.JsonOptions, token);
            if (FailCleanup) throw PublishBeforeFailure ? new DurableWriteIndeterminateException(path, new IOException("Injected durability failure."))
                : new IOException("Injected receipt write failure.");
        }
    }
}
