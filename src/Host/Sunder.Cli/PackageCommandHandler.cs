using Sunder.Runtime.Contracts;

namespace Sunder.Cli;

internal sealed class PackageCommandHandler(
    ICliRuntimePackageClient runtime,
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
        output.Data(packages.Select(package => new
        {
            packageId = package.PackageId,
            version = package.Version,
            enabled = package.IsEnabled,
            summary = package.Summary,
            source = package.Provenance.SourceKind.ToString(),
            versionPolicy = package.Provenance.VersionPolicy.ToString(),
            registryOrigin = package.Provenance.RegistryOrigin,
            tag = package.Provenance.RequestedTag,
        }).ToArray());
        if (packages.Length == 0)
        {
            output.Info("No packages installed.");
            return CliExitCodes.Success;
        }
        CliRenderers.InstalledPackages(output, packages);
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(PackageStatusCommand command, CancellationToken token)
    {
        var installedPackages = await runtime.GetInstalledPackagesAsync(token).ConfigureAwait(false);
        var snapshot = await runtime.GetPackageSnapshotAsync(token).ConfigureAwait(false);
        var installed = installedPackages.FirstOrDefault(package => Matches(package.PackageId, command.PackageId));
        var session = snapshot.SessionPackages.FirstOrDefault(package => Matches(package.PackageId, command.PackageId));
        var active = snapshot.ActivePackages.Any(package => Matches(package.PackageId, command.PackageId));
        if (installed is null && session is null)
        {
            output.Error($"Package '{command.PackageId}' is not installed or present in the Runtime session.", "cli.resource.not_found");
            return CliExitCodes.NotFound;
        }

        output.Data(new
        {
            packageId = session?.PackageId ?? installed!.PackageId,
            installed = installed is null ? null : new
            {
                name = installed.Name,
                version = installed.Version,
                enabled = installed.IsEnabled,
                installedAtUtc = installed.InstalledAtUtc,
                statusMessage = installed.StatusMessage,
                source = installed.Provenance.SourceKind.ToString().ToLowerInvariant(),
                versionPolicy = installed.Provenance.VersionPolicy.ToString().ToLowerInvariant(),
                registryOrigin = installed.Provenance.RegistryOrigin,
                tag = installed.Provenance.RequestedTag,
                requestedVersion = installed.Provenance.RequestedVersion,
            },
            session = session is null ? null : new
            {
                displayName = session.DisplayName,
                version = session.Version,
                enabled = session.IsEnabled,
                active,
                readiness = session.Readiness.ToString().ToLowerInvariant(),
                failureOrigin = session.FailureOrigin?.ToString().ToLowerInvariant(),
                lastError = session.LastError,
                lastFailureAtUtc = session.LastFailureAtUtc,
                failureCount = session.FailureCount,
            },
            runtime = new
            {
                instanceId = snapshot.RuntimeInstanceId,
                sessionGeneration = snapshot.SessionGeneration,
                bootstrapState = snapshot.BootstrapState.ToString().ToLowerInvariant(),
            },
        });
        output.Line($"Package: {session?.PackageId ?? installed!.PackageId}");
        if (installed is not null)
        {
            output.Line($"Installed: {installed.Version} ({(installed.IsEnabled ? "enabled" : "disabled")})");
            output.Line($"Source: {installed.Provenance.SourceKind.ToString().ToLowerInvariant()}");
        }
        else
        {
            output.Line("Installed: no (session-only)");
        }
        if (session is not null)
        {
            output.Line($"Session: {session.Readiness.ToString().ToLowerInvariant()} ({(active ? "active" : "inactive")})");
            if (!string.IsNullOrWhiteSpace(session.LastError)) output.Warning(session.LastError);
        }
        else
        {
            output.Line("Session: not loaded");
        }
        return CliExitCodes.Success;
    }

    public async Task<int> ExecuteAsync(InstallRegistryPackageCommand command, CancellationToken token)
    {
        progress.Report("Runtime is resolving and applying the package transaction...");
        var result = await runtime.InstallRegistryPackageAsync(
            new(options.RequireRegistryApiUrl().AbsoluteUri, command.PackageId, command.Version, command.Tag, AllowDowngrade: command.AllowDowngrade, Reinstall: command.Reinstall),
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
            new(command.RegistryOrigin, command.PackageId, command.IncludePrerelease), token).ConfigureAwait(false);
        return CliRenderers.RegistryPackageChange(output, result);
    }

    public async Task<int> ExecuteAsync(AdoptPackageSourceCommand command, CancellationToken token)
    {
        progress.Report(command.DryRun
            ? $"Runtime is resolving source adoption for '{command.PackageId}' without applying it..."
            : $"Runtime is adopting the Registry source for '{command.PackageId}'...");
        var result = await runtime.AdoptRegistryPackageSourceAsync(
            new RuntimeRegistrySourceAdoptionRequest(
                command.PackageId,
                command.RegistryOrigin,
                command.Tag,
                command.Version,
                command.IncludePrerelease,
                command.AllowDowngrade,
                Confirm: !command.DryRun,
                DryRun: command.DryRun),
            token).ConfigureAwait(false);
        return CliRenderers.RegistryPackageChange(output, result);
    }

    public async Task<int> ExecuteAsync(SetPackageEnabledCommand command, CancellationToken token)
    {
        progress.Report($"{(command.Enabled ? "Enabling" : "Disabling")} package '{command.PackageId}'...");
        var result = await runtime.SetPackageEnabledAsync(
            command.PackageId,
            command.Enabled,
            token).ConfigureAwait(false);
        return CliRenderers.PackageOperation(output, result);
    }

    public async Task<int> ExecuteAsync(UninstallPackageCommand command, CancellationToken token)
    {
        var plan = await runtime.GetPackageUninstallPlanAsync(command.PackageId, token).ConfigureAwait(false);
        output.Data(new
        {
            packageId = plan.PackageId,
            directRemovals = plan.DirectRemovals.Select(ToPlanPackage).ToArray(),
            cascadingRemovals = plan.CascadingRemovals.Select(ToPlanPackage).ToArray(),
            expectedRemovalPackageIds = plan.ExpectedRemovalPackageIds,
            reloadImpact = plan.ReloadImpact,
            dataBehavior = plan.DataBehavior.ToString(),
            retainedDataPackageIds = plan.RetainedDataPackageIds,
            dryRun = command.DryRun,
        });
        output.Line($"Uninstall plan for '{plan.PackageId}':");
        foreach (var package in plan.DirectRemovals)
            output.Line($"  remove: {package.PackageId} {package.Version}");
        foreach (var package in plan.CascadingRemovals)
            output.Line($"  cascade: {package.PackageId} {package.Version}");
        output.Line($"  package data: {plan.DataBehavior.ToString().ToLowerInvariant()}");

        if (command.DryRun)
            return CliExitCodes.Success;
        if (plan.RequiresCascadeConsent && !command.AllowCascade)
        {
            output.Error(
                "The uninstall plan includes dependent packages. Review with '--dry-run', then rerun with '--yes --cascade'.",
                "runtime.package.uninstall.cascade_required");
            return CliExitCodes.Conflict;
        }

        progress.Report("Applying the exact Runtime-generated uninstall plan...");
        var result = await runtime.UninstallPackageAsync(
            command.PackageId,
            new PackageUninstallRequest(command.AllowCascade, plan.ConfirmationToken),
            token).ConfigureAwait(false);
        return CliRenderers.PackageOperation(output, result);
    }

    private static object ToPlanPackage(PackageUninstallPlanPackage package) => new
    {
        packageId = package.PackageId,
        name = package.Name,
        version = package.Version,
    };

    private static bool Matches(string first, string second)
        => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
