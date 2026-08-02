using Sunder.Host.Contracts;

namespace Sunder.Cli;

internal sealed class RuntimeStatusCommandHandler(
    ICliHostStatusClient host,
    ICliRuntimeSystemClient runtime,
    CliOutput output)
{
    public async Task<int> ExecuteAsync(RuntimeStatusCommand command, CancellationToken token)
    {
        var hostStatus = await host.GetStatusAsync(token).ConfigureAwait(false);
        var runtimeStatus = hostStatus.State == HostRuntimeState.Ready
            ? await runtime.GetSystemStatusAsync(token).ConfigureAwait(false)
            : null;
        output.Data(new
        {
            desiredState = Token(hostStatus.DesiredState),
            state = Token(hostStatus.State),
            deploymentGeneration = hostStatus.DeploymentGeneration,
            runtimeInstanceId = hostStatus.RuntimeInstanceId,
            activeOperationId = hostStatus.ActiveOperationId,
            failureCode = hostStatus.FailureCode,
            failureMessage = hostStatus.FailureMessage,
            runtime = runtimeStatus is null ? null : new
            {
                name = runtimeStatus.Name,
                version = runtimeStatus.Version,
                ready = runtimeStatus.IsReady,
                startedAtUtc = runtimeStatus.StartedAtUtc,
            },
        });
        output.Line($"Runtime state: {Token(hostStatus.State)}");
        output.Line($"Desired state: {Token(hostStatus.DesiredState)}");
        output.Line($"Deployment generation: {hostStatus.DeploymentGeneration}");
        if (runtimeStatus is not null)
        {
            output.Line($"Runtime: {runtimeStatus.Name} {runtimeStatus.Version}");
            output.Line($"Ready: {(runtimeStatus.IsReady ? "yes" : "no")}");
            output.Line($"Started (UTC): {runtimeStatus.StartedAtUtc.UtcDateTime:O}");
        }
        if (!string.IsNullOrWhiteSpace(hostStatus.FailureMessage))
            output.Error(hostStatus.FailureMessage, hostStatus.FailureCode ?? "host.runtime.failed");
        else if (runtimeStatus is { IsReady: false })
            output.Error("Runtime is not ready.", "runtime.not_ready");

        return hostStatus.State is HostRuntimeState.Failed or HostRuntimeState.CrashLoop
               || runtimeStatus is { IsReady: false }
            ? CliExitCodes.Unavailable
            : CliExitCodes.Success;
    }

    private static string Token<T>(T value) where T : struct, Enum
        => value.ToString().ToLowerInvariant();
}
