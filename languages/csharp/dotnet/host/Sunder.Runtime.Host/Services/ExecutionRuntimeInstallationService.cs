using Sunder.Runtime.Contracts;
using static Sunder.Runtime.Host.Services.ExecutionRuntimeManagementSupport;

namespace Sunder.Runtime.Host.Services;

internal sealed record ExecutionRuntimeInstallConsent(RuntimeExecutionRuntimeResolution Resolution, bool RetainUntilUninstall);

internal sealed class ExecutionRuntimeInstallationService(
    OfficialRuntimeResolver resolver,
    ExecutionRuntimeStore store,
    ExecutionRuntimeArtifactDownloader downloader,
    ExecutionRuntimeManagementState state,
    ExecutionRuntimeManagementPlans plans,
    TimeProvider clock,
    RuntimeOperationGate operations,
    RuntimeEventStreamService? events = null)
{
    internal async Task<RuntimeExecutionRuntimeResolution> ResolveAsync(
        RuntimeExecutionRuntimeRequirement requirement,
        CancellationToken token,
        RuntimeExecutionRuntimeIdentity? current = null,
        string? expectedFingerprint = null)
    {
        ValidateRequirement(requirement);
        if (current is not null) ValidateIdentity(current);
        OfficialRuntimePlatformProbe.ValidateCurrentPlatform(Rid, OfficialRuntimeSourcePolicy.Source(requirement.RuntimeId, requirement.ModuleAbi));

        var selected = await resolver.ResolveAsync(requirement, Rid, token).ConfigureAwait(false);
        var fingerprint = SelectionFingerprint(requirement, selected, current);
        if (expectedFingerprint is not null && expectedFingerprint != fingerprint) throw Stale();

        var alreadyInstalled = await IsHealthyAsync(selected, token).ConfigureAwait(false);
        var resolution = new RuntimeExecutionRuntimeResolution(
            requirement,
            selected,
            fingerprint,
            clock.GetUtcNow().AddMinutes(5),
            alreadyInstalled,
            alreadyInstalled ? 0 : selected.Distribution.DownloadSize);
        ValidateResolution(resolution);
        return resolution;
    }

    internal async Task<bool> IsHealthyAsync(RuntimeExecutionRuntimeVendorArtifact artifact, CancellationToken token)
    {
        ValidateArtifact(artifact);
        var identity = Identity(artifact.Identity);
        var receipt = await store.GetReceiptAsync(identity, token).ConfigureAwait(false);
        return receipt is not null
                && receipt.Identity == identity
                && receipt.InstalledArtifact.Provenance.Distribution.MaterializationProfile == artifact.Distribution.MaterializationProfile
                && (await store.VerifyAsync(identity, deep: false, token).ConfigureAwait(false)).Healthy;
    }

    public async Task<RuntimeExecutionRuntimeInstallPlan> PreviewAsync(
        RuntimeExecutionRuntimeInstallPreviewRequest request,
        CancellationToken token)
    {
        try
        {
            var resolution = await ResolveAsync(request.Requirement, token).ConfigureAwait(false);
            return CreatePlan(resolution, request.RetainUntilUninstall);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(false, ErrorCode(error), error.Message, null, null, request.RetainUntilUninstall);
        }
    }

    internal RuntimeExecutionRuntimeInstallPlan CreatePlan(RuntimeExecutionRuntimeResolution resolution, bool retain = true)
        => new(true, null, "Download and install this official runtime before continuing.",
            plans.Add(new ExecutionRuntimeInstallConsent(resolution, retain), resolution.ExpiresAtUtc), resolution, retain);

    internal async Task ReconfirmAsync(
        RuntimeExecutionRuntimeResolution value,
        CancellationToken token,
        RuntimeExecutionRuntimeIdentity? current = null)
    {
        var confirmation = await ResolveAsync(value.Requirement, token, current, value.ResolutionFingerprint).ConfigureAwait(false);
        if (confirmation.Artifact.Identity != value.Artifact.Identity
            || !Equals(OfficialRuntimeSourcePolicy.Selection(confirmation.Artifact), OfficialRuntimeSourcePolicy.Selection(value.Artifact)))
            throw Stale();
    }

    internal async Task<ExecutionRuntimeEnsureResult> AcquireAsync(
        RuntimeExecutionRuntimeResolution value,
        CancellationToken token,
        RuntimeExecutionRuntimeIdentity? current = null)
    {
        try
        {
            await ReconfirmAsync(value, token, current).ConfigureAwait(false);
            var result = await downloader.DownloadAsync(value.Artifact, token).ConfigureAwait(false);
            await ReconfirmAsync(value, token, current).ConfigureAwait(false);
            return result;
        }
        finally { events?.PublishExecutionRuntimesChanged(); }
    }

    public async Task<RuntimeExecutionRuntimeOperationResult> ApplyAsync(
        RuntimeExecutionRuntimePlanConfirmation confirmation,
        CancellationToken token)
    {
        try
        {
            await using var operation = await operations.EnterAsync(token).ConfigureAwait(false);
            token = operation.CancellationToken;
            var consent = plans.Get<ExecutionRuntimeInstallConsent>(confirmation);
            _ = await state.ReadAsync(token).ConfigureAwait(false);
            await using var retention = await store.RetainAsync(new(Identity(consent.Resolution.Artifact.Identity)), token).ConfigureAwait(false);
            var acquired = await AcquireAsync(consent.Resolution, token).ConfigureAwait(false);
            if (consent.RetainUntilUninstall)
            {
                await state.RetainAsync(new(Identity(acquired.Receipt), UpdateLine(Identity(acquired.Receipt))), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            return new(true, null, "Runtime installed. Revalidate and resume the original package installation.",
                RuntimeExecutionRuntimeOperationActions.Install, null, acquired.AlreadyPresent);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(false, ErrorCode(error), error.Message, RuntimeExecutionRuntimeOperationActions.Install, null, false);
        }
    }

    public async Task<RuntimeExecutionRuntimeCatalogPage> CatalogAsync(RuntimeExecutionRuntimeCatalogRequest request, CancellationToken token)
    {
        if (request.Skip < 0 || request.Take is < 1 or > RuntimeExecutionRuntimeManagementLimits.MaximumCatalogPageSize)
            throw new RuntimeValidationException("Invalid runtime catalog page.");

        OfficialRuntimePlatformProbe.ValidateCurrentPlatform(Rid, request.SourceId);
        var catalog = await resolver.CatalogAsync(request.SourceId, Rid, request.Skip, request.Take, token).ConfigureAwait(false);
        var entries = new List<RuntimeExecutionRuntimeCatalogEntry>(catalog.Items.Count);
        foreach (var artifact in catalog.Items)
            entries.Add(new(DisplayName(artifact), artifact, await IsHealthyAsync(artifact, token).ConfigureAwait(false)));
        return new(Rid, request.Skip, request.Take, catalog.TotalCount, entries);
    }

    internal static string ErrorCode(Exception error) => error switch
    {
        RuntimeException runtime => runtime.Code,
        HttpRequestException or TimeoutException => ExecutionRuntimeErrorCodes.DownloadFailed,
        InvalidDataException => ExecutionRuntimeErrorCodes.ArtifactInvalid,
        _ => ExecutionRuntimeErrorCodes.IntegrityFailed,
    };

    private static string SelectionFingerprint(
        RuntimeExecutionRuntimeRequirement requirement,
        RuntimeExecutionRuntimeVendorArtifact artifact,
        RuntimeExecutionRuntimeIdentity? current)
        => Hash(new
        {
            Requirement = requirement,
            Current = current,
            Selection = OfficialRuntimeSourcePolicy.Selection(artifact),
        });
}
