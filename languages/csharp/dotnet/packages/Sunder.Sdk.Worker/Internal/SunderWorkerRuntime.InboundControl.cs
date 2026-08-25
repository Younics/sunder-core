using System.Collections.ObjectModel;
using System.Text.Json;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Authentication;
using Sunder.Sdk.Callbacks;

namespace Sunder.Sdk.Worker.Internal;

internal sealed partial class SunderWorkerRuntime
{
    private readonly Dictionary<string, InboundContribution> _contributions = new(StringComparer.Ordinal);

    private void BeginContribution(string type, JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(
            root,
            "type",
            "id",
            "activationId",
            "sessionGeneration",
            "value");
        var id = WorkerJson.RequiredSafeId(root, "id");
        var activationId = WorkerJson.RequiredSafeId(root, "activationId", 64);
        var generation = WorkerJson.RequiredNonNegativeInt64(root, "sessionGeneration");
        var value = WorkerJson.RequiredValue(root, "value").Clone();
        InboundContribution contribution;
        lock (Gate)
        {
            ThrowIfFaultedUnderGate();
            if (_phase != RuntimePhase.Active
                || _sessionGeneration != generation
                || !string.Equals(activationId, _environment.ActivationId, StringComparison.Ordinal))
            {
                throw new WorkerProtocolException(
                    "Host contribution call does not identify this exact active generation.");
            }
            if (_invocations.ContainsKey(id)
                || _contributions.ContainsKey(id)
                || _rememberedHostIds.Contains(id))
            {
                throw new WorkerProtocolException(
                    $"Host reused contribution call id '{id}'.");
            }
            if (_invocations.Count + _contributions.Count >= _limits.MaximumInboundCalls)
            {
                throw new WorkerProtocolException(
                    "Host exceeded the worker inbound call limit.");
            }
            contribution = new InboundContribution(
                id,
                type,
                generation,
                value,
                CancellationTokenSource.CreateLinkedTokenSource(_workStop.Token));
            _contributions.Add(id, contribution);
        }
        contribution.Operation = ExecuteContributionAsync(contribution);
    }

    private async Task ExecuteContributionAsync(InboundContribution contribution)
    {
        try
        {
            var (responseType, value) = contribution.Type switch
            {
                "host.callback-start" => (
                    "worker.callback-started",
                    await StartCallbackAsync(contribution.Value, contribution.Cancellation.Token).ConfigureAwait(false)),
                "host.callback-complete" => (
                    "worker.callback-completed",
                    await CompleteCallbackAsync(contribution.Value, contribution.Cancellation.Token).ConfigureAwait(false)),
                "host.callback-cancel" => (
                    "worker.callback-cancelled",
                    await CancelCallbackAsync(contribution.Value, contribution.Cancellation.Token).ConfigureAwait(false)),
                "host.auth-status" => (
                    "worker.auth-status",
                    await GetAuthStatusAsync(contribution.Value, contribution.Cancellation.Token).ConfigureAwait(false)),
                "host.auth-connect" => (
                    "worker.auth-connected",
                    await StartAuthAsync(contribution.Value, contribution.Cancellation.Token).ConfigureAwait(false)),
                "host.auth-complete" => (
                    "worker.auth-completed",
                    await CompleteAuthAsync(contribution.Value, contribution.Cancellation.Token).ConfigureAwait(false)),
                "host.auth-disconnect" => (
                    "worker.auth-disconnected",
                    await DisconnectAuthAsync(contribution.Value, contribution.Cancellation.Token).ConfigureAwait(false)),
                _ => throw new WorkerProtocolException(
                    $"Host sent unsupported contribution call '{contribution.Type}'."),
            };
            await WriteContributionResultAsync(responseType, contribution, value).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteContributionErrorAsync(
                contribution,
                "worker.contribution-cancelled",
                "The worker contribution call was cancelled.").ConfigureAwait(false);
        }
        catch (WorkerProtocolException exception)
        {
            Fail(exception);
        }
        catch (Exception)
        {
            _diagnostics.Write("Worker contribution handler failed.");
            await WriteContributionErrorAsync(
                contribution,
                "worker.contribution-fault",
                "The worker contribution handler failed.").ConfigureAwait(false);
        }
        finally
        {
            lock (Gate)
            {
                _contributions.Remove(contribution.Id);
                RememberHostId(contribution.Id);
            }
            contribution.Cancellation.Dispose();
        }
    }

    private void HandleContributionCancellation(JsonElement root)
    {
        WorkerJson.RequireOnlyProperties(
            root,
            "type",
            "id",
            "activationId",
            "sessionGeneration");
        var id = WorkerJson.RequiredSafeId(root, "id");
        var activationId = WorkerJson.RequiredSafeId(root, "activationId", 64);
        var generation = WorkerJson.RequiredNonNegativeInt64(root, "sessionGeneration");
        InboundContribution? contribution;
        lock (Gate)
        {
            if (!_contributions.TryGetValue(id, out contribution))
            {
                if (_rememberedHostIds.Contains(id)) return;
                throw new WorkerProtocolException(
                    $"Host sent an unsolicited contribution cancellation for '{id}'.");
            }
            if (contribution.HostCancelled
                || contribution.SessionGeneration != generation
                || !string.Equals(activationId, _environment.ActivationId, StringComparison.Ordinal))
            {
                throw new WorkerProtocolException(
                    "Host sent a duplicate or mismatched contribution cancellation.");
            }
            contribution.HostCancelled = true;
        }
        CancelContribution(contribution);
    }

    private async Task<JsonElement> StartCallbackAsync(
        JsonElement value,
        CancellationToken cancellationToken)
    {
        WorkerJson.RequireOnlyProperties(
            value,
            "handlerId",
            "callbackSessionId",
            "callbackUri",
            "purpose",
            "parameters");
        var handler = GetCallbackHandler(WorkerJson.RequiredString(value, "handlerId", 128));
        var context = new PackageCallbackStartContext(
            WorkerJson.RequiredSafeId(value, "callbackSessionId"),
            ReadAbsoluteUri(value, "callbackUri"),
            WorkerJson.OptionalStringOrNull(value, "purpose", 128),
            ReadStringMap(value, "parameters", allowNullValues: false)
                .ToDictionary(static pair => pair.Key, static pair => pair.Value!, StringComparer.Ordinal));
        var result = await handler.StartCallbackAsync(context, cancellationToken).ConfigureAwait(false);
        return result is null
            ? JsonSerializer.SerializeToElement<object?>(null)
            : JsonSerializer.SerializeToElement(new
            {
                packageId = result.PackageId,
                callbackSessionId = result.CallbackSessionId,
                flow = result.Flow switch
                {
                    PackageCallbackFlowKind.Browser => "browser",
                    _ => throw new InvalidOperationException("The callback handler returned an unsupported flow."),
                },
                launchUrl = result.LaunchUrl,
                message = BoundResultText(result.Message, 2048),
            });
    }

    private async Task<JsonElement> CompleteCallbackAsync(
        JsonElement value,
        CancellationToken cancellationToken)
    {
        WorkerJson.RequireOnlyProperties(value, "handlerId", "callbackSessionId", "queryValues");
        var handler = GetCallbackHandler(WorkerJson.RequiredString(value, "handlerId", 128));
        var sessionId = WorkerJson.RequiredSafeId(value, "callbackSessionId");
        var result = await handler.CompleteCallbackAsync(
            new PackageCallbackCompletionContext(
                sessionId,
                ReadStringMap(value, "queryValues", allowNullValues: true)),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            packageId = result.PackageId,
            callbackSessionId = result.CallbackSessionId,
            state = result.State switch
            {
                PackageCallbackCompletionState.Completed => "completed",
                PackageCallbackCompletionState.Failed => "failed",
                PackageCallbackCompletionState.Cancelled => "cancelled",
                _ => throw new InvalidOperationException(
                    "The callback handler returned an unsupported completion state."),
            },
            message = BoundResultText(result.Message, 2048),
        });
    }

    private async Task<JsonElement> CancelCallbackAsync(
        JsonElement value,
        CancellationToken cancellationToken)
    {
        WorkerJson.RequireOnlyProperties(value, "handlerId", "callbackSessionId", "reason");
        var handler = GetCallbackHandler(WorkerJson.RequiredString(value, "handlerId", 128));
        var reason = WorkerJson.RequiredString(value, "reason", 32) switch
        {
            "expired" => PackageCallbackCancellationReason.Expired,
            "package-unloaded" => PackageCallbackCancellationReason.PackageUnloaded,
            "host-shutdown" => PackageCallbackCancellationReason.HostShutdown,
            "caller-requested" => PackageCallbackCancellationReason.CallerRequested,
            _ => throw new WorkerProtocolException("Host callback cancellation reason is unsupported."),
        };
        await handler.CancelCallbackAsync(
            new PackageCallbackCancellationContext(
                WorkerJson.RequiredSafeId(value, "callbackSessionId"),
                reason),
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement<object?>(null);
    }

    private async Task<JsonElement> GetAuthStatusAsync(
        JsonElement value,
        CancellationToken cancellationToken)
    {
        WorkerJson.RequireOnlyProperties(value);
        return WriteAuthStatus(await GetAuthHandler().GetStatusAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task<JsonElement> StartAuthAsync(
        JsonElement value,
        CancellationToken cancellationToken)
    {
        WorkerJson.RequireOnlyProperties(value, "authSessionId", "callbackUri");
        var result = await GetAuthHandler().StartAuthorizationAsync(
            new PackageAuthSessionStartContext(
                WorkerJson.RequiredSafeId(value, "authSessionId"),
                ReadAbsoluteUri(value, "callbackUri")),
            cancellationToken).ConfigureAwait(false);
        return result is null
            ? JsonSerializer.SerializeToElement<object?>(null)
            : JsonSerializer.SerializeToElement(new
            {
                packageId = result.PackageId,
                callbackSessionId = result.AuthSessionId,
                flow = result.Flow switch
                {
                    PackageAuthFlowKind.Browser => "browser",
                    _ => throw new InvalidOperationException("The auth handler returned an unsupported flow."),
                },
                launchUrl = result.LaunchUrl,
                message = BoundResultText(result.Message, 2048),
            });
    }

    private async Task<JsonElement> CompleteAuthAsync(
        JsonElement value,
        CancellationToken cancellationToken)
    {
        WorkerJson.RequireOnlyProperties(value, "authSessionId", "queryValues");
        var status = await GetAuthHandler().CompleteAuthorizationAsync(
            new PackageAuthSessionCompletionContext(
                WorkerJson.RequiredSafeId(value, "authSessionId"),
                ReadStringMap(value, "queryValues", allowNullValues: true)),
            cancellationToken).ConfigureAwait(false);
        return WriteAuthStatus(status);
    }

    private async Task<JsonElement> DisconnectAuthAsync(
        JsonElement value,
        CancellationToken cancellationToken)
    {
        WorkerJson.RequireOnlyProperties(value);
        return WriteAuthStatus(await GetAuthHandler().DisconnectAsync(cancellationToken).ConfigureAwait(false));
    }

    private IPackageCallbackHandler GetCallbackHandler(string handlerId)
    {
        if (string.Equals(
                handlerId,
                PackageCallbackHandlerIds.Authentication,
                StringComparison.OrdinalIgnoreCase))
        {
            return GetAuthHandler();
        }
        var handler = Configuration.CallbackHandlers.FirstOrDefault(candidate =>
            string.Equals(candidate.CallbackHandlerId, handlerId, StringComparison.Ordinal));
        return handler ?? throw new WorkerProtocolException(
            $"Host requested undeclared callback handler '{handlerId}'.");
    }

    private IPackageAuthHandler GetAuthHandler()
        => Configuration.AuthHandler
           ?? throw new WorkerProtocolException("Host requested an undeclared auth handler.");

    private static JsonElement WriteAuthStatus(PackageAuthStatus status)
        => JsonSerializer.SerializeToElement(new
        {
            packageId = status.PackageId,
            status = status.Status switch
            {
                PackageAuthStatusKind.Unavailable => "unavailable",
                PackageAuthStatusKind.NotConnected => "not-connected",
                PackageAuthStatusKind.Connected => "connected",
                PackageAuthStatusKind.Failed => "failed",
                _ => throw new InvalidOperationException("The auth handler returned an unsupported status."),
            },
            message = BoundResultText(status.Message, 2048),
            canAuthorize = status.CanAuthorize,
            canDisconnect = status.CanDisconnect,
        });

    private static Uri ReadAbsoluteUri(JsonElement value, string propertyName)
    {
        var text = WorkerJson.RequiredString(value, propertyName, 4096);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw new WorkerProtocolException(
                $"Host contribution property '{propertyName}' must be an absolute URI.");
        }
        return uri;
    }

    private static IReadOnlyDictionary<string, string?> ReadStringMap(
        JsonElement value,
        string propertyName,
        bool allowNullValues)
    {
        var map = WorkerJson.RequiredValue(value, propertyName);
        WorkerJson.RequireObject(map, $"Host contribution property '{propertyName}'");
        if (map.GetPropertyCount() > PackageCallbackParameters.MaximumCount)
        {
            throw new WorkerProtocolException(
                $"Host contribution property '{propertyName}' has too many entries.");
        }
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var totalLength = 0;
        foreach (var property in map.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name)
                || property.Name.Length > PackageCallbackParameters.MaximumKeyLength
                || property.Value.ValueKind != JsonValueKind.String
                && (!allowNullValues || property.Value.ValueKind != JsonValueKind.Null))
            {
                throw new WorkerProtocolException(
                    $"Host contribution property '{propertyName}' contains an invalid entry.");
            }
            var item = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString()!;
            if (item?.Length > PackageCallbackParameters.MaximumValueLength)
            {
                throw new WorkerProtocolException(
                    $"Host contribution property '{propertyName}' contains an oversized value.");
            }
            totalLength = checked(totalLength + property.Name.Length + (item?.Length ?? 0));
            if (totalLength > PackageCallbackParameters.MaximumTotalLength)
            {
                throw new WorkerProtocolException(
                    $"Host contribution property '{propertyName}' exceeds its total size bound.");
            }
            result.Add(property.Name, item);
        }
        return new ReadOnlyDictionary<string, string?>(result);
    }

    private async Task WriteContributionResultAsync(
        string type,
        InboundContribution contribution,
        JsonElement value)
    {
        var body = WorkerJson.Serialize(_limits, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("id", contribution.Id);
            writer.WriteString("activationId", _environment.ActivationId);
            writer.WriteNumber("sessionGeneration", contribution.SessionGeneration);
            writer.WritePropertyName("value");
            WorkerJson.WriteJsonValue(writer, value);
            writer.WriteEndObject();
        });
        await _output.WriteAsync(body).ConfigureAwait(false);
    }

    private async Task WriteContributionErrorAsync(
        InboundContribution contribution,
        string code,
        string message)
    {
        var body = WorkerJson.Serialize(_limits, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("type", "worker.contribution-error");
            writer.WriteString("id", contribution.Id);
            writer.WriteString("activationId", _environment.ActivationId);
            writer.WriteNumber("sessionGeneration", contribution.SessionGeneration);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        await _output.WriteAsync(body).ConfigureAwait(false);
    }

    private static string BoundResultText(string? value, int maximumLength)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : WorkerDiagnosticSink.Sanitize(value, maximumLength);

    private void CancelContribution(InboundContribution contribution)
    {
        try
        {
            contribution.Cancellation.Cancel();
        }
        catch (AggregateException exception)
        {
            _diagnostics.Write("Contribution cancellation callback failed", exception);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private InboundContribution[] GetContributionsUnderGate()
        => _contributions.Values.ToArray();

    private void CancelContributions(IEnumerable<InboundContribution> contributions)
    {
        foreach (var contribution in contributions) CancelContribution(contribution);
    }

    private sealed class InboundContribution(
        string id,
        string type,
        long sessionGeneration,
        JsonElement value,
        CancellationTokenSource cancellation)
    {
        public string Id { get; } = id;
        public string Type { get; } = type;
        public long SessionGeneration { get; } = sessionGeneration;
        public JsonElement Value { get; } = value;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Operation { get; set; } = Task.CompletedTask;
        public bool HostCancelled { get; set; }
    }
}
