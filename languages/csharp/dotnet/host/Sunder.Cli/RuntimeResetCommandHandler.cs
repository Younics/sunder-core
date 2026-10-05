using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Cli;

internal sealed class RuntimeResetCommandHandler(
    ICliRuntimeResetClient runtime,
    CliOutput output,
    Uri runtimeUrl)
{
    public async Task<int> ExecuteAsync(RuntimeResetCommand command, CancellationToken token)
    {
        if (!command.Yes)
        {
            throw new CliUsageException("Runtime reset requires --yes.");
        }

        if (runtimeUrl != CliOptions.DefaultRuntimeUrl)
        {
            throw new CliUsageException(
                $"Runtime reset only supports the default local Host at '{CliOptions.DefaultRuntimeUrl}'. Remove the custom Runtime URL before resetting local state.");
        }

        var challenge = await runtime.PrepareResetAsync(token).ConfigureAwait(false);
        if (challenge.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The Runtime reset confirmation challenge expired before it could be used.");
        }

        var result = command.Scope == RuntimeResetScope.StartupCache
            ? await runtime.DrainAndResetStartupCacheAsync(challenge, token).ConfigureAwait(false)
            : await runtime.DrainAndResetLocalStateAsync(challenge, token).ConfigureAwait(false);
        output.Info("Runtime accepted the one-time reset challenge and returned the bound local reset result.");
        foreach (var category in result.Categories)
        {
            output.Line($"{category.Category}: {category.Status}");
            foreach (var relativePath in category.RelativePaths ?? [])
            {
                output.Line($"  {relativePath}");
            }
        }
        if (result.ReceiptRelativePath is not null)
        {
            output.Line($"receipt: {result.ReceiptRelativePath}");
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
            categories = result.Categories.Select(category => new
            {
                name = category.Category,
                status = category.Status,
                paths = category.RelativePaths ?? [],
            }).ToArray(),
            receipt = result.ReceiptRelativePath,
        });

        if (!result.Success)
        {
            output.Error(
                "Runtime V1 reset was partial. Re-run the command after resolving locked files or credential-provider access.",
                "runtime.reset.partial");
            return CliExitCodes.Failure;
        }

        output.Success($"Runtime V1 reset complete: {reset} reset, {alreadyEmpty} already empty.");
        return CliExitCodes.Success;
    }
}
