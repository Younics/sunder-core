using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class ExecutionRuntimeCommandHandler(
    ICliRuntimeExecutionRuntimeClient runtime,
    CliOutput output,
    ICliProgress progress)
{
    public async Task<int> ExecuteAsync(ExecutionRuntimeSettingsCommand command, CancellationToken token)
    {
        var settings = await runtime.GetExecutionRuntimeSettingsAsync(token);
        if (command.AutomaticUpdates is { } enabled) settings = await runtime.UpdateExecutionRuntimeSettingsAsync(new(settings.Revision, enabled), token);
        output.Data(settings);
        output.Info($"Automatic runtime binary updates: {settings.AutomaticUpdatesEnabled}; security refresh remains active.");
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(InstallExecutionRuntimeCommand command, CancellationToken token)
    {
        var preview = await runtime.PreviewExecutionRuntimeInstallAsync(new(
            new(command.RuntimeId, command.VersionRange, command.ModuleAbi), true), token);
        output.Data(preview);
        output.Info(preview.Message);
        if (preview.Resolution is { } resolution)
            output.Line($"{DisplayIdentity(resolution.Artifact.Identity)} ({resolution.Artifact.Identity.Rid}); download {DisplayDownloadSize(resolution.DownloadSize)}; retained until uninstall.");
        if (!preview.Success || preview.Plan is null) return CliExitCodes.Conflict;
        if (!command.Yes) { output.Info("Review the exact runtime and download size; rerun with --yes to install and retain it."); return CliExitCodes.Success; }
        return RenderOperation(await runtime.ApplyExecutionRuntimeInstallAsync(new(preview.Plan.PlanId, preview.Plan.Fingerprint), token));
    }

    public async Task<int> ExecuteAsync(UpdateExecutionRuntimesCommand command, CancellationToken token)
    {
        var preview = await runtime.PreviewExecutionRuntimeUpdateAsync(new(), token);
        output.Data(preview);
        output.Info(preview.Message);
        foreach (var candidate in preview.Runtimes)
            output.Line($"{DisplayIdentity(candidate.CurrentRuntime)} -> {DisplayIdentity(candidate.Resolution.Artifact.Identity)}; download {DisplayDownloadSize(candidate.Resolution.DownloadSize)}.");
        output.Line("Restart impact: " + string.Join(", ", preview.Usage.AffectedPackageIds));
        if (!preview.Success || preview.Plan is null) return CliExitCodes.Conflict;
        if (!command.Yes || preview.Runtimes.Count == 0) return CliExitCodes.Success;
        var prepared = await runtime.PrepareExecutionRuntimeUpdateAsync(new(preview.Plan.PlanId, preview.Plan.Fingerprint), token);
        if (!prepared.Success || prepared.Update is null) { output.Data(prepared); output.Info(prepared.Message); return prepared.Success ? CliExitCodes.Success : CliExitCodes.Conflict; }
        var apply = await runtime.PreviewExecutionRuntimeUpdateAsync(new(PreparedUpdateId: prepared.Update.UpdateId), token);
        output.Data(apply);
        if (!apply.Success || apply.Plan is null || !preview.Usage.AffectedPackageIds.SequenceEqual(apply.Usage.AffectedPackageIds)
            || preview.Usage.RequiresAppRestart != apply.Usage.RequiresAppRestart)
        { output.Warning("Update impact changed; review a fresh preview before applying."); return CliExitCodes.Conflict; }
        var result = await runtime.ApplyExecutionRuntimeUpdateAsync(new(new(apply.Plan.PlanId, apply.Plan.Fingerprint), apply.Usage.Fingerprint,
            command.Restart ? RuntimeExecutionRuntimeApplyMode.RestartAffectedPackages : RuntimeExecutionRuntimeApplyMode.WhenStopped), token);
        output.Data(result);
        output.Info(result.Message);
        return result.Success ? CliExitCodes.Success : CliExitCodes.Conflict;
    }

    public async Task<int> ExecuteAsync(RemoveExecutionRuntimeCommand command, CancellationToken token)
    {
        var installed = await runtime.GetExecutionRuntimeAsync(ToContract(command.Identity), token);
        var preview = await runtime.PreviewExecutionRuntimeUninstallAsync(new(installed.Identity), token);
        output.Data(preview);
        output.Info(preview.Message);
        if (!preview.Success || !preview.CanUninstall || preview.Plan is null) return CliExitCodes.Conflict;
        var result = await runtime.ApplyExecutionRuntimeUninstallAsync(new(preview.Plan.PlanId, preview.Plan.Fingerprint), token);
        output.Data(result);
        output.Info(result.Message);
        return result.Success ? CliExitCodes.Success : CliExitCodes.Conflict;
    }

    public async Task<int> ExecuteAsync(ListExecutionRuntimesCommand command, CancellationToken token)
    {
        var runtimes = (await runtime.GetExecutionRuntimesAsync(token).ConfigureAwait(false))
            .OrderBy(item => item.Identity.RuntimeId, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.VendorVersion, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.Rid, StringComparer.Ordinal)
            .ThenBy(item => item.Identity.VendorChecksumDigest, StringComparer.Ordinal)
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
        output.Line($"{"Execution Runtime".PadRight(identityWidth)}  {"RID".PadRight(ridWidth)}  Status     Support          Refs  Size");
        foreach (var item in runtimes)
        {
            output.Line(
                $"{DisplayIdentity(item.Identity).PadRight(identityWidth)}  "
                + $"{item.Identity.Rid.PadRight(ridWidth)}  "
                + $"{item.Status.PadRight(10)} "
                + $"{item.Support.State.ToString().PadRight(16)} "
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
                    new RuntimeExecutionRuntimeRequirement(
                        command.RuntimeId,
                        command.VersionRange,
                        command.ModuleAbi)),
                token)
            .ConfigureAwait(false);
        return RenderOperation(result);
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
                    or ExecutionRuntimeErrorCodes.SourceNotAllowed
                    or ExecutionRuntimeErrorCodes.ArtifactChanged
                    or ExecutionRuntimeErrorCodes.PlatformPrerequisiteMissing
                    or ExecutionRuntimeResolutionConflictCodes.NoCompatibleVersion
                    or ExecutionRuntimeResolutionConflictCodes.RidUnavailable
                    or ExecutionRuntimeResolutionConflictCodes.ModuleAbiMismatch
                    or ExecutionRuntimeErrorCodes.PlanStale => CliExitCodes.Conflict,
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
        output.Line($"Source: {DisplaySource(summary.Identity.SourceId)}");
        output.Line($"RID: {summary.Identity.Rid}");
        output.Line($"Module ABI: {summary.Identity.ModuleAbi}");
        output.Line($"Vendor checksum: {DisplayChecksumAlgorithm(summary.Identity.VendorChecksumAlgorithm)} {summary.Identity.VendorChecksumDigest}");
        output.Line($"Archive: {summary.InstalledArtifact.ArchiveSize} bytes, sha256 {summary.InstalledArtifact.ArchiveSha256}");
        output.Line($"Status: {summary.Status}");
        output.Line($"Health: {summary.Health.Status}");
        output.Line($"Support: {summary.Support.State} ({summary.Support.SupportLine})");
        output.Line($"References: {summary.PackageReferenceCount}");
        if (!string.IsNullOrWhiteSpace(summary.Health.Message)) output.Warning(summary.Health.Message);
        if (!string.IsNullOrWhiteSpace(summary.Support.Message)) output.Warning(summary.Support.Message);
    }

    private static string DisplayIdentity(RuntimeExecutionRuntimeIdentity identity)
        => $"{identity.RuntimeId} {identity.VendorVersion}";

    private static string DisplayDownloadSize(long? bytes)
        => bytes is null ? "unknown size" : $"{bytes.Value} bytes";

    private static string DisplaySource(ConfiguredOfficialSourceId source)
        => source switch
        {
            ConfiguredOfficialSourceId.DotNet => "dotnet",
            ConfiguredOfficialSourceId.Node => "node",
            _ => source.ToString(),
        };

    private static string DisplayChecksumAlgorithm(RuntimeExecutionRuntimeChecksumAlgorithm algorithm)
        => algorithm switch
        {
            RuntimeExecutionRuntimeChecksumAlgorithm.Sha512 => "sha512",
            RuntimeExecutionRuntimeChecksumAlgorithm.Sha256 => "sha256",
            _ => algorithm.ToString(),
        };

    private static RuntimeExecutionRuntimeIdentity ToContract(CliExecutionRuntimeIdentity identity)
        => new(
            identity.SourceId,
            identity.RuntimeId,
            identity.VendorVersion,
            identity.Rid,
            identity.ModuleAbi,
            identity.VendorChecksumAlgorithm,
            identity.VendorChecksumDigest);
}
