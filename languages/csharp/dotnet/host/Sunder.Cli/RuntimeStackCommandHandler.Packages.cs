using Sunder.Package.Format;
using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed partial class RuntimeStackCommandHandler
{
    private async Task<(int ExitCode, object? Preflight)> PreparePackageRequirementsAsync(SunderStackManifest manifest, ImportStackCommand command, CancellationToken token)
    {
        if ((manifest.Packages?.Count ?? 0) == 0) return (CliExitCodes.Success, null);
        if (options.RegistryApiUrl is null)
        {
            output.Info("No Registry is configured. Runtime will require the Stack packages to be active locally; no packages will be installed.");
            return (CliExitCodes.Success, null);
        }
        var request = new RuntimeRegistryPackageBatchRequest(options.RequireRegistryApiUrl().AbsoluteUri,
            manifest.Packages!.Select(package => new RuntimeRegistryPackageChangeRequest(package.PackageId!, null, [], package.InstallTag,
                string.IsNullOrWhiteSpace(package.MinimumVersion) ? ">=0.0.0-0" : $">={package.MinimumVersion}", package.Required != false)).ToArray());
        var plan = await packages.ResolveRegistryPackagePlanAsync(request, token).ConfigureAwait(false);
        if (plan.Success && (plan.ResolutionFingerprint is not { Length: 64 } fingerprint || !fingerprint.All(char.IsAsciiHexDigit)))
        {
            output.Error("Runtime did not return a valid Stack package plan fingerprint.", "runtime.stack.package_plan.invalid");
            return (CliExitCodes.Conflict, null);
        }
        var preflight = CliPackageChangePreflight.Create(plan, await executionRuntimes.GetExecutionRuntimesAsync(token).ConfigureAwait(false));
        var data = CliJsonData.RegistryPackageExecution(preflight, command.DryRun, result: null);
        output.Data(data);
        var exit = CliRenderers.RegistryPackagePreflight(output, preflight, command.ApproveSensitiveCapabilities, null);
        if (exit != CliExitCodes.Success) return (exit, data);
        if (command.DryRun || plan.Items.Count == 0) return (CliExitCodes.Success, data);
        if (preflight.RequiredCapabilityConsents.Count > 0 && !command.ApproveSensitiveCapabilities)
        {
            output.Error("Review package capabilities and rerun with '--yes --approve-sensitive-capabilities'.", "runtime.registry.consent_required");
            return (CliExitCodes.Conflict, data);
        }
        var applied = await packages.ApplyRegistryPackagePlanAsync(request with
        {
            ExpectedResolutionFingerprint = plan.ResolutionFingerprint,
            CapabilityConsents = command.ApproveSensitiveCapabilities ? preflight.RequiredCapabilityConsents : [],
        }, token).ConfigureAwait(false);
        output.Data(CliJsonData.RegistryPackageExecution(preflight, command.DryRun, applied));
        return (CliRenderers.RegistryPackageChange(output, applied, includePlan: false, writeData: false),
            CliJsonData.RegistryPackageExecution(preflight, command.DryRun, applied));
    }
}
