using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed partial class RuntimeManagementClient
{
    private const string ExecutionRuntimeFeature = RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1;

    public Task<IReadOnlyList<InstalledExecutionRuntimeSummary>> GetExecutionRuntimesAsync(
        CancellationToken token = default)
        => GetRequiredAsync<IReadOnlyList<InstalledExecutionRuntimeSummary>>(
            "execution-runtimes",
            ExecutionRuntimeFeature,
            token);

    public Task<InstalledExecutionRuntimeSummary> GetExecutionRuntimeAsync(
        RuntimeExecutionRuntimeIdentity identity,
        CancellationToken token = default)
        => GetRequiredAsync<InstalledExecutionRuntimeSummary>(
            BuildExecutionRuntimePath(identity),
            ExecutionRuntimeFeature,
            token);

    public Task<IReadOnlyList<RuntimeExecutionRuntimePackageReference>> GetExecutionRuntimeReferencesAsync(
        RuntimeExecutionRuntimeIdentity identity,
        CancellationToken token = default)
        => GetRequiredAsync<IReadOnlyList<RuntimeExecutionRuntimePackageReference>>(
            BuildExecutionRuntimePath(identity) + "/references",
            ExecutionRuntimeFeature,
            token);

    public Task<RuntimeExecutionRuntimeImpact> GetExecutionRuntimeImpactAsync(
        RuntimeExecutionRuntimeIdentity identity,
        CancellationToken token = default)
        => GetRequiredAsync<RuntimeExecutionRuntimeImpact>(
            BuildExecutionRuntimePath(identity) + "/impact",
            ExecutionRuntimeFeature,
            token);

    public Task<IReadOnlyList<PackageTargetExecutionRuntimeBinding>> GetPackageExecutionRuntimeBindingsAsync(
        string packageId,
        CancellationToken token = default)
        => GetRequiredAsync<IReadOnlyList<PackageTargetExecutionRuntimeBinding>>(
            $"packages/{Uri.EscapeDataString(packageId)}/execution-runtime-bindings",
            ExecutionRuntimeFeature,
            token);

    public Task<RuntimeExecutionRuntimeOperationResult> EnsureExecutionRuntimeAsync(
        RuntimeExecutionRuntimeEnsureRequest request,
        CancellationToken token = default)
        => PostAsync<RuntimeExecutionRuntimeEnsureRequest, RuntimeExecutionRuntimeOperationResult>(
            "execution-runtimes/ensure",
            request,
            ExecutionRuntimeFeature,
            token);

    public Task<RuntimeExecutionRuntimeOperationResult> VerifyExecutionRuntimeAsync(
        RuntimeExecutionRuntimeIdentity identity,
        CancellationToken token = default)
        => PostExecutionRuntimeActionAsync(identity, "verify", token);

    public async Task<RuntimeExecutionRuntimeOperationResult> RepairExecutionRuntimeAsync(
        RuntimeExecutionRuntimeIdentity identity,
        CancellationToken token = default)
    {
        var impact = await GetExecutionRuntimeImpactAsync(identity, token).ConfigureAwait(false);
        return await RepairExecutionRuntimeAsync(identity, impact, token).ConfigureAwait(false);
    }

    public Task<RuntimeExecutionRuntimeOperationResult> RepairExecutionRuntimeAsync(
        RuntimeExecutionRuntimeIdentity identity,
        RuntimeExecutionRuntimeImpact impact,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(impact);
        return PostExecutionRuntimeActionAsync(
            identity,
            "repair",
            new RuntimeExecutionRuntimeActionRequest(impact.Fingerprint),
            token);
    }

    public async Task<RuntimeExecutionRuntimeOperationResult> CollectExecutionRuntimeGarbageAsync(
        CancellationToken token = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, CreateUri("execution-runtimes/gc"));
        request.Options.Set(
            RuntimeAuthenticatedHttpMessageHandler.RequiredFeaturesKey,
            new[] { ExecutionRuntimeFeature });
        using var response = await _transport.SendAsync(request, cancellationToken: token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<RuntimeExecutionRuntimeOperationResult>(response, token)
            .ConfigureAwait(false);
    }

    private async Task<RuntimeExecutionRuntimeOperationResult> PostExecutionRuntimeActionAsync(
        RuntimeExecutionRuntimeIdentity identity,
        string action,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            CreateUri($"{BuildExecutionRuntimePath(identity)}/{action}"));
        request.Options.Set(
            RuntimeAuthenticatedHttpMessageHandler.RequiredFeaturesKey,
            new[] { ExecutionRuntimeFeature });
        using var response = await _transport.SendAsync(request, cancellationToken: token).ConfigureAwait(false);
        return await _responses.ReadRequiredJsonAsync<RuntimeExecutionRuntimeOperationResult>(response, token)
            .ConfigureAwait(false);
    }

    private Task<RuntimeExecutionRuntimeOperationResult> PostExecutionRuntimeActionAsync(
        RuntimeExecutionRuntimeIdentity identity,
        string action,
        RuntimeExecutionRuntimeActionRequest request,
        CancellationToken token)
        => PostAsync<RuntimeExecutionRuntimeActionRequest, RuntimeExecutionRuntimeOperationResult>(
            $"{BuildExecutionRuntimePath(identity)}/{action}",
            request,
            ExecutionRuntimeFeature,
            token);

    private static string BuildExecutionRuntimePath(RuntimeExecutionRuntimeIdentity identity)
        => ExecutionRuntimeApiPath(RuntimeApiRoutes.ExecutionRuntime(identity));
}
