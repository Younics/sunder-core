using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed partial class ExecutionRuntimeLeaseRegistry
{
    // One slot per execution, shared by every job/provider using that execution. Terminal
    // references remain outstanding: capture/query alone supplies no safe acknowledgement.
    internal const int MaximumExecutionObservations = 4096;
    internal const int MaximumExecutionObservationsPerPackage = 256;
    private const int MaximumObservationDocumentBytes = 64 * 1024;
    private readonly IExecutionRuntimeObservationDocuments _observationDocuments;

    internal async Task<RuntimeWorkerExecutionReference> CaptureExecutionAsync(
        RuntimeExecutionReservationIdentity reservation, string ownerPackageId, CancellationToken token)
    {
        using var fence = await EnterFenceAsync(token).ConfigureAwait(false);
        var leasePath = _layout.GetLeasePath(reservation.LeaseId);
        if (!ObservationFileExists(leasePath))
            throw new RuntimeUnavailableException("The selected Worker execution is no longer available for capture.");
        var execution = await ReadAsync(leasePath, token).ConfigureAwait(false);
        ValidateObservedExecution(execution);
        if (execution.HostInstanceId != _hostInstanceId || execution.PackageId != reservation.PackageId
            || execution.GenerationId != reservation.GenerationId || execution.ActivationId != reservation.ActivationId
            || execution.LeaseId != reservation.LeaseId)
            throw CorruptObservation("The selected Worker does not own the exact execution reservation.");
        if (execution.PackageId != ownerPackageId) throw new ExecutionRuntimeObservationOwnerException();

        var capturePath = _layout.GetExecutionObservationCapturePath(execution.LeaseId);
        var capture = await ReadObservationAsync<ExecutionRuntimeObservationCaptureV1>(capturePath, token).ConfigureAwait(false);
        if (capture is not null)
        {
            ValidateCapture(capture, execution.LeaseId);
            RequireSameObservedExecution(capture.Execution, execution);
            // A previous capture write may have become visible before failing its
            // durability barrier. Reflush the identical registration before reissuing it.
            await _observationDocuments.WriteCaptureAsync(capturePath, capture, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return capture.Reference;
        }
        if (ObservationFileExists(_layout.GetExecutionObservationCleanupPath(execution.LeaseId)))
            throw CorruptObservation("An execution cleanup receipt has no capture registration.");

        await RequireObservationCapacityUnderFenceAsync(ownerPackageId, token).ConfigureAwait(false);
        CreateObservationDirectoriesUnderFence();
        capture = new(ExecutionRuntimeObservationCaptureV1.CurrentSchemaVersion,
            ExecutionRuntimeObservationReferences.Create(execution), execution, _timeProvider.GetUtcNow());
        ValidateCapture(capture, execution.LeaseId);
        await _observationDocuments.WriteCaptureAsync(capturePath, capture, token).ConfigureAwait(false);
        // A late cancellation retains the registration but cannot authorize a caller's Start.
        token.ThrowIfCancellationRequested();
        return capture.Reference;
    }

    internal async Task<RuntimeWorkerExecutionObservation> ObserveExecutionAsync(
        RuntimeWorkerExecutionReference reference, string ownerPackageId, CancellationToken token)
    {
        if (!ExecutionRuntimeObservationReferences.TryGetLeaseId(reference, out var leaseId))
            return new(reference, RuntimeWorkerExecutionState.Unknown);
        using var fence = await EnterFenceAsync(token).ConfigureAwait(false);
        var capture = await ReadObservationAsync<ExecutionRuntimeObservationCaptureV1>(
            _layout.GetExecutionObservationCapturePath(leaseId), token).ConfigureAwait(false);
        if (capture is null)
        {
            if (ObservationFileExists(_layout.GetExecutionObservationCleanupPath(leaseId)))
                throw CorruptObservation("An execution cleanup receipt has no capture registration.");
            return new(reference, RuntimeWorkerExecutionState.Unknown);
        }
        ValidateCapture(capture, leaseId);
        if (capture.Reference != reference) return new(reference, RuntimeWorkerExecutionState.Unknown);
        if (capture.Execution.PackageId != ownerPackageId) throw new ExecutionRuntimeObservationOwnerException();

        var cleanup = await ReadObservationAsync<ExecutionRuntimeObservationCleanupV1>(
            _layout.GetExecutionObservationCleanupPath(leaseId), token).ConfigureAwait(false);
        if (cleanup is not null) ValidateCleanup(cleanup, capture);
        var leasePath = _layout.GetLeasePath(leaseId);
        if (ObservationFileExists(leasePath))
        {
            var execution = await ReadAsync(leasePath, token).ConfigureAwait(false);
            ValidateObservedExecution(execution);
            RequireSameObservedExecution(capture.Execution, execution);
            return new(reference, RuntimeWorkerExecutionState.CleanupPending);
        }
        // Neither provider withdrawal nor a missing lease is positive cleanup evidence.
        // A visible receipt from an indeterminate write remains pending while its lease exists.
        token.ThrowIfCancellationRequested();
        return cleanup is null ? new(reference, RuntimeWorkerExecutionState.Unknown)
            : new(reference, RuntimeWorkerExecutionState.CleanupConfirmed) { ConfirmedAtUtc = cleanup.ConfirmedAtUtc };
    }

    private async Task RecordObservedCleanupUnderFenceAsync(ExecutionRuntimeProcessLeaseV1 execution, CancellationToken token)
    {
        var capture = await ReadObservationAsync<ExecutionRuntimeObservationCaptureV1>(
            _layout.GetExecutionObservationCapturePath(execution.LeaseId), token).ConfigureAwait(false);
        if (capture is null)
        {
            if (ObservationFileExists(_layout.GetExecutionObservationCleanupPath(execution.LeaseId)))
                throw CorruptObservation("An execution cleanup receipt has no capture registration.");
            return; // Unobserved/legacy executions do not acquire invented retrospective references.
        }
        ValidateCapture(capture, execution.LeaseId);
        RequireSameObservedExecution(capture.Execution, execution);
        // Every caller already owns the store fence. Probe the complete leased containment,
        // not just the payload root, immediately before publishing its positive receipt.
        if (_containment.Probe(execution) != ExecutionRuntimeContainmentProbe.Empty)
            throw new IOException("Observed Worker containment has not been proven empty.");
        var path = _layout.GetExecutionObservationCleanupPath(execution.LeaseId);
        var cleanup = await ReadObservationAsync<ExecutionRuntimeObservationCleanupV1>(path, token).ConfigureAwait(false);
        if (cleanup is not null) ValidateCleanup(cleanup, capture);
        else cleanup = new(ExecutionRuntimeObservationCleanupV1.CurrentSchemaVersion, capture.Reference,
            execution, ExecutionRuntimeObservationCleanupV1.ContainmentEmptyProof, _timeProvider.GetUtcNow(), _hostInstanceId);
        ValidateCleanup(cleanup, capture);
        CreateObservationDirectoriesUnderFence();
        // Retry reflushes only the identical receipt, including its first confirmation time.
        // Any write/flush failure propagates BEFORE the owning live lease can be deleted.
        await _observationDocuments.WriteCleanupAsync(path, cleanup, token).ConfigureAwait(false);
    }

    private void RequireUnobservedReservationUnderFence(string leaseId)
    {
        if (ObservationFileExists(_layout.GetExecutionObservationCapturePath(leaseId))
            || ObservationFileExists(_layout.GetExecutionObservationCleanupPath(leaseId)))
            throw CorruptObservation("An unstarted reservation cannot own an attached execution observation.");
    }

    private async Task RequireObservationCapacityUnderFenceAsync(string packageId, CancellationToken token)
    {
        GuardObservationPath(_layout.ExecutionObservationCaptureRootPath);
        if (!Directory.Exists(_layout.ExecutionObservationCaptureRootPath)) return;
        var total = 0;
        var owned = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(_layout.ExecutionObservationCaptureRootPath))
        {
            token.ThrowIfCancellationRequested();
            if (++total > MaximumExecutionObservations) throw new ExecutionRuntimeObservationCapacityException();
            GuardObservationPath(path);
            if (Directory.Exists(path)) throw CorruptObservation("The execution capture directory contains a nested directory.");
            var name = Path.GetFileName(path);
            // Preserve crash-left atomic candidates and count them against capacity. They
            // are never evidence and are never silently promoted/deleted by an observation.
            if (name.Contains(".json.tmp-", StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".json", StringComparison.Ordinal)
                || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _))
                throw CorruptObservation("The execution capture directory contains an unexpected entry.");
            var capture = await ReadObservationAsync<ExecutionRuntimeObservationCaptureV1>(path, token).ConfigureAwait(false)
                ?? throw CorruptObservation("An execution capture disappeared under the owning fence.");
            ValidateCapture(capture, Path.GetFileNameWithoutExtension(name));
            if (capture.Execution.PackageId == packageId) owned++;
        }
        if (total >= MaximumExecutionObservations || owned >= MaximumExecutionObservationsPerPackage)
            throw new ExecutionRuntimeObservationCapacityException();
    }

    private void CreateObservationDirectoriesUnderFence()
    {
        foreach (var path in new[] { _layout.ExecutionObservationRootPath,
                     _layout.ExecutionObservationCaptureRootPath, _layout.ExecutionObservationCleanupRootPath })
        {
            GuardObservationPath(path);
            DurableJsonDocument.CreateDirectory(path);
            GuardObservationPath(path);
        }
    }

    private void ValidateObservedExecution(ExecutionRuntimeProcessLeaseV1 execution)
    {
        try
        {
            Validate(execution, _layout.GetLeasePath(execution.LeaseId));
            if (!execution.HasContainment || !execution.IsAttached || execution.ContainmentGeneration is null)
                throw CorruptObservation("Execution observations require a complete contained Worker attachment.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException)
        { throw CorruptObservation("Execution observation identity is invalid.", error); }
    }

    private void ValidateCapture(ExecutionRuntimeObservationCaptureV1 capture, string leaseId)
    {
        if (capture.SchemaVersion != ExecutionRuntimeObservationCaptureV1.CurrentSchemaVersion
            || capture.CapturedAtUtc == default || capture.Execution is null
            || !ExecutionRuntimeObservationReferences.TryGetLeaseId(capture.Reference, out var referenceLease)
            || referenceLease != leaseId || capture.Execution.LeaseId != leaseId
            || capture.Reference.RuntimeInstanceId != capture.Execution.HostInstanceId)
            throw CorruptObservation("The execution capture registration is invalid.");
        ValidateObservedExecution(capture.Execution);
    }

    private void ValidateCleanup(ExecutionRuntimeObservationCleanupV1 cleanup, ExecutionRuntimeObservationCaptureV1 capture)
    {
        if (cleanup.SchemaVersion != ExecutionRuntimeObservationCleanupV1.CurrentSchemaVersion
            || cleanup.Reference != capture.Reference || cleanup.Execution != capture.Execution
            || cleanup.ProofKind != ExecutionRuntimeObservationCleanupV1.ContainmentEmptyProof
            || cleanup.ConfirmedAtUtc == default || cleanup.ConfirmingHostInstanceId == Guid.Empty)
            throw CorruptObservation("The execution cleanup receipt does not match its exact captured execution.");
    }

    private static void RequireSameObservedExecution(ExecutionRuntimeProcessLeaseV1 expected, ExecutionRuntimeProcessLeaseV1 actual)
    {
        if (expected != actual) throw CorruptObservation("The captured execution lease identity changed.");
    }

    private async Task<T?> ReadObservationAsync<T>(string path, CancellationToken token) where T : class
    {
        if (!ObservationFileExists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is <= 0 or > MaximumObservationDocumentBytes)
                throw CorruptObservation("Execution observation evidence has an invalid size.");
            return await JsonSerializer.DeserializeAsync<T>(stream, ExecutionRuntimeStore.JsonOptions, token).ConfigureAwait(false)
                ?? throw CorruptObservation("Execution observation evidence is empty.");
        }
        catch (JsonException error) { throw CorruptObservation("Execution observation evidence is malformed.", error); }
    }

    private bool ObservationFileExists(string path)
    {
        GuardObservationPath(path);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw CorruptObservation("Execution observation evidence names a directory instead of a file.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private void GuardObservationPath(string path)
    {
        try
        {
            PackageStorePathGuard.EnsureSafe(path, _layout.LeaseRootPath);
            foreach (var directory in new[] { _layout.ExecutionObservationRootPath,
                         _layout.ExecutionObservationCaptureRootPath, _layout.ExecutionObservationCleanupRootPath })
            {
                if (!path.StartsWith(directory, PathComparison)) continue;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0)
                        throw CorruptObservation("An execution observation directory was replaced by a file.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        catch (InvalidDataException error) { throw CorruptObservation("Execution observation evidence has an unsafe path.", error); }
    }

    private static ExecutionRuntimeObservationCorruptException CorruptObservation(string message, Exception? inner = null)
        => new(message, inner);
}
