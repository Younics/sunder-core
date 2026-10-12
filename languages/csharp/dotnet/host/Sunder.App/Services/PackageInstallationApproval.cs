using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;

namespace Sunder.App.Services;

internal sealed class PackageInstallationApproval(RuntimePackageRuntimePrerequisitePlan reviewed)
{
    public RuntimePackageRuntimePrerequisitePlan Reviewed { get; } = reviewed;
    public bool NeedsRuntimeInstallation => Reviewed.Requirements.Any(item => !item.Satisfied);

    public void Validate(RuntimePackageRuntimePrerequisitePlan current, bool requireReady = false)
    {
        if (!current.Success || current.SourceFingerprint != Reviewed.SourceFingerprint || current.Rid != Reviewed.Rid
            || RuntimeSemantics(current) != RuntimeSemantics(Reviewed))
            throw Changed();
        if (requireReady && (!current.Satisfied || current.Requirements.Any(item => !item.Satisfied)))
            throw new InvalidOperationException("A required runtime is not ready. Review and queue the installation again; the package was not changed.");
    }

    public async Task InstallRuntimesAsync(IRuntimePackageRuntimePrerequisiteClient client,
        Func<CancellationToken, Task<RuntimePackageRuntimePrerequisitePlan>> refresh,
        BackgroundProcessContext context)
    {
        var current = await refresh(context.CancellationToken).ConfigureAwait(false);
        Validate(current);
        foreach (var installation in current.Requirements.Where(item => !item.Satisfied)
                     .Select(item => item.Installation).OfType<RuntimeExecutionRuntimeInstallPlan>()
                     .DistinctBy(item => item.Resolution?.Artifact.Identity))
        {
            if (!installation.Success || installation.Plan is null || installation.Resolution is null || !installation.RetainUntilUninstall)
                throw new InvalidOperationException(installation.Message);
            context.ReportIndeterminate($"Downloading and installing {RuntimeManagementUi.Resolution(installation.Resolution)}…");
            var result = await client.ApplyExecutionRuntimeInstallAsync(RuntimeManagementUi.Confirm(installation.Plan), context.CancellationToken).ConfigureAwait(false);
            if (!result.Success) throw new InvalidOperationException(RuntimeManagementUi.Error(result.Message, result.ErrorCode));
        }
        context.CancellationToken.ThrowIfCancellationRequested();
        Validate(await refresh(context.CancellationToken).ConfigureAwait(false), requireReady: true);
        context.ReportProgress(100, "Package runtimes are ready.");
    }

    public static InvalidOperationException Changed()
        => new("The package, runtime, source, or permissions changed after approval. Review the installation again before applying changes.");

    // Only user-approved semantics are compared. New preview handles, expiry/observation times,
    // cached/download availability and transfer sizes do not authorize different bytes.
    private static string RuntimeSemantics(RuntimePackageRuntimePrerequisitePlan plan)
        => Hash(plan.Requirements.OrderBy(item => item.Package.PackageId, StringComparer.Ordinal)
            .ThenBy(item => item.Package.Role, StringComparer.Ordinal).ThenBy(item => item.Package.Rid, StringComparer.Ordinal)
            .Select(item => new
            {
                item.Package, item.ManifestSha256, item.Requirement,
                Identity = item.Installation?.Resolution?.Artifact.Identity ?? item.InstalledRuntime,
            }).ToArray());

    public static string PackageSemantics(RuntimeRegistryResolveInstallPlanResponse plan)
        => Hash(new
        {
            Items = plan.Items.OrderBy(item => item.PackageId, StringComparer.Ordinal).Select(item => new
            {
                item.PackageId, item.CurrentVersion, item.Version, item.PackageContentIdentity, item.ActionKind,
                Dependencies = item.DependsOn.OrderBy(dependency => dependency.PackageId, StringComparer.Ordinal).ToArray(),
                Targets = item.Targets.OrderBy(target => target.Role, StringComparer.Ordinal).ThenBy(target => target.Rid, StringComparer.Ordinal).ToArray(),
                Artifacts = item.Artifacts.OrderBy(artifact => artifact.Kind, StringComparer.Ordinal).ThenBy(artifact => artifact.Rid, StringComparer.Ordinal)
                    .Select(artifact => new { artifact.Kind, artifact.Rid, artifact.Sha256, artifact.SourceArchiveSha256,
                        artifact.ManifestSha256, artifact.ProjectionContentIdentity, artifact.ProjectionFormatVersion }).ToArray(),
            }).ToArray(),
            Consents = plan.RequiredCapabilityConsents.OrderBy(item => item.PackageId, StringComparer.Ordinal)
                .Select(item => new { item.PackageId, item.PackageVersion, item.ManifestSha256, Capabilities = item.Capabilities.Order(StringComparer.Ordinal).ToArray() }).ToArray(),
        });

    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
