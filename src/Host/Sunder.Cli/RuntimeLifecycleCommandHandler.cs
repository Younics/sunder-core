using Sunder.Host.Contracts;

namespace Sunder.Cli;

internal sealed class RuntimeLifecycleCommandHandler(
    ICliHostLifecycleClient host,
    CliOutput output,
    ICliProgress progress)
{
    public async Task<int> ExecuteAsync(RuntimeLifecycleCommand command, CancellationToken token)
    {
        var before = await host.GetStatusAsync(token).ConfigureAwait(false);
        var request = new HostLifecycleRequest(Guid.NewGuid(), before.DeploymentGeneration);
        progress.Report($"Submitting Runtime {Action(command.Action)} operation...");
        var submission = command.Action switch
        {
            RuntimeLifecycleAction.Start => await host.SubmitStartRuntimeAsync(request, token).ConfigureAwait(false),
            RuntimeLifecycleAction.Stop => await host.SubmitStopRuntimeAsync(request, token).ConfigureAwait(false),
            RuntimeLifecycleAction.Restart => await host.SubmitRestartRuntimeAsync(request, token).ConfigureAwait(false),
            _ => throw new InvalidOperationException("The Runtime lifecycle action is invalid."),
        };
        var operation = await host.WaitForOperationAsync(submission.Operation, token).ConfigureAwait(false);
        var status = await host.GetStatusAsync(token).ConfigureAwait(false);

        output.Data(new
        {
            operation = new
            {
                operationId = operation.OperationId,
                mutationId = operation.MutationId,
                kind = operation.Kind,
                state = operation.State.ToString().ToLowerInvariant(),
                createdAtUtc = operation.CreatedAtUtc,
                updatedAtUtc = operation.UpdatedAtUtc,
                message = operation.Message,
                failureCode = operation.FailureCode,
            },
            runtime = new
            {
                desiredState = status.DesiredState.ToString().ToLowerInvariant(),
                state = status.State.ToString().ToLowerInvariant(),
                deploymentGeneration = status.DeploymentGeneration,
                runtimeInstanceId = status.RuntimeInstanceId,
            },
        });

        if (operation.State != HostOperationState.Succeeded)
        {
            output.Error(
                operation.Message ?? $"Runtime {Action(command.Action)} failed.",
                operation.FailureCode ?? "host.runtime.lifecycle_failed");
            return CliExitCodes.Failure;
        }

        output.Success(operation.Message ?? $"Runtime {Action(command.Action)} completed.");
        output.Info($"Runtime state: {status.State.ToString().ToLowerInvariant()}.");
        return CliExitCodes.Success;
    }

    private static string Action(RuntimeLifecycleAction action) => action.ToString().ToLowerInvariant();
}
