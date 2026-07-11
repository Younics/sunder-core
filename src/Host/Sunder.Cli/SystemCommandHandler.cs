namespace Sunder.Cli;

internal sealed class SystemCommandHandler(ICliRuntimeClient runtime, CliOutput output)
{
    public async Task<int> ExecuteAsync(SystemStatusCommand command, CancellationToken token)
    {
        var status = await runtime.GetSystemStatusAsync(token).ConfigureAwait(false);
        output.Data(status);
        output.Line($"Runtime: {status.Name} {status.Version}");
        output.Line($"Ready: {(status.IsReady ? "yes" : "no")}");
        output.Line($"Started (UTC): {status.StartedAtUtc.UtcDateTime:O}");
        return status.IsReady ? CliExitCodes.Success : CliExitCodes.Unavailable;
    }
}
