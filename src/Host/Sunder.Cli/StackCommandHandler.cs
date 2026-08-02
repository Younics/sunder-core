using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class StackCommandHandler(
    ICliRuntimePublishClient? runtimePublisher,
    ICliRuntimeManagementClient? runtimeManager,
    IRegistryClient? registry,
    ArchiveValidationService archives,
    CliOutput output,
    ICliProgress progress,
    IBrowserLauncher browser,
    CliOptions options,
    IRegistryPublishCredentialReader credentials)
{
    public async Task<int> ExecuteAsync(SearchStacksCommand command, CancellationToken token)
    {
        var stacks = (await RequireRegistry().SearchStacksAsync(command.Query, command.Skip, command.Take, token).ConfigureAwait(false))
            .ToArray();
        output.Data(CliJsonData.StackSummaries(stacks));
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
        output.Data(CliJsonData.StackDetails(stack));
        CliRenderers.StackDetails(output, stack);
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(OpenStackCommand command, CancellationToken token)
    {
        var stack = await FindAsync(command.StackId, token).ConfigureAwait(false);
        if (stack is null) return CliExitCodes.NotFound;
        var link = BuildShowLink(stack.StackId);
        var opened = browser.TryOpen(new Uri(link));
        output.Data(new { stack = CliJsonData.StackDetails(stack), url = link, opened });
        CliRenderers.StackDetails(output, stack);
        output.Line();
        if (opened)
        {
            output.Success($"Opened Stack '{stack.StackId}' in Sunder App.");
        }
        else
        {
            output.Warning("Sunder App could not be opened automatically. Open this link manually:");
            output.Line(link);
        }
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(DownloadStackCommand command, CancellationToken token)
    {
        var stack = await FindAsync(command.StackId, token).ConfigureAwait(false);
        if (stack is null) return CliExitCodes.NotFound;
        var destination = ResolveOutput(command.Output, stack.StackId);
        progress.Report($"Downloading Stack '{stack.StackId}'...");
        await RequireRegistry().DownloadStackAsync(stack.Artifact, stack.StackId, destination, command.Force, token).ConfigureAwait(false);
        output.Data(new
        {
            stackId = stack.StackId,
            output = destination,
            sha256 = stack.Artifact.Sha256,
            size = stack.Artifact.Size,
        });
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
            : command.CredentialSource == RegistryCredentialSource.Runtime
                ? $"Publishing Stack '{validation.Manifest.StackId}' through the Runtime..."
                : $"Publishing Stack '{validation.Manifest.StackId}' with a scoped automation credential...");
        RegistryPublishStackResponse result;
        if (command.DevLocal)
        {
            result = await RequireRegistry().PublishLocalStackAsync(fullPath, token).ConfigureAwait(false);
        }
        else if (command.CredentialSource == RegistryCredentialSource.Runtime)
        {
            result = await RequirePublisher().PublishRegistryStackAsync(
                options.RequireRegistryApiUrl().AbsoluteUri, fullPath, token).ConfigureAwait(false);
        }
        else
        {
            using var credential = await credentials.ReadAsync(command.CredentialSource, token).ConfigureAwait(false);
            result = await RequireRegistry().PublishStackAsync(fullPath, credential, token).ConfigureAwait(false);
        }
        return CliRenderers.StackPublish(output, result);
    }

    public async Task<int> ExecuteAsync(DeleteStackCommand command, CancellationToken token)
    {
        var result = await RequireManager().DeleteRegistryStackAsync(
            new RuntimeRegistryDeleteStackRequest(options.RequireRegistryApiUrl().AbsoluteUri, command.StackId), token).ConfigureAwait(false);
        return CliRenderers.StackManagement(output, result);
    }

    public async Task<int> ExecuteAsync(ValidateStackCommand command, CancellationToken token)
    {
        var result = await archives.ValidateStackAsync(command.File, token).ConfigureAwait(false);
        return CliRenderers.StackValidation(output, result);
    }

    private async Task<Sunder.Registry.Contracts.RegistryStackDetails?> FindAsync(string stackId, CancellationToken token)
    {
        var stack = await RequireRegistry().GetStackAsync(stackId, token).ConfigureAwait(false);
        if (stack is null) output.Error($"Stack '{stackId}' was not found.", "cli.resource.not_found");
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

    private ICliRuntimePublishClient RequirePublisher()
        => runtimePublisher ?? throw new InvalidOperationException("Registry Stack publication requires a Runtime client.");

    private ICliRuntimeManagementClient RequireManager()
        => runtimeManager ?? throw new InvalidOperationException("Registry Stack management requires a Runtime client.");

    private IRegistryClient RequireRegistry()
        => registry ?? throw new InvalidOperationException("The Stack command requires a Registry client.");
}
