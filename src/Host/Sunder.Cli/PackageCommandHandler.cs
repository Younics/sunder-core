using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class PackageCommandHandler(
    ICliRuntimeClient runtime,
    CliOutput output,
    ICliProgress progress,
    CliOptions options,
    ArchiveValidationService archives)
{
    public async Task<int> ExecuteAsync(ListInstalledCommand command, CancellationToken token)
    {
        var packages = (await runtime.GetInstalledPackagesAsync(token).ConfigureAwait(false))
            .OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        output.Data(packages);
        if (packages.Length == 0)
        {
            output.Info("No packages installed.");
            return CliExitCodes.Success;
        }
        CliRenderers.InstalledPackages(output, packages);
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(InstallRegistryPackageCommand command, CancellationToken token)
    {
        progress.Report("Runtime is resolving and applying the package transaction...");
        var result = await runtime.InstallRegistryPackageAsync(
            new(options.RegistryApiUrl.AbsoluteUri, command.PackageId, command.Version, command.Tag, AllowDowngrade: command.AllowDowngrade, Reinstall: command.Reinstall),
            token).ConfigureAwait(false);
        return CliRenderers.RegistryPackageChange(output, result);
    }

    public async Task<int> ExecuteAsync(InstallLocalPackageCommand command, CancellationToken token)
    {
        var validation = await archives.ValidatePackageAsync(command.File, token).ConfigureAwait(false);
        if (!validation.Success || string.IsNullOrWhiteSpace(validation.Manifest?.Id))
            return CliRenderers.PackageValidation(output, validation);
        progress.Report("Uploading the validated package to the Runtime...");
        var result = await runtime.ApplyLocalPackageAsync(
            Path.GetFullPath(command.File), validation.Manifest.Id, command.AllowDowngrade, command.Reinstall, token).ConfigureAwait(false);
        return CliRenderers.PackageOperation(output, result);
    }

    public async Task<int> ExecuteAsync(UpdatePackagesCommand command, CancellationToken token)
    {
        progress.Report("Runtime is resolving and applying one atomic update transaction...");
        var result = await runtime.UpdateRegistryPackagesAsync(
            new(options.RegistryApiUrl.AbsoluteUri, command.PackageId, command.IncludePrerelease), token).ConfigureAwait(false);
        return CliRenderers.RegistryPackageChange(output, result);
    }
}
