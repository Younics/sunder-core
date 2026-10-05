namespace Sunder.Cli;

internal sealed class ValidationCommandHandler(ArchiveValidationService archives, CliOutput output)
{
    public async Task<int> ExecuteAsync(ValidatePackageCommand command, CancellationToken token)
    {
        var result = command.ApiOutput is null
            ? await archives.ValidatePackageAsync(command.File, token).ConfigureAwait(false)
            : await archives.ExportPackageApiAsync(command.File, command.ApiOutput, token).ConfigureAwait(false);
        if (command.ApiOutput is not null && result.Success)
        {
            output.Data(new { schemaVersion = 1, publicApiExported = true });
            output.Success("Validated public API inspection exported.");
            return CliExitCodes.Success;
        }
        return CliRenderers.PackageValidation(output, result);
    }
}
