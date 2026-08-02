using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class RegistryManagementCommandHandler(
    ICliRuntimeManagementClient? runtime,
    IRegistryBrowseClient? registry,
    CliOutput output,
    CliOptions options)
{
    public async Task<int> ExecuteAsync(SetYankCommand command, CancellationToken token)
    {
        var result = await RequireRuntime().SetYankAsync(
            new(options.RequireRegistryApiUrl().AbsoluteUri, command.PackageId, command.Version, command.IsYanked), token).ConfigureAwait(false);
        return CliRenderers.Management(output, result);
    }

    public async Task<int> ExecuteAsync(SetDeprecationCommand command, CancellationToken token)
    {
        var result = await RequireRuntime().SetDeprecationAsync(
            new(options.RequireRegistryApiUrl().AbsoluteUri, command.PackageId, command.Version, command.Message), token).ConfigureAwait(false);
        return CliRenderers.Management(output, result);
    }

    public async Task<int> ExecuteAsync(ListDistTagsCommand command, CancellationToken token)
    {
        var result = await RequireRegistry().GetDistTagsAsync(command.PackageId, token).ConfigureAwait(false);
        if (result is null)
        {
            output.Error($"Package '{command.PackageId}' was not found.", "cli.resource.not_found");
            return CliExitCodes.NotFound;
        }
        output.Data(new
        {
            packageId = result.PackageId,
            tags = result.DistTags.Select(item => new
            {
                tag = item.Tag,
                version = item.Version,
                updatedAtUtc = item.UpdatedAtUtc,
            }).ToArray(),
        });
        if (result.DistTags.Count == 0)
        {
            output.Info($"Package '{command.PackageId}' has no dist tags.");
            return CliExitCodes.Success;
        }
        var tags = result.DistTags.OrderBy(item => item.Tag, StringComparer.OrdinalIgnoreCase).ToArray();
        var width = Math.Max("Tag".Length, tags.Max(item => item.Tag.Length));
        output.Line($"{"Tag".PadRight(width)}  Version");
        foreach (var tag in tags) output.Line($"{tag.Tag.PadRight(width)}  {tag.Version}");
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(SetDistTagCommand command, CancellationToken token)
    {
        var result = await RequireRuntime().SetDistTagAsync(
            new(options.RequireRegistryApiUrl().AbsoluteUri, command.PackageId, command.Tag, command.Version), token).ConfigureAwait(false);
        return CliRenderers.Management(output, result);
    }

    private ICliRuntimeManagementClient RequireRuntime()
        => runtime ?? throw new InvalidOperationException("Registry management requires a Runtime client.");

    private IRegistryBrowseClient RequireRegistry()
        => registry ?? throw new InvalidOperationException("Registry tag listing requires a Registry client.");
}
