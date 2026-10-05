using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class ExecutionRuntimeCommandHandler(
    ICliRuntimeExecutionRuntimeClient runtime,
    CliOutput output,
    ICliProgress progress,
    CliOptions options)
{
    public async Task<int> ExecuteAsync(ListExecutionRuntimesCommand command, CancellationToken token)
    {
        var runtimes = (await runtime.GetExecutionRuntimesAsync(token).ConfigureAwait(false))
            .OrderBy(item => item.Identity.RuntimeId, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.Version, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.PackagingRevision)
            .ThenBy(item => item.Identity.Rid, StringComparer.Ordinal)
            .ToArray();
        output.Data(runtimes);
        if (runtimes.Length == 0)
        {
            output.Info("No execution runtimes are installed.");
            return CliExitCodes.Success;
        }

        var identityWidth = Math.Max(
            "Execution Runtime".Length,
            runtimes.Max(item => DisplayIdentity(item.Identity).Length));
        var ridWidth = Math.Max("RID".Length, runtimes.Max(item => item.Identity.Rid.Length));
        output.Line($"{"Execution Runtime".PadRight(identityWidth)}  {"RID".PadRight(ridWidth)}  Status     Lifecycle        Refs  Size");
        foreach (var item in runtimes)
        {
            output.Line(
                $"{DisplayIdentity(item.Identity).PadRight(identityWidth)}  "
                + $"{item.Identity.Rid.PadRight(ridWidth)}  "
                + $"{item.Status.PadRight(10)} "
                + $"{item.Lifecycle.State.PadRight(16)} "
                + $"{item.PackageReferenceCount,4}  {item.Size} bytes");
        }
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(ShowExecutionRuntimeCommand command, CancellationToken token)
    {
        var identity = ToContract(command.Identity);
        var summary = await runtime.GetExecutionRuntimeAsync(identity, token).ConfigureAwait(false);
        var references = await runtime.GetExecutionRuntimeReferencesAsync(identity, token).ConfigureAwait(false);
        output.Data(new { runtime = summary, references });
        WriteSummary(summary);
        output.Line("Dependent packages:");
        foreach (var reference in references)
        {
            output.Line($"  {reference.PackageId} {reference.PackageVersion} ({reference.Role}, {reference.Rid})");
        }
        if (references.Count == 0) output.Line("  none");
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(EnsureExecutionRuntimeCommand command, CancellationToken token)
    {
        progress.Report("Runtime is resolving, downloading, and verifying the execution runtime...");
        var result = await runtime.EnsureExecutionRuntimeAsync(
                new RuntimeExecutionRuntimeEnsureRequest(
                    command.RegistryOrigin ?? options.RequireRegistryApiUrl().AbsoluteUri,
                    new RuntimeExecutionRuntimeRequirement(
                        command.RuntimeId,
                        command.VersionRange,
                        command.ModuleAbi)),
                token)
            .ConfigureAwait(false);
        return RenderOperation(result);
    }

    public async Task<int> ExecuteAsync(ImportExecutionRuntimeCommand command, CancellationToken token)
    {
        var path = Path.GetFullPath(command.File);
        if (!File.Exists(path)) throw new FileNotFoundException("Execution runtime archive was not found.", path);
        if (!string.Equals(Path.GetExtension(path), ".sunderruntime", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("Execution runtime import requires a .sunderruntime archive.");

        progress.Report("Uploading the execution runtime archive for trusted validation and import...");
        return RenderOperation(await runtime.ImportExecutionRuntimeAsync(path, token).ConfigureAwait(false));
    }

    public async Task<int> ExecuteAsync(VerifyExecutionRuntimeCommand command, CancellationToken token)
    {
        progress.Report("Deeply verifying the execution runtime...");
        return RenderOperation(await runtime.VerifyExecutionRuntimeAsync(ToContract(command.Identity), token)
            .ConfigureAwait(false));
    }

    public async Task<int> ExecuteAsync(RepairExecutionRuntimeCommand command, CancellationToken token)
    {
        progress.Report("Stopping dependent Workers and repairing the execution runtime from cached bytes...");
        return RenderOperation(await runtime.RepairExecutionRuntimeAsync(ToContract(command.Identity), token)
            .ConfigureAwait(false));
    }

    public async Task<int> ExecuteAsync(GarbageCollectExecutionRuntimesCommand command, CancellationToken token)
    {
        progress.Report("Requesting lease-safe execution runtime garbage collection...");
        return RenderOperation(await runtime.CollectExecutionRuntimeGarbageAsync(token).ConfigureAwait(false));
    }

    private int RenderOperation(RuntimeExecutionRuntimeOperationResult result)
    {
        output.Data(result);
        if (!result.Success)
        {
            output.Error(result.Message, result.ErrorCode ?? "execution-runtime.install-failed");
            return result.ErrorCode switch
            {
                ExecutionRuntimeErrorCodes.Missing => CliExitCodes.NotFound,
                ExecutionRuntimeErrorCodes.DownloadFailed => CliExitCodes.Unavailable,
                 ExecutionRuntimeErrorCodes.Busy
                    or ExecutionRuntimeErrorCodes.RequirementInvalid
                    or ExecutionRuntimeErrorCodes.Unsupported
                    or ExecutionRuntimeErrorCodes.Unresolved
                    or ExecutionRuntimeResolutionConflictCodes.NoCompatibleVersion
                    or ExecutionRuntimeResolutionConflictCodes.RidUnavailable
                    or ExecutionRuntimeResolutionConflictCodes.ModuleAbiMismatch
                    or ExecutionRuntimeErrorCodes.PolicyRequired
                    or ExecutionRuntimeErrorCodes.PlanStale
                    or ExecutionRuntimeErrorCodes.HostTrustUpdateRequired
                    or ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied => CliExitCodes.Conflict,
                _ => CliExitCodes.Failure,
            };
        }

        output.Success(result.Message);
        if (result.Runtime is not null) WriteSummary(result.Runtime);
        return CliExitCodes.Success;
    }

    private void WriteSummary(InstalledExecutionRuntimeSummary summary)
    {
        output.Line($"Runtime: {DisplayIdentity(summary.Identity)}");
        output.Line($"RID: {summary.Identity.Rid}");
        output.Line($"Module ABI: {summary.Identity.ModuleAbi}");
        output.Line($"Status: {summary.Status}");
        output.Line($"Health: {summary.Health.Status}");
        output.Line($"Lifecycle: {summary.Lifecycle.State}");
        output.Line($"References: {summary.PackageReferenceCount}");
        if (!string.IsNullOrWhiteSpace(summary.Health.Message)) output.Warning(summary.Health.Message);
        if (!string.IsNullOrWhiteSpace(summary.Lifecycle.PolicyMessage)) output.Warning(summary.Lifecycle.PolicyMessage);
    }

    private static string DisplayIdentity(RuntimeExecutionRuntimeIdentity identity)
        => $"{identity.RuntimeId} {identity.Version} r{identity.PackagingRevision}";

    private static RuntimeExecutionRuntimeIdentity ToContract(CliExecutionRuntimeIdentity identity)
        => new(
            identity.RuntimeId,
            identity.Version,
            identity.PackagingRevision,
            identity.Rid,
            identity.ModuleAbi,
            ManifestSha256: string.Empty,
            ArtifactSha256: string.Empty);
}
