using System.Net.Http.Headers;
using Sunder.Package.Format;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed record ExecutionRuntimeLifecyclePolicyRefreshResult(
    int ReceiptsObserved,
    int ReleasesUnavailable,
    IReadOnlyList<ExecutionRuntimeIdentity> NewlyActivationBlocked,
    PreparedExecutionRuntimeLifecyclePolicyUpdate? PreparedUpdate = null);

internal sealed class ExecutionRuntimeLifecyclePolicyRefresher(
    RegistryHttpClient registryClient,
    ExecutionRuntimeStore store,
    ExecutionRuntimeLifecyclePolicyCache policyCache,
    TimeProvider timeProvider)
{
    public async Task<ExecutionRuntimeLifecyclePolicyRefreshResult> RefreshAsync(
        CancellationToken cancellationToken)
        => await RefreshAsync(
            identityFilter: null,
            hardDenyCodes: null,
            expectedRegistryOrigin: null,
            expectedRuntimePolicyRevision: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<ExecutionRuntimeLifecyclePolicyRefreshResult> RefreshAsync(
        IReadOnlyCollection<ExecutionRuntimeIdentity> identities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var identityFilter = identities
            .Select(static identity => identity?.SingleFlightKey
                ?? throw new ArgumentException("An execution-runtime lifecycle refresh identity cannot be null.", nameof(identities)))
            .ToHashSet(StringComparer.Ordinal);
        return await RefreshAsync(
            identityFilter,
            hardDenyCodes: null,
            expectedRegistryOrigin: null,
            expectedRuntimePolicyRevision: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionRuntimeLifecyclePolicyRefreshResult> RefreshHardDenialsAsync(
        Uri registryOrigin,
        long runtimePolicyRevision,
        IReadOnlyList<RegistryExecutionRuntimeResolutionConflict> conflicts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registryOrigin);
        ArgumentNullException.ThrowIfNull(conflicts);
        if (!registryOrigin.IsAbsoluteUri || runtimePolicyRevision <= 0 || conflicts.Count == 0)
        {
            throw new InvalidDataException("Execution-runtime hard-denial authority is incomplete.");
        }
        var origin = RegistryOrigin.Normalize(registryOrigin.AbsoluteUri).AbsoluteUri;
        var denyCodes = new Dictionary<string, string>(StringComparer.Ordinal);
        var identities = new Dictionary<string, ExecutionRuntimeIdentity>(StringComparer.Ordinal);
        foreach (var conflict in conflicts)
        {
            if (conflict?.CurrentRuntime is not { } current
                || conflict.Code is not (
                    ExecutionRuntimeErrorCodes.Revoked
                    or ExecutionRuntimeErrorCodes.Retired
                    or ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied))
            {
                throw new InvalidDataException(
                    "Registry returned malformed exact execution-runtime hard-denial evidence.");
            }
            var identity = new ExecutionRuntimeIdentity(
                current.RuntimeId,
                current.Version,
                current.PackagingRevision,
                current.Rid,
                current.ModuleAbi);
            if (denyCodes.TryGetValue(identity.SingleFlightKey, out var existingCode)
                && !string.Equals(existingCode, conflict.Code, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Registry returned conflicting hard-denial codes for one exact execution runtime.");
            }
            denyCodes[identity.SingleFlightKey] = conflict.Code;
            identities[identity.SingleFlightKey] = identity;
        }

        return await RefreshAsync(
            identities.Keys.ToHashSet(StringComparer.Ordinal),
            denyCodes,
            origin,
            runtimePolicyRevision,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExecutionRuntimeLifecyclePolicyRefreshResult> RefreshAsync(
        IReadOnlySet<string>? identityFilter,
        IReadOnlyDictionary<string, string>? hardDenyCodes,
        string? expectedRegistryOrigin,
        long? expectedRuntimePolicyRevision,
        CancellationToken cancellationToken)
    {
        await policyCache.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var previousPolicies = policyCache.CaptureSnapshot();
        var refreshStartedAtUtc = timeProvider.GetUtcNow();
        var receipts = (await store.ListReceiptsAsync(cancellationToken).ConfigureAwait(false))
            .Where(receipt => identityFilter is null || identityFilter.Contains(receipt.Identity.SingleFlightKey))
            .Select(receipt => GetAuthoritativeReceipt(receipt, previousPolicies))
            .Where(static receipt => receipt is not null)
            .Select(static receipt => receipt!)
            .ToArray();
        if (hardDenyCodes is not null
            && !receipts.Select(static receipt => receipt.Receipt.Identity.SingleFlightKey)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(hardDenyCodes.Keys))
        {
            throw new InvalidDataException(
                "Registry denied an exact execution runtime whose immutable local receipt is missing.");
        }
        var observations = new List<ExecutionRuntimeLifecyclePolicyObservation>();
        var unavailable = 0;
        foreach (var group in receipts.GroupBy(static receipt => new ReleaseKey(
                     receipt.RegistryOrigin,
                     receipt.Receipt.Identity.RuntimeId,
                     receipt.Receipt.Identity.Version,
                     receipt.Receipt.Identity.PackagingRevision)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegistryExecutionRuntimeReleaseDetails? details;
            try
            {
                var origin = RegistryOrigin.Normalize(group.Key.RegistryOrigin);
                var route = RegistryApiRoutes.ExecutionRuntimeRelease(
                    group.Key.RuntimeId,
                    group.Key.Version,
                    group.Key.PackagingRevision);
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    new Uri(origin, RegistryApiRoutes.ToHttpClientRelativePath(route)));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                using var response = await registryClient.SendAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    unavailable++;
                    continue;
                }
                details = await registryClient.ReadJsonAsync<RegistryExecutionRuntimeReleaseDetails>(
                        response,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or TimeoutException)
            {
                unavailable++;
                continue;
            }

            if (details is null)
            {
                throw new InvalidDataException(
                    $"Registry returned an empty lifecycle response for '{group.Key.RuntimeId} {group.Key.Version}'.");
            }
            ValidateRelease(details, group.Key);
            foreach (var authoritative in group)
            {
                var receipt = authoritative.Receipt;
                if (expectedRegistryOrigin is not null
                    && !string.Equals(
                        authoritative.RegistryOrigin,
                        expectedRegistryOrigin,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Registry hard-denial origin does not match receipt '{receipt.Identity.SingleFlightKey}'.");
                }
                var artifact = details.Artifacts.SingleOrDefault(item =>
                    item.Artifact?.Identity is { } identity
                    && identity == new RegistryExecutionRuntimeIdentity(
                        receipt.Identity.RuntimeId,
                        receipt.Identity.Version,
                        receipt.Identity.PackagingRevision,
                        receipt.Identity.Rid,
                        receipt.Identity.ModuleAbi));
                if (artifact is null || !MatchesReceipt(artifact, receipt))
                {
                    throw new InvalidDataException(
                        $"Registry lifecycle response does not match immutable receipt '{receipt.Identity.SingleFlightKey}'.");
                }
                if (expectedRuntimePolicyRevision is not null
                    && artifact.Artifact.ObservedRuntimePolicyRevision != expectedRuntimePolicyRevision)
                {
                    throw new InvalidDataException(
                        $"Registry hard-denial policy revision does not match receipt '{receipt.Identity.SingleFlightKey}'.");
                }
                var denyCode = hardDenyCodes?.GetValueOrDefault(receipt.Identity.SingleFlightKey);
                if (denyCode == ExecutionRuntimeErrorCodes.Revoked
                    && artifact.Artifact.Lifecycle.State != ExecutionRuntimeLifecycleStates.Revoked
                    || denyCode == ExecutionRuntimeErrorCodes.Retired
                    && artifact.Artifact.Lifecycle.State != ExecutionRuntimeLifecycleStates.Retired)
                {
                    throw new InvalidDataException(
                        $"Registry hard-denial lifecycle does not match code '{denyCode}'.");
                }
                observations.Add(new ExecutionRuntimeLifecyclePolicyObservation(
                    receipt,
                    authoritative.RegistryOrigin,
                    artifact.Artifact.Lifecycle,
                    artifact.Artifact.ObservedRuntimePolicyRevision,
                    artifact.Artifact.MinimumHostTrustGeneration,
                    timeProvider.GetUtcNow(),
                    denyCode == ExecutionRuntimeErrorCodes.CurrentRuntimeTrustDenied
                        ? ExecutionRuntimeTrustDispositions.Denied
                        : ExecutionRuntimeTrustDispositions.Trusted));
            }
        }

        PreparedExecutionRuntimeLifecyclePolicyUpdate? preparedUpdate = null;
        ExecutionRuntimeLifecyclePolicySnapshot currentPolicies;
        if (hardDenyCodes is null)
        {
            await policyCache.ObserveAsync(observations, cancellationToken).ConfigureAwait(false);
            currentPolicies = policyCache.CaptureSnapshot();
        }
        else
        {
            preparedUpdate = await policyCache.PrepareUpdateAsync(observations, cancellationToken)
                .ConfigureAwait(false);
            currentPolicies = preparedUpdate.Authority;
        }
        var now = timeProvider.GetUtcNow();
        var newlyActivationBlocked = observations
            .Select(static observation => observation.Receipt)
            .Where(receipt => currentPolicies.TryGet(receipt.Identity, out var current)
                              && current is not null
                              && current.MatchesReceipt(receipt)
                              && !current.PermitsActivation(now)
                              && (!previousPolicies.TryGet(receipt.Identity, out var previous)
                                  || previous is null
                                  || !previous.MatchesReceipt(receipt)
                                  || previous.PermitsActivation(refreshStartedAtUtc)))
            .Select(static receipt => receipt.Identity)
            .Distinct()
            .OrderBy(static identity => identity.SingleFlightKey, StringComparer.Ordinal)
            .ToArray();
        return new ExecutionRuntimeLifecyclePolicyRefreshResult(
            observations.Count,
            unavailable,
            Array.AsReadOnly(newlyActivationBlocked),
            preparedUpdate);
    }

    private static AuthoritativeReceipt? GetAuthoritativeReceipt(
        ExecutionRuntimeReceiptV1 receipt,
        ExecutionRuntimeLifecyclePolicySnapshot policies)
    {
        var hasDirectRegistryAuthority =
            (receipt.TrustEnvironment == ExecutionRuntimeTrustEnvironment.Production
             && receipt.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Registry)
            || (receipt.TrustEnvironment == ExecutionRuntimeTrustEnvironment.Development
                && receipt.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.Development);
        if (hasDirectRegistryAuthority
            && receipt.RegistryOrigin is not null)
        {
            return new AuthoritativeReceipt(
                receipt,
                RegistryOrigin.Normalize(receipt.RegistryOrigin).AbsoluteUri);
        }
        if (receipt.TrustEnvironment == ExecutionRuntimeTrustEnvironment.Production
            && receipt.AcquisitionSource == RuntimeExecutionRuntimeAcquisitionSources.LocalImport
            && receipt.RegistryOrigin is null
            && policies.TryGet(receipt.Identity, out var policy)
            && policy is not null
            && policy.MatchesReceipt(receipt))
        {
            return new AuthoritativeReceipt(receipt, policy.RegistryOrigin);
        }
        return null;
    }

    private static void ValidateRelease(
        RegistryExecutionRuntimeReleaseDetails details,
        ReleaseKey key)
    {
        if (details.Release is null
            || details.Release.Lifecycle is null
            || details.Artifacts is null
            || details.Release.SupportedRids is null
            || !string.Equals(details.Release.RuntimeId, key.RuntimeId, StringComparison.Ordinal)
            || !string.Equals(details.Release.Version, key.Version, StringComparison.Ordinal)
            || details.Release.PackagingRevision != key.PackagingRevision
            || !string.Equals(
                details.Release.WorkerProtocol,
                SunderExecutionRuntimeFormat.WorkerProtocol,
                StringComparison.Ordinal)
            || details.Release.PublishedAtUtc == default
            || details.Release.SupportedRids.Count != details.Release.SupportedRids.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("Registry execution-runtime lifecycle response is invalid.");
        }

        var artifactRids = new HashSet<string>(StringComparer.Ordinal);
        long? policyRevision = null;
        foreach (var item in details.Artifacts)
        {
            if (item?.Artifact?.Identity is not { } identity
                || item.Artifact.Lifecycle is null
                || item.PublishedAtUtc == default
                || !string.Equals(identity.RuntimeId, key.RuntimeId, StringComparison.Ordinal)
                || !string.Equals(identity.Version, key.Version, StringComparison.Ordinal)
                || identity.PackagingRevision != key.PackagingRevision
                || !string.Equals(identity.ModuleAbi, details.Release.ModuleAbi, StringComparison.Ordinal)
                || !artifactRids.Add(identity.Rid)
                || item.Artifact.Lifecycle != details.Release.Lifecycle
                || item.Artifact.ObservedRuntimePolicyRevision <= 0
                || policyRevision is not null
                && policyRevision != item.Artifact.ObservedRuntimePolicyRevision)
            {
                throw new InvalidDataException("Registry execution-runtime artifact lifecycle response is invalid.");
            }
            policyRevision = item.Artifact.ObservedRuntimePolicyRevision;
        }
        if (!artifactRids.SetEquals(details.Release.SupportedRids))
        {
            throw new InvalidDataException(
                "Registry execution-runtime lifecycle response has inconsistent RID inventory.");
        }
    }

    private static bool MatchesReceipt(
        RegistryExecutionRuntimeArtifactDetails details,
        ExecutionRuntimeReceiptV1 receipt)
    {
        var artifact = details.Artifact;
        return artifact.Identity == new RegistryExecutionRuntimeIdentity(
                   receipt.Identity.RuntimeId,
                   receipt.Identity.Version,
                   receipt.Identity.PackagingRevision,
                   receipt.Identity.Rid,
                   receipt.Identity.ModuleAbi)
               && string.Equals(artifact.ManifestSha256, receipt.ManifestSha256, StringComparison.Ordinal)
               && string.Equals(artifact.ArtifactSha256, receipt.ArtifactSha256, StringComparison.Ordinal)
               && artifact.ArtifactSize == receipt.ArtifactSize
               && string.Equals(details.ContentIdentity, receipt.ContentIdentity, StringComparison.Ordinal)
               && string.Equals(artifact.SigningKeyId, receipt.SigningKeyId, StringComparison.Ordinal)
               && string.Equals(
                   artifact.NativeAttestationSha256,
                   receipt.NativeAttestationSha256,
                   StringComparison.Ordinal)
               && string.Equals(
                   artifact.NativeAttestationKeyId,
                   receipt.NativeAttestationKeyId,
                   StringComparison.Ordinal);
    }

    private sealed record ReleaseKey(
        string RegistryOrigin,
        string RuntimeId,
        string Version,
        int PackagingRevision);

    private sealed record AuthoritativeReceipt(
        ExecutionRuntimeReceiptV1 Receipt,
        string RegistryOrigin);
}
