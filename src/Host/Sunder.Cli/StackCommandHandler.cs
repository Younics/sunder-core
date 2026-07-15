using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class StackCommandHandler(
    ICliRuntimePublishClient runtimePublisher,
    ICliRuntimeManagementClient runtimeManager,
    IRegistryClient registry,
    ArchiveValidationService archives,
    CliOutput output,
    ICliProgress progress,
    CliOptions options)
{
    public async Task<int> ExecuteAsync(SearchStacksCommand command, CancellationToken token)
    {
        var stacks = (await registry.SearchStacksAsync(command.Query, command.Skip, command.Take, token).ConfigureAwait(false))
            .ToArray();
        output.Data(stacks);
        if (stacks.Length == 0)
        {
            output.Info("No Stacks found.");
            return CliExitCodes.Success;
        }
        CliRenderers.StackSummaries(output, stacks);
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(StackInfoCommand command, CancellationToken token)
    {
        var stack = await FindAsync(command.StackId, token).ConfigureAwait(false);
        if (stack is null) return CliExitCodes.NotFound;
        output.Data(stack);
        CliRenderers.StackDetails(output, stack);
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(UseStackCommand command, CancellationToken token)
    {
        var stack = await FindAsync(command.StackId, token).ConfigureAwait(false);
        if (stack is null) return CliExitCodes.NotFound;
        var link = BuildShowLink(stack.StackId);
        output.Data(new { stack, showLink = link });
        CliRenderers.StackDetails(output, stack);
        output.Line();
        output.Info("Open this link in Sunder App to review and use the Stack:");
        output.Line(link);
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(DownloadStackCommand command, CancellationToken token)
    {
        var stack = await FindAsync(command.StackId, token).ConfigureAwait(false);
        if (stack is null) return CliExitCodes.NotFound;
        var destination = ResolveOutput(command.Output, stack.StackId);
        progress.Report($"Downloading Stack '{stack.StackId}'...");
        await registry.DownloadStackAsync(stack.Artifact, stack.StackId, destination, command.Force, token).ConfigureAwait(false);
        output.Data(new { stackId = stack.StackId, output = destination, stack.Artifact.Sha256, stack.Artifact.Size });
        output.Success($"Downloaded Stack '{stack.StackId}' to {destination}.");
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(PublishStackCommand command, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(command.File);
        var validation = await archives.ValidateStackAsync(fullPath, token).ConfigureAwait(false);
        if (!validation.Success || validation.Manifest?.StackId is null)
            return CliRenderers.StackValidation(output, validation);
        progress.Report(command.DevLocal
            ? $"Publishing Stack '{validation.Manifest.StackId}' to the development Registry..."
            : $"Publishing Stack '{validation.Manifest.StackId}' through the Runtime...");
        var result = command.DevLocal
            ? await registry.PublishLocalStackAsync(fullPath, token).ConfigureAwait(false)
            : await runtimePublisher.PublishRegistryStackAsync(options.RegistryApiUrl.AbsoluteUri, fullPath, token).ConfigureAwait(false);
        return CliRenderers.StackPublish(output, result);
    }

    public async Task<int> ExecuteAsync(UpdateStackCommand command, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(command.File);
        var validation = await archives.ValidateStackAsync(fullPath, token).ConfigureAwait(false);
        if (!validation.Success || validation.Manifest?.StackId is null)
            return CliRenderers.StackValidation(output, validation);
        if (!string.Equals(validation.Manifest.StackId, command.StackId, StringComparison.OrdinalIgnoreCase))
        {
            output.Error($"Stack archive id '{validation.Manifest.StackId}' does not match requested Stack id '{command.StackId}'.");
            return CliExitCodes.Usage;
        }
        progress.Report($"Updating Stack '{validation.Manifest.StackId}' through the Runtime...");
        var result = await runtimePublisher.PublishRegistryStackAsync(options.RegistryApiUrl.AbsoluteUri, fullPath, token).ConfigureAwait(false);
        return CliRenderers.StackPublish(output, result);
    }

    public async Task<int> ExecuteAsync(DeleteStackCommand command, CancellationToken token)
    {
        var result = await runtimeManager.DeleteRegistryStackAsync(
            new RuntimeRegistryDeleteStackRequest(options.RegistryApiUrl.AbsoluteUri, command.StackId), token).ConfigureAwait(false);
        return CliRenderers.StackManagement(output, result);
    }

    public async Task<int> ExecuteAsync(ValidateStackCommand command, CancellationToken token)
    {
        var result = await archives.ValidateStackAsync(command.File, token).ConfigureAwait(false);
        return CliRenderers.StackValidation(output, result);
    }

    private async Task<Sunder.Registry.Contracts.RegistryStackDetails?> FindAsync(string stackId, CancellationToken token)
    {
        var stack = await registry.GetStackAsync(stackId, token).ConfigureAwait(false);
        if (stack is null) output.Error($"Stack '{stackId}' was not found.");
        return stack;
    }

    private static string ResolveOutput(string? output, string stackId)
    {
        if (string.IsNullOrWhiteSpace(output)) return Path.GetFullPath(SunderStackFormat.BuildStackFileName(stackId));
        if (Path.EndsInDirectorySeparator(output))
            throw new CliUsageException("Option '--output' must name the exact destination file, not a directory.");
        var path = Path.GetFullPath(output);
        if (Directory.Exists(path))
            throw new CliUsageException("Option '--output' must name the exact destination file, not a directory.");
        return path;
    }

    private static string BuildShowLink(string stackId) => $"sunder://stacks/{Uri.EscapeDataString(stackId)}";
}
