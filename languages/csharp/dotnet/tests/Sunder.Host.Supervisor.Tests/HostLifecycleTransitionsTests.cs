using Sunder.Host.Contracts;
using Sunder.Host.Supervisor;
using Xunit;

namespace Sunder.Host.Supervisor.Tests;

public sealed class HostLifecycleTransitionsTests
{
    private static readonly DateTimeOffset AcceptedAt =
        new(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(HostOperationKinds.RuntimeStart, HostRuntimeDesiredState.Running)]
    [InlineData(HostOperationKinds.RuntimeStop, HostRuntimeDesiredState.Stopped)]
    [InlineData(HostOperationKinds.RuntimeRestart, HostRuntimeDesiredState.Running)]
    public void CalculateSubmission_MapsEveryOperationKindAndAdvancesGenerationOnce(
        string operationKind,
        HostRuntimeDesiredState expectedDesiredState)
    {
        var initial = new HostLifecyclePersistentState(
            HostRuntimeDesiredState.Stopped,
            7,
            ActiveOperationId: null,
            Operations: []);
        var request = new HostLifecycleRequest(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            7);

        Assert.True(HostLifecycleTransitions.TryGetDesiredState(operationKind, out var desiredState));
        Assert.Equal(expectedDesiredState, desiredState);

        var accepted = Assert.IsType<HostLifecycleAcceptedSubmissionResult>(
            HostLifecycleTransitions.CalculateSubmission(
                initial,
                request,
                operationKind,
                "22222222222222222222222222222222",
                AcceptedAt));

        Assert.Equal(expectedDesiredState, accepted.State.DesiredState);
        Assert.Equal(8, accepted.State.DeploymentGeneration);
        Assert.Equal(accepted.Operation.OperationId, accepted.State.ActiveOperationId);
        Assert.Equal(operationKind, accepted.Operation.Kind);
        Assert.Equal(request.MutationId, accepted.Operation.MutationId);
        Assert.Equal(7, accepted.Operation.ExpectedDeploymentGeneration);
        Assert.Equal(HostOperationState.Accepted, accepted.Operation.State);
        Assert.Equal(AcceptedAt, accepted.Operation.CreatedAtUtc);
        Assert.Equal(AcceptedAt, accepted.Operation.UpdatedAtUtc);
        Assert.Null(accepted.Operation.Message);
        Assert.Null(accepted.Operation.FailureCode);
        Assert.Equal(HostRuntimeDesiredState.Stopped, initial.DesiredState);
        Assert.Equal(7, initial.DeploymentGeneration);
        Assert.Null(initial.ActiveOperationId);
        Assert.Empty(initial.Operations);
    }

    [Fact]
    public void OperationKindMapping_RejectsUnknownKind()
    {
        Assert.False(HostLifecycleTransitions.TryGetDesiredState("runtime.unknown", out _));
        Assert.False(HostLifecycleTransitions.TryGetDesiredState(null, out _));
    }

    [Fact]
    public void CalculateSubmission_PreservesRetryAndConflictPrecedence()
    {
        var mutationId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var active = new HostOperationDescriptor(
            "44444444444444444444444444444444",
            mutationId,
            HostOperationKinds.RuntimeStart,
            4,
            HostOperationState.Running,
            AcceptedAt,
            AcceptedAt.AddSeconds(1),
            null,
            null);
        var state = new HostLifecyclePersistentState(
            HostRuntimeDesiredState.Running,
            5,
            active.OperationId,
            [active]);

        var retry = Assert.IsType<HostLifecycleRetrySubmissionResult>(
            HostLifecycleTransitions.CalculateSubmission(
                state,
                new HostLifecycleRequest(mutationId, 4),
                HostOperationKinds.RuntimeStart,
                "55555555555555555555555555555555",
                AcceptedAt.AddMinutes(1)));
        Assert.Same(active, retry.Operation);

        var mutationReuse = Assert.IsType<HostLifecycleSubmissionConflictResult>(
            HostLifecycleTransitions.CalculateSubmission(
                state,
                new HostLifecycleRequest(mutationId, 99),
                HostOperationKinds.RuntimeStart,
                "66666666666666666666666666666666",
                AcceptedAt.AddMinutes(1)));
        Assert.Equal("host.mutation-reuse", mutationReuse.Code);
        Assert.Equal(
            $"Mutation '{mutationId:D}' was already used for a different lifecycle request.",
            mutationReuse.Message);
        Assert.Null(mutationReuse.ActiveOperationId);

        var kindReuse = Assert.IsType<HostLifecycleSubmissionConflictResult>(
            HostLifecycleTransitions.CalculateSubmission(
                state,
                new HostLifecycleRequest(mutationId, 4),
                HostOperationKinds.RuntimeStop,
                "67676767676767676767676767676767",
                AcceptedAt.AddMinutes(1)));
        Assert.Equal("host.mutation-reuse", kindReuse.Code);
        Assert.Null(kindReuse.ActiveOperationId);

        var staleMutationId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var staleGeneration = Assert.IsType<HostLifecycleSubmissionConflictResult>(
            HostLifecycleTransitions.CalculateSubmission(
                state,
                new HostLifecycleRequest(staleMutationId, 4),
                HostOperationKinds.RuntimeStop,
                "88888888888888888888888888888888",
                AcceptedAt.AddMinutes(1)));
        Assert.Equal("host.deployment-generation-conflict", staleGeneration.Code);
        Assert.Equal(
            "Expected deployment generation 4, but the current generation is 5.",
            staleGeneration.Message);
        Assert.Null(staleGeneration.ActiveOperationId);

        var activeConflict = Assert.IsType<HostLifecycleSubmissionConflictResult>(
            HostLifecycleTransitions.CalculateSubmission(
                state,
                new HostLifecycleRequest(Guid.Parse("99999999-9999-9999-9999-999999999999"), 5),
                HostOperationKinds.RuntimeStop,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                AcceptedAt.AddMinutes(1)));
        Assert.Equal("host.operation-active", activeConflict.Code);
        Assert.Equal(
            $"Lifecycle operation '{active.OperationId}' is already active.",
            activeConflict.Message);
        Assert.Equal(active.OperationId, activeConflict.ActiveOperationId);
    }

    [Fact]
    public void MarkAcceptedOperationRunning_ChangesOnlyOperationProgress()
    {
        var accepted = AcceptStart(deploymentGeneration: 3);
        var runningAt = AcceptedAt.AddSeconds(1);

        var running = HostLifecycleTransitions.MarkAcceptedOperationRunning(
            accepted.State,
            accepted.Operation.OperationId,
            runningAt);

        Assert.Equal(HostOperationState.Accepted, accepted.Operation.State);
        Assert.Equal(HostOperationState.Running, running.Operation.State);
        Assert.Equal(AcceptedAt, running.Operation.CreatedAtUtc);
        Assert.Equal(runningAt, running.Operation.UpdatedAtUtc);
        Assert.Equal(HostRuntimeDesiredState.Running, running.State.DesiredState);
        Assert.Equal(4, running.State.DeploymentGeneration);
        Assert.Equal(running.Operation.OperationId, running.State.ActiveOperationId);
        Assert.Same(running.Operation, Assert.Single(running.State.Operations));
    }

    [Fact]
    public void CompleteOperation_FailedStartPreservesRunningIntentAndAcceptedGeneration()
    {
        var accepted = AcceptStart(deploymentGeneration: 3);
        var running = HostLifecycleTransitions.MarkAcceptedOperationRunning(
            accepted.State,
            accepted.Operation.OperationId,
            AcceptedAt.AddSeconds(1));

        var failed = HostLifecycleTransitions.CompleteOperation(
            running.State,
            running.Operation.OperationId,
            HostOperationState.Failed,
            AcceptedAt.AddSeconds(2),
            "Runtime worker failed to start.",
            "host.worker-start-failed");

        Assert.True(failed.Applied);
        var failedOperation = Assert.IsType<HostOperationDescriptor>(failed.Operation);
        Assert.Equal(HostOperationState.Failed, failedOperation.State);
        Assert.Equal("Runtime worker failed to start.", failedOperation.Message);
        Assert.Equal("host.worker-start-failed", failedOperation.FailureCode);
        Assert.Equal(HostRuntimeDesiredState.Running, failed.State.DesiredState);
        Assert.Equal(4, failed.State.DeploymentGeneration);
        Assert.Null(failed.State.ActiveOperationId);

        var recovered = HostLifecycleTransitions.PromoteReady(
            failed.State,
            completingOperationId: null,
            AcceptedAt.AddSeconds(3));
        Assert.True(recovered.DeploymentGenerationAdvanced);
        Assert.Equal(5, recovered.State.DeploymentGeneration);
        Assert.Equal(HostRuntimeDesiredState.Running, recovered.State.DesiredState);
        Assert.Same(failedOperation, Assert.Single(recovered.State.Operations));

        var duplicate = HostLifecycleTransitions.CompleteOperation(
            failed.State,
            running.Operation.OperationId,
            HostOperationState.Succeeded,
            AcceptedAt.AddSeconds(4),
            "ignored",
            failureCode: null);
        Assert.False(duplicate.Applied);
        Assert.Same(failed.State, duplicate.State);
        Assert.Null(duplicate.Operation);
    }

    [Fact]
    public void PromoteReady_ForClientOperationDoesNotAdvanceGenerationTwice()
    {
        var accepted = AcceptStart(deploymentGeneration: 3);
        var running = HostLifecycleTransitions.MarkAcceptedOperationRunning(
            accepted.State,
            accepted.Operation.OperationId,
            AcceptedAt.AddSeconds(1));

        var ready = HostLifecycleTransitions.PromoteReady(
            running.State,
            running.Operation.OperationId,
            AcceptedAt.AddSeconds(2));

        Assert.False(ready.DeploymentGenerationAdvanced);
        Assert.Equal(4, ready.State.DeploymentGeneration);
        Assert.Equal(HostRuntimeDesiredState.Running, ready.State.DesiredState);
        Assert.Null(ready.State.ActiveOperationId);
        var completedOperation = Assert.IsType<HostOperationDescriptor>(ready.CompletedOperation);
        Assert.Equal(HostOperationState.Succeeded, completedOperation.State);
        Assert.Equal("Runtime worker is ready.", completedOperation.Message);
        Assert.Null(completedOperation.FailureCode);
        Assert.Equal(HostOperationState.Running, running.Operation.State);
    }

    [Fact]
    public void PromoteReady_ForRecoveryAdvancesGenerationOnce()
    {
        var state = new HostLifecyclePersistentState(
            HostRuntimeDesiredState.Running,
            4,
            ActiveOperationId: null,
            Operations: []);

        var ready = HostLifecycleTransitions.PromoteReady(
            state,
            completingOperationId: null,
            AcceptedAt);

        Assert.True(ready.DeploymentGenerationAdvanced);
        Assert.Equal(5, ready.State.DeploymentGeneration);
        Assert.Equal(HostRuntimeDesiredState.Running, ready.State.DesiredState);
        Assert.Null(ready.State.ActiveOperationId);
        Assert.Empty(ready.State.Operations);
        Assert.Null(ready.CompletedOperation);
        Assert.Equal(4, state.DeploymentGeneration);
    }

    private static HostLifecycleAcceptedSubmissionResult AcceptStart(long deploymentGeneration)
        => Assert.IsType<HostLifecycleAcceptedSubmissionResult>(
            HostLifecycleTransitions.CalculateSubmission(
                new HostLifecyclePersistentState(
                    HostRuntimeDesiredState.Stopped,
                    deploymentGeneration,
                    ActiveOperationId: null,
                    Operations: []),
                new HostLifecycleRequest(
                    Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    deploymentGeneration),
                HostOperationKinds.RuntimeStart,
                "cccccccccccccccccccccccccccccccc",
                AcceptedAt));
}
