using Sunder.Host.Contracts;

namespace Sunder.Host.Supervisor;

internal abstract record HostLifecycleSubmissionTransitionResult;

internal sealed record HostLifecycleAcceptedSubmissionResult(
    HostLifecyclePersistentState State,
    HostOperationDescriptor Operation) : HostLifecycleSubmissionTransitionResult;

internal sealed record HostLifecycleRetrySubmissionResult(
    HostOperationDescriptor Operation) : HostLifecycleSubmissionTransitionResult;

internal sealed record HostLifecycleSubmissionConflictResult(
    string Code,
    string Message,
    string? ActiveOperationId) : HostLifecycleSubmissionTransitionResult;

internal sealed record HostLifecycleRunningTransitionResult(
    HostLifecyclePersistentState State,
    HostOperationDescriptor Operation);

internal sealed record HostLifecycleCompletionTransitionResult(
    HostLifecyclePersistentState State,
    HostOperationDescriptor? Operation,
    bool Applied);

internal sealed record HostLifecycleReadyPromotionResult(
    HostLifecyclePersistentState State,
    HostOperationDescriptor? CompletedOperation,
    bool DeploymentGenerationAdvanced);

internal static class HostLifecycleTransitions
{
    private const int MaximumOperationMessageLength = 2048;

    public static bool TryGetDesiredState(
        string? operationKind,
        out HostRuntimeDesiredState desiredState)
    {
        switch (operationKind)
        {
            case HostOperationKinds.RuntimeStart:
            case HostOperationKinds.RuntimeRestart:
                desiredState = HostRuntimeDesiredState.Running;
                return true;
            case HostOperationKinds.RuntimeStop:
                desiredState = HostRuntimeDesiredState.Stopped;
                return true;
            default:
                desiredState = default;
                return false;
        }
    }

    public static HostLifecycleSubmissionTransitionResult CalculateSubmission(
        HostLifecyclePersistentState state,
        HostLifecycleRequest request,
        string operationKind,
        string operationId,
        DateTimeOffset acceptedAtUtc)
    {
        var existing = state.Operations.FirstOrDefault(operation =>
            operation.MutationId == request.MutationId);
        if (existing is not null)
        {
            if (!string.Equals(existing.Kind, operationKind, StringComparison.Ordinal)
                || existing.ExpectedDeploymentGeneration != request.ExpectedDeploymentGeneration)
            {
                return new HostLifecycleSubmissionConflictResult(
                    "host.mutation-reuse",
                    $"Mutation '{request.MutationId:D}' was already used for a different lifecycle request.",
                    ActiveOperationId: null);
            }

            return new HostLifecycleRetrySubmissionResult(existing);
        }

        if (request.ExpectedDeploymentGeneration != state.DeploymentGeneration)
        {
            return new HostLifecycleSubmissionConflictResult(
                "host.deployment-generation-conflict",
                $"Expected deployment generation {request.ExpectedDeploymentGeneration}, but the current generation is {state.DeploymentGeneration}.",
                ActiveOperationId: null);
        }

        if (state.ActiveOperationId is not null)
        {
            return new HostLifecycleSubmissionConflictResult(
                "host.operation-active",
                $"Lifecycle operation '{state.ActiveOperationId}' is already active.",
                state.ActiveOperationId);
        }

        if (!TryGetDesiredState(operationKind, out var desiredState))
        {
            throw new InvalidOperationException($"Unsupported Host lifecycle operation '{operationKind}'.");
        }

        var operation = new HostOperationDescriptor(
            operationId,
            request.MutationId,
            operationKind,
            request.ExpectedDeploymentGeneration,
            HostOperationState.Accepted,
            acceptedAtUtc,
            acceptedAtUtc,
            null,
            null);
        var updated = state with
        {
            DesiredState = desiredState,
            DeploymentGeneration = state.DeploymentGeneration + 1,
            ActiveOperationId = operation.OperationId,
            Operations = Array.AsReadOnly<HostOperationDescriptor>([.. state.Operations, operation]),
        };
        return new HostLifecycleAcceptedSubmissionResult(updated, operation);
    }

    public static HostLifecycleRunningTransitionResult MarkAcceptedOperationRunning(
        HostLifecyclePersistentState state,
        string operationId,
        DateTimeOffset updatedAtUtc)
    {
        var operation = GetRequiredOperation(state, operationId);
        if (operation.State != HostOperationState.Accepted)
        {
            throw new InvalidOperationException(
                $"Host lifecycle operation '{operationId}' is not accepted.");
        }

        var running = operation with
        {
            State = HostOperationState.Running,
            UpdatedAtUtc = updatedAtUtc,
        };
        return new HostLifecycleRunningTransitionResult(
            ReplaceOperation(state, running, state.ActiveOperationId),
            running);
    }

    public static HostLifecycleCompletionTransitionResult CompleteOperation(
        HostLifecyclePersistentState state,
        string operationId,
        HostOperationState completionState,
        DateTimeOffset updatedAtUtc,
        string? message,
        string? failureCode)
    {
        if (!string.Equals(state.ActiveOperationId, operationId, StringComparison.Ordinal))
        {
            return new HostLifecycleCompletionTransitionResult(state, Operation: null, Applied: false);
        }
        if (completionState is not (HostOperationState.Succeeded or HostOperationState.Failed))
        {
            throw new ArgumentOutOfRangeException(
                nameof(completionState),
                completionState,
                "A Host lifecycle operation can only complete as succeeded or failed.");
        }

        var operation = GetRequiredOperation(state, operationId);
        var completed = operation with
        {
            State = completionState,
            UpdatedAtUtc = updatedAtUtc,
            Message = Truncate(message),
            FailureCode = Truncate(failureCode),
        };
        return new HostLifecycleCompletionTransitionResult(
            ReplaceOperation(state, completed, activeOperationId: null),
            completed,
            Applied: true);
    }

    public static HostLifecycleReadyPromotionResult PromoteReady(
        HostLifecyclePersistentState state,
        string? completingOperationId,
        DateTimeOffset updatedAtUtc)
    {
        if (completingOperationId is null)
        {
            return new HostLifecycleReadyPromotionResult(
                state with
                {
                    DeploymentGeneration = state.DeploymentGeneration + 1,
                },
                CompletedOperation: null,
                DeploymentGenerationAdvanced: true);
        }

        var completion = CompleteOperation(
            state,
            completingOperationId,
            HostOperationState.Succeeded,
            updatedAtUtc,
            "Runtime worker is ready.",
            failureCode: null);
        return new HostLifecycleReadyPromotionResult(
            completion.State,
            completion.Operation,
            DeploymentGenerationAdvanced: false);
    }

    private static HostOperationDescriptor GetRequiredOperation(
        HostLifecyclePersistentState state,
        string operationId)
        => state.Operations.FirstOrDefault(operation =>
               string.Equals(operation.OperationId, operationId, StringComparison.Ordinal))
           ?? throw new KeyNotFoundException($"Host lifecycle operation '{operationId}' was not found.");

    private static HostLifecyclePersistentState ReplaceOperation(
        HostLifecyclePersistentState state,
        HostOperationDescriptor replacement,
        string? activeOperationId)
    {
        var replaced = false;
        var operations = state.Operations.Select(operation =>
        {
            if (!string.Equals(operation.OperationId, replacement.OperationId, StringComparison.Ordinal))
            {
                return operation;
            }

            replaced = true;
            return replacement;
        }).ToArray();
        if (!replaced)
        {
            throw new KeyNotFoundException(
                $"Host lifecycle operation '{replacement.OperationId}' was not found.");
        }

        return state with
        {
            ActiveOperationId = activeOperationId,
            Operations = Array.AsReadOnly(operations),
        };
    }

    private static string? Truncate(string? value)
        => value is null || value.Length <= MaximumOperationMessageLength
            ? value
            : value[..MaximumOperationMessageLength];
}
