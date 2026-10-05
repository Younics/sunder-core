using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class DevPackageCommandHandler(
    ICliManagedRuntimeClient host,
    ICliRuntimeDevPackageClient runtime,
    CliOutput output)
{
    public async Task<int> ExecuteAsync(PrepareDevPackageImportsCommand command, CancellationToken token)
    {
        var folder = Path.GetFullPath(command.Folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Development package folder '{folder}' does not exist.");
        await host.EnsureRuntimeReadyAsync(token).ConfigureAwait(false);
        var request = new RuntimePrepareDevPackageImportsRequest(folder, command.RegistryOrigin)
        {
            CandidateArchives = (command.CandidateArchives ?? []).Select(Path.GetFullPath).ToArray(),
            PlatformCatalogPath = command.PlatformCatalogPath is null ? null : Path.GetFullPath(command.PlatformCatalogPath),
        };
        var result = await runtime.PrepareDevPackageImportsAsync(request, token).ConfigureAwait(false);
        output.Data(result);
        output.Success("Development import evidence is prepared. The local package remains unpublished; run/smoke and hot reload use the retained evidence offline.");
        return CliExitCodes.Success;
    }

    public Task<int> ExecuteAsync(RunDevPackageCommand command, CancellationToken token)
        => ExecuteAsync(command.Folder, command.Watch, hold: true, token);

    public Task<int> ExecuteAsync(SmokeDevPackageCommand command, CancellationToken token)
        => ExecuteAsync(command.Folder, watch: false, hold: false, token);

    private async Task<int> ExecuteAsync(
        string folder,
        bool watch,
        bool hold,
        CancellationToken token)
    {
        var fullPath = Path.GetFullPath(folder);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Development package folder '{fullPath}' does not exist.");
        }

        await host.EnsureRuntimeReadyAsync(token).ConfigureAwait(false);
        var session = runtime.CreateDevPackageOwnerSession(ReportSessionFault);
        try
        {
            var lease = await session.AcquireAsync([fullPath], watch, token).ConfigureAwait(false);
            WriteResult(lease, fullPath, watch, hold);
            if (!hold)
            {
                await session.ReleaseAsync(token).ConfigureAwait(false);
                output.Success("Development package smoke completed through the managed Runtime.");
                return CliExitCodes.Success;
            }

            output.Success("Development package is running through the managed Runtime. Press Ctrl+C to stop.");
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return CliExitCodes.Success;
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void WriteResult(
        DevPackageOwnerLeaseResponse lease,
        string folder,
        bool watch,
        bool hold)
    {
        output.Data(new
        {
            mode = hold ? "run" : "smoke",
            folder,
            watch,
            runtimeInstanceId = lease.RuntimeInstanceId,
            sessionGeneration = lease.SessionGeneration,
            packages = lease.Packages.Select(package => new
            {
                packageId = package.PackageId,
                watch = package.Watch,
            }).ToArray(),
        });
        foreach (var package in lease.Packages)
        {
            output.Info($"Loaded {package.PackageId}{(package.Watch ? " with watching" : string.Empty)}.");
        }
    }

    private void ReportSessionFault(string operation, Exception exception)
        => output.Warning(
            $"Development package owner {operation} failed; Runtime lease fencing will clean up if needed: {exception.Message}");
}
