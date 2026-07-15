using Sunder.Runtime.Client;
using Sunder.Runtime.LocalState;

namespace Sunder.Cli;

internal sealed class RuntimeResetCommandHandler(ICliRuntimeResetClient runtime, CliOutput output)
{
    public async Task<int> ExecuteAsync(RuntimeResetCommand command, CancellationToken token)
    {
        if (!command.Yes)
        {
            throw new CliUsageException("Runtime reset requires --yes.");
        }

        try
        {
            var challenge = await runtime.PrepareResetAsync(token).ConfigureAwait(false);
            if (challenge.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("The Runtime reset confirmation challenge expired before it could be used.");
            }

            await runtime.DrainForResetAsync(challenge.Challenge, token).ConfigureAwait(false);
            output.Info("Runtime accepted the one-time reset challenge and is draining.");
        }
        catch (Exception exception) when (
            (exception is HttpRequestException or InvalidOperationException)
            && !CliErrorMapper.IsTimeout(exception))
        {
            output.Warning("Runtime is not reachable; attempting the same validated reset under the offline Runtime lease.");
        }

        var result = await runtime.ResetLocalStateAsync(token).ConfigureAwait(false);
        foreach (var category in result.Categories)
        {
            output.Line($"{category.Category}: {category.Status}");
        }

        var reset = result.Categories.Count(category => category.Status == "reset");
        var alreadyEmpty = result.Categories.Count(category => category.Status == "already-empty");
        var partial = result.Categories.Count(category => category.Status == "partial");
        output.Data(new
        {
            schemaVersion = RuntimeLocalState.SchemaVersion,
            reset,
            alreadyEmpty,
            partial,
            categories = result.Categories,
        });

        if (!result.Success)
        {
            output.Error("Runtime V1 reset was partial. Re-run the command after resolving locked files or credential-provider access.");
            return CliExitCodes.Failure;
        }

        output.Success($"Runtime V1 reset complete: {reset} reset, {alreadyEmpty} already empty.");
        return CliExitCodes.Success;
    }
}
