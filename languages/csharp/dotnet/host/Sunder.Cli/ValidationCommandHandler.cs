namespace Sunder.Cli;

internal sealed class ValidationCommandHandler(ArchiveValidationService archives, CliOutput output)
{
    public async Task<int> ExecuteAsync(ValidatePackageCommand command, CancellationToken token)
    {
        var result = await archives.ValidatePackageAsync(command.File, token).ConfigureAwait(false);
        return CliRenderers.PackageValidation(output, result);
    }
}
