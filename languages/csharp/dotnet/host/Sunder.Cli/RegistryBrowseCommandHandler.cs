namespace Sunder.Cli;

internal sealed class RegistryBrowseCommandHandler(IRegistryBrowseClient registry, CliOutput output)
{
    public async Task<int> ExecuteAsync(SearchPackagesCommand command, CancellationToken token)
    {
        var packages = (await registry.SearchAsync(command.Query, command.Skip, command.Take, token).ConfigureAwait(false))
            .ToArray();
        output.Data(CliJsonData.PackageSummaries(packages));
        if (packages.Length == 0)
        {
            output.Info("No packages found.");
            return CliExitCodes.Success;
        }
        CliRenderers.PackageSummaries(output, packages);
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(PackageInfoCommand command, CancellationToken token)
    {
        if (command.Version is not null)
        {
            var version = await registry.GetVersionAsync(command.PackageId, command.Version, token).ConfigureAwait(false);
            if (version is null)
            {
                output.Error($"Package '{command.PackageId}' {command.Version} was not found.", "cli.resource.not_found");
                return CliExitCodes.NotFound;
            }
            output.Data(CliJsonData.PackageVersion(version));
            CliRenderers.PackageVersion(output, version);
            return CliExitCodes.Success;
        }

        var package = await registry.GetPackageAsync(command.PackageId, token).ConfigureAwait(false);
        if (package is null)
        {
            output.Error($"Package '{command.PackageId}' was not found.", "cli.resource.not_found");
            return CliExitCodes.NotFound;
        }
        output.Data(CliJsonData.PackageDetails(package));
        CliRenderers.PackageDetails(output, package);
        return CliExitCodes.Success;
    }
}
