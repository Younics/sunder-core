using System.Text.Json;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Storage;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class SunderWorkerRuntime
{
    internal async ValueTask ReportGenerationFaultAsync(
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(message);
        if (!WorkerJson.IsSafeErrorCode(code))
        {
            throw new ArgumentException(
                "A generation-fault code must be a bounded ASCII error token.",
                nameof(code));
        }
        var safeMessage = WorkerDiagnosticSink.Sanitize(message, 512);
        if (safeMessage.Length == 0)
        {
            throw new ArgumentException("A generation-fault message must contain safe text.", nameof(message));
        }
        cancellationToken.ThrowIfCancellationRequested();

        long sessionGeneration;
        lock (Gate)
        {
            ThrowIfFaultedUnderGate();
            if (_phase != RuntimePhase.Active || _sessionGeneration is null)
            {
                throw new InvalidOperationException(
                    "A worker may report a generation fault only for its active committed generation.");
            }
            if (_generationFaultReported)
            {
                throw new InvalidOperationException("This worker already reported a fault for its active generation.");
            }
            _generationFaultReported = true;
            sessionGeneration = _sessionGeneration.Value;
        }

        var body = WorkerJson.Serialize(_limits, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "worker.generation-fault");
            writer.WriteString("activationId", _environment.ActivationId);
            writer.WriteNumber("sessionGeneration", sessionGeneration);
            writer.WriteString("code", code);
            writer.WriteString("message", safeMessage);
            writer.WriteEndObject();
        });
        await _output.WriteAsync(body).ConfigureAwait(false);
    }

    private async ValueTask HandleHelloAsync(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(
            root,
            "type",
            "protocol",
            "protocolVersion",
            "challenge",
            "packageId",
            "packageVersion",
            "activationId",
            "sessionId",
            "providers",
            "resourceProfile",
            "resourceProfileHash");
        lock (Gate)
        {
            if (_phase != RuntimePhase.WaitingForHello)
            {
                throw new WorkerProtocolException("host.hello is duplicate or out of order.");
            }
        }

        var protocol = WorkerJson.RequiredString(root, "protocol", 64);
        var protocolVersion = WorkerJson.RequiredNonNegativeInt64(root, "protocolVersion");
        var challenge = WorkerJson.RequiredString(root, "challenge", 128);
        var packageId = WorkerJson.RequiredString(root, "packageId", 256);
        var packageVersion = WorkerJson.RequiredString(root, "packageVersion", SemanticVersion.MaximumLength);
        var activationId = WorkerJson.RequiredSafeId(root, "activationId", 64);
        var sessionId = WorkerJson.RequiredSafeId(root, "sessionId", 256);
        if (!string.Equals(protocol, WorkerProtocol.Name, StringComparison.Ordinal)
            || protocolVersion != WorkerProtocol.Version
            || !string.Equals(protocol, _environment.Protocol, StringComparison.Ordinal)
            || !string.Equals(packageId, _environment.PackageId, StringComparison.Ordinal)
            || !string.Equals(packageVersion, _environment.PackageVersion, StringComparison.Ordinal)
            || !string.Equals(activationId, _environment.ActivationId, StringComparison.Ordinal)
            || !string.Equals(sessionId, _environment.SessionId, StringComparison.Ordinal))
        {
            throw new WorkerProtocolException(
                "host.hello does not match the exact protocol and sanitized process activation environment.");
        }

        var resourceProfileHash = WorkerJson.RequiredString(root, "resourceProfileHash", 64);
        var resourceProfile = WorkerWire.ReadResourceProfile(
            WorkerJson.RequiredValue(root, "resourceProfile"),
            resourceProfileHash);
        _client.ResourceProfile = new SunderRpcResourceProfile(
            resourceProfileHash,
            resourceProfile.RpcValueBytes,
            resourceProfile.ValueDepth,
            resourceProfile.ValueNodes,
            resourceProfile.ObjectMembers,
            resourceProfile.InlineDecodedBytes,
            resourceProfile.ContentReferenceBytes);
        _limits.ApplyResourceProfile(resourceProfile);

        ConfigureForActivation();

        var providersValue = WorkerJson.RequiredValue(root, "providers");
        if (providersValue.ValueKind != JsonValueKind.Array)
        {
            throw new WorkerProtocolException("Host hello providers must be an array.");
        }
        var declaredProviders = providersValue.EnumerateArray()
            .Select(WorkerWire.ReadProviderIdentity)
            .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
            .ToArray();
        if (!declaredProviders.SequenceEqual(_providerIdentities))
        {
            throw new WorkerProtocolException(
                "Registered providers do not exactly match the Host-declared Runtime providers.");
        }

        var body = WorkerJson.Serialize(_limits, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "worker.ready");
            writer.WriteString("protocol", WorkerProtocol.Name);
            writer.WriteNumber("protocolVersion", WorkerProtocol.Version);
            writer.WriteString("challenge", challenge);
            writer.WriteString("packageId", packageId);
            writer.WriteString("packageVersion", packageVersion);
            writer.WriteString("activationId", activationId);
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("resourceProfileHash", resourceProfileHash);
            WriteContributions(writer);
            writer.WriteEndObject();
        });
        await _output.WriteAsync(body).ConfigureAwait(false);
        lock (Gate)
        {
            if (_phase != RuntimePhase.WaitingForHello)
            {
                throw new WorkerProtocolException("Worker state changed while completing host.hello.");
            }
            _phase = RuntimePhase.Ready;
        }
    }

    private void ConfigureForActivation()
    {
        if (_configuration is not null)
        {
            throw new WorkerProtocolException("Worker activation configuration ran more than once.");
        }
        var configuration = _configure(_client)
                            ?? throw new InvalidOperationException("Worker configuration returned null.");
        _configuration = configuration;
        _providers = configuration.Providers.ToDictionary(
            static provider => provider.ProviderId,
            StringComparer.Ordinal);
        _providerIdentities = configuration.Providers
            .Select(provider => new WorkerProviderIdentity(
                provider.ProviderId,
                provider.ContractId,
                provider.ContractVersion,
                provider.DescriptorSha256,
                provider.AbiSha256))
            .OrderBy(static provider => provider.ProviderId, StringComparer.Ordinal)
            .ToArray();
    }

    private WorkerRuntimeConfiguration Configuration
        => _configuration ?? throw new WorkerProtocolException("Worker activation is not configured.");

    private void WriteContributions(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("contributions");
        writer.WriteStartObject();
        writer.WritePropertyName("settingsSchema");
        if (Configuration.SettingsSchema is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            WorkerWire.WriteSettingsSchema(writer, Configuration.SettingsSchema);
        }
        writer.WritePropertyName("storageMigrations");
        writer.WriteStartArray();
        WriteStorageMigrationPlan(writer, "state", Configuration.StateMigrations);
        WriteStorageMigrationPlan(writer, "secrets", Configuration.SecretMigrations);
        writer.WriteEndArray();
        writer.WritePropertyName("callbackHandlers");
        writer.WriteStartArray();
        foreach (var handler in Configuration.CallbackHandlers
                     .OrderBy(static handler => handler.CallbackHandlerId, StringComparer.Ordinal))
        {
            writer.WriteStringValue(handler.CallbackHandlerId);
        }
        writer.WriteEndArray();
        writer.WriteBoolean("authHandler", Configuration.AuthHandler is not null);
        writer.WritePropertyName("rpcProviders");
        writer.WriteStartArray();
        foreach (var provider in _providerIdentities)
        {
            WorkerWire.WriteProviderIdentity(writer, provider);
        }
        writer.WriteEndArray();
        writer.WriteBoolean("candidateLifecycle", Configuration.OnCandidateStarted is not null);
        writer.WriteBoolean("generationLifecycle", Configuration.OnGenerationCommitted is not null);
        writer.WriteBoolean("migrationPreparationLifecycle", true);
        writer.WriteEndObject();
    }

    private static void WriteStorageMigrationPlan(
        Utf8JsonWriter writer,
        string store,
        IReadOnlyList<PackageStorageKeyMigration> migrations)
    {
        if (migrations.Count == 0) return;
        writer.WriteStartObject();
        writer.WriteString("store", store);
        writer.WritePropertyName("rules");
        writer.WriteStartArray();
        foreach (var migration in migrations)
        {
            var descriptor = migration.Descriptor;
            writer.WriteStartObject();
            switch (descriptor.Kind)
            {
                case PackageStorageKeyMigrationDescriptorKind.Exact:
                    writer.WriteString("kind", "exact");
                    writer.WriteString("legacyKey", descriptor.LegacyPrefix);
                    writer.WriteString("destinationKey", descriptor.DestinationKey);
                    break;
                case PackageStorageKeyMigrationDescriptorKind.OpaqueId:
                case PackageStorageKeyMigrationDescriptorKind.OpaqueIdWithJsonIdentity:
                case PackageStorageKeyMigrationDescriptorKind.OpaqueIdWithCaseInsensitiveJsonIdentity:
                    writer.WriteString("kind", descriptor.Kind switch
                    {
                        PackageStorageKeyMigrationDescriptorKind.OpaqueId => "opaque-id",
                        PackageStorageKeyMigrationDescriptorKind.OpaqueIdWithJsonIdentity => "opaque-id-json",
                        _ => "opaque-id-json-case-insensitive",
                    });
                    writer.WriteString("legacyPrefix", descriptor.LegacyPrefix);
                    writer.WriteString("legacySuffix", descriptor.LegacySuffix);
                    writer.WriteString("destinationPrefix", descriptor.DestinationPrefix);
                    writer.WriteNumber("destinationVersion", descriptor.DestinationVersion);
                    if (descriptor.JsonIdentityProperty is not null)
                    {
                        writer.WriteString("identityProperty", descriptor.JsonIdentityProperty);
                    }
                    break;
                case PackageStorageKeyMigrationDescriptorKind.DynamicCleanup:
                    writer.WriteString("kind", "dynamic-cleanup");
                    break;
                default:
                    throw new InvalidOperationException(
                        "The worker storage migration kind is unsupported.");
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void BeginStartCandidate(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(root, "type");
        lock (Gate)
        {
            if (_phase != RuntimePhase.Ready)
            {
                throw new WorkerProtocolException("host.start-candidate is duplicate or out of order.");
            }
            _phase = RuntimePhase.CandidateStarting;
        }
        _candidateStartOperation = CompleteCandidateStartAsync();
    }

    private async Task CompleteCandidateStartAsync()
    {
        try
        {
            if (Configuration.OnCandidateStarted is not null)
            {
                await Configuration.OnCandidateStarted(_workStop.Token).ConfigureAwait(false);
            }

            lock (Gate)
            {
                ThrowIfFaultedUnderGate();
                if (_phase == RuntimePhase.Stopping) return;
                if (_phase != RuntimePhase.CandidateStarting)
                {
                    throw new WorkerProtocolException("Worker state changed while starting the candidate.");
                }
                _phase = RuntimePhase.CandidateStarted;
            }
            var body = WorkerJson.Serialize(_limits, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.candidate-started");
                writer.WriteEndObject();
            });
            await _output.WriteAsync(body).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_workStop.IsCancellationRequested && IsStopping())
        {
        }
        catch (Exception exception)
        {
            Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("The worker candidate-start callback failed.", exception));
        }
    }

    private void BeginCommitGeneration(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(root, "type", "activationId", "sessionGeneration");
        var activationId = WorkerJson.RequiredSafeId(root, "activationId", 64);
        var sessionGeneration = WorkerJson.RequiredNonNegativeInt64(root, "sessionGeneration");
        if (!string.Equals(activationId, _environment.ActivationId, StringComparison.Ordinal))
        {
            throw new WorkerProtocolException("host.commit-generation does not identify this worker activation.");
        }

        var invokeCallback = false;
        lock (Gate)
        {
            if (_phase == RuntimePhase.Committed && _sessionGeneration == sessionGeneration)
            {
                // Publication reconciliation may retry the exact commit.
                if (_generationCommitAcknowledgementPending) return;
                _generationCommitAcknowledgementPending = true;
            }
            else if (_phase == RuntimePhase.Committing && _sessionGeneration == sessionGeneration)
            {
                // The original callback will publish the acknowledgement for this exact retry.
                return;
            }
            else if (_phase == RuntimePhase.CandidateStarted && _sessionGeneration is null)
            {
                _sessionGeneration = sessionGeneration;
                _phase = RuntimePhase.Committing;
                invokeCallback = true;
            }
            else
            {
                throw new WorkerProtocolException("host.commit-generation is duplicate, conflicting, or out of order.");
            }
        }
        _generationCommitOperation = CompleteGenerationCommitAsync(
            activationId,
            sessionGeneration,
            invokeCallback);
    }

    private async Task CompleteGenerationCommitAsync(
        string activationId,
        long sessionGeneration,
        bool invokeCallback)
    {
        try
        {
            if (invokeCallback)
            {
                if (Configuration.OnGenerationCommitted is not null)
                {
                    await Configuration.OnGenerationCommitted(
                        new SunderWorkerGenerationContext(_environment.ActivationGuid, sessionGeneration),
                        _workStop.Token).ConfigureAwait(false);
                }

                lock (Gate)
                {
                    ThrowIfFaultedUnderGate();
                    if (_phase == RuntimePhase.Stopping) return;
                    if (_phase != RuntimePhase.Committing || _sessionGeneration != sessionGeneration)
                    {
                        throw new WorkerProtocolException("Worker state changed while committing the Runtime generation.");
                    }
                    _phase = RuntimePhase.Committed;
                    _generationCommitAcknowledgementPending = true;
                }
            }

            var body = WorkerJson.Serialize(_limits, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.generation-committed");
                writer.WriteString("activationId", activationId);
                writer.WriteNumber("sessionGeneration", sessionGeneration);
                writer.WriteEndObject();
            });
            await _output.WriteAsync(body).ConfigureAwait(false);
            lock (Gate)
            {
                if (_phase == RuntimePhase.Committed && _sessionGeneration == sessionGeneration)
                {
                    _generationCommitAcknowledgementPending = false;
                }
            }
        }
        catch (OperationCanceledException) when (_workStop.IsCancellationRequested && IsStopping())
        {
        }
        catch (Exception exception)
        {
            Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("The worker generation-commit callback failed.", exception));
        }
    }

    private void BeginStorageMigrationPreparation(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(root, "type", "activationId", "sessionGeneration");
        var activationId = WorkerJson.RequiredSafeId(root, "activationId", 64);
        var sessionGeneration = WorkerJson.RequiredNonNegativeInt64(root, "sessionGeneration");
        if (!string.Equals(activationId, _environment.ActivationId, StringComparison.Ordinal))
        {
            throw new WorkerProtocolException(
                "host.prepare-storage-migrations does not identify this worker activation.");
        }

        lock (Gate)
        {
            if (_phase != RuntimePhase.Committed || _sessionGeneration != sessionGeneration)
            {
                throw new WorkerProtocolException(
                    "host.prepare-storage-migrations is duplicate, conflicting, or out of order.");
            }
            _phase = RuntimePhase.PreparingStorageMigrations;
        }
        _migrationPreparationOperation = CompleteStorageMigrationPreparationAsync(
            activationId,
            sessionGeneration);
    }

    private async Task CompleteStorageMigrationPreparationAsync(
        string activationId,
        long sessionGeneration)
    {
        try
        {
            if (Configuration.OnBeforeStorageMigrations is not null)
            {
                var generation = new SunderWorkerGenerationContext(
                    _environment.ActivationGuid,
                    sessionGeneration);
                var context = new SunderWorkerMigrationPreparationContext(_client, generation);
                await Configuration.OnBeforeStorageMigrations(context, _workStop.Token)
                    .ConfigureAwait(false);
            }

            lock (Gate)
            {
                ThrowIfFaultedUnderGate();
                if (_phase == RuntimePhase.Stopping) return;
                if (_phase != RuntimePhase.PreparingStorageMigrations
                    || _sessionGeneration != sessionGeneration)
                {
                    throw new WorkerProtocolException(
                        "Worker state changed while preparing storage migrations.");
                }
                _phase = RuntimePhase.StorageMigrationsPrepared;
            }
            var body = WorkerJson.Serialize(_limits, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.storage-migrations-prepared");
                writer.WriteString("activationId", activationId);
                writer.WriteNumber("sessionGeneration", sessionGeneration);
                writer.WriteEndObject();
            });
            await _output.WriteAsync(body).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_workStop.IsCancellationRequested && IsStopping())
        {
        }
        catch (Exception exception)
        {
            Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException(
                    "The worker storage-migration preparation callback failed.",
                    exception));
        }
    }

    private ValueTask HandleActivateAsync(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(root, "type", "sessionGeneration", "workspaceRootPath");
        var sessionGeneration = WorkerJson.RequiredNonNegativeInt64(root, "sessionGeneration");
        var workspaceRootPath = WorkerJson.RequiredString(root, "workspaceRootPath", 4096);
        lock (Gate)
        {
            if (_phase != RuntimePhase.StorageMigrationsPrepared
                || _sessionGeneration != sessionGeneration)
            {
                throw new WorkerProtocolException("host.activate is duplicate or out of order.");
            }
            _client.RoleLocalWorkspace.Bind(workspaceRootPath, _environment);
            _phase = RuntimePhase.Activating;
        }
        _activationOperation = CompleteActivationAsync(sessionGeneration);
        return ValueTask.CompletedTask;
    }

    private async Task CompleteActivationAsync(long sessionGeneration)
    {
        try
        {
            if (Configuration.OnActivated is not null)
            {
                await Configuration.OnActivated(_client, _workStop.Token).ConfigureAwait(false);
            }

            lock (Gate)
            {
                ThrowIfFaultedUnderGate();
                if (_phase == RuntimePhase.Stopping) return;
                if (_phase != RuntimePhase.Activating || _sessionGeneration != sessionGeneration)
                {
                    throw new WorkerProtocolException("Worker state changed while activating the Runtime generation.");
                }
                _phase = RuntimePhase.Active;
            }
            if (Configuration.Composition?.Completion is { IsCompletedSuccessfully: false } completion)
            {
                _generationHealthOperation = MonitorCompositionAsync(completion);
            }

            var body = WorkerJson.Serialize(_limits, writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("type", "worker.activated");
                writer.WriteNumber("sessionGeneration", sessionGeneration);
                writer.WriteEndObject();
            });
            await _output.WriteAsync(body).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_workStop.IsCancellationRequested && IsStopping())
        {
        }
        catch (SunderRpcException exception) when (
            IsStopping()
            && exception.Error.Kind == SunderRpcErrorKind.Unavailable
            && exception.Error.Code == "rpc.worker.not-active")
        {
        }
        catch (Exception exception)
        {
            Fail(exception is WorkerProtocolException
                ? exception
                : new WorkerProtocolException("The worker activation callback failed.", exception));
        }
    }

    private async Task MonitorCompositionAsync(Task completion)
    {
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (IsStopping())
        {
        }
        catch (Exception exception)
        {
            try
            {
                if (Configuration.Composition is not null)
                {
                    await Configuration.Composition.ShutdownAsync(
                        new SunderWorkerShutdownContext("lifecycle-faulted"),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception cleanupException)
            {
                exception = new AggregateException(
                    "A package-owned lifecycle service and composition cleanup both failed.",
                    exception,
                    cleanupException);
            }
            Fail(new WorkerProtocolException(
                "A package-owned worker lifecycle service faulted.",
                exception));
        }
    }
}
