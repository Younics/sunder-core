namespace Sunder.Cli;

internal sealed class DeveloperPublishCommandHandler(
    ICliRuntimePublishClient runtime,
    IRegistryManageClient registry,
    ArchiveValidationService archives,
    CliOutput output,
    ICliProgress progress,
    CliOptions options)
{
    public async Task<int> ExecuteAsync(PublishPackageCommand command, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(command.File);
        var validation = await archives.ValidatePackageAsync(fullPath, token).ConfigureAwait(false);
        if (!validation.Success || validation.Manifest?.Id is null || validation.Manifest.Version is null)
            return CliRenderers.PackageValidation(output, validation);

        progress.Report(command.DevLocal
            ? $"Publishing {validation.Manifest.Id} {validation.Manifest.Version} to the development Registry..."
            : $"Publishing {validation.Manifest.Id} {validation.Manifest.Version} through the Runtime...");
        var result = command.DevLocal
            ? await registry.PublishLocalPackageAsync(fullPath, command.SetLatest, token).ConfigureAwait(false)
            : await runtime.PublishRegistryPackageAsync(options.RegistryApiUrl.AbsoluteUri, fullPath, command.SetLatest, token).ConfigureAwait(false);
        return CliRenderers.PackagePublish(output, result);
    }
}
