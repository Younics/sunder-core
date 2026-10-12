using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.Host.Services;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class RegistryRuntimeArchitectureTests
{
    private static async Task SeedExistingActivePolicyAsync(RuntimePackagePaths paths, InstalledPackageRecord package)
    {
        if (package.Provenance.RegistryOrigin is not { } origin)
        {
            return;
        }

        var clock = new PolicyContinuousClock();
        var cache = new PackageLifecyclePolicyCache(paths, clock);
        var sourceIdentity = package.Provenance.SourceKind == InstalledPackageSourceKind.Registry
            ? package.Provenance.SourceIdentity!
            : package.ContentIdentity;
        var packageSubject = new RegistryPackageVersionIdentity(package.PackageId, package.Version, sourceIdentity);
        var normalizedOrigin = RegistryActivePolicyClient.Origin(origin);
        var request = await cache.CreateRequestAsync(normalizedOrigin, [packageSubject], [], default);
        request = request with { Nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) };
        var observedAt = package.InstalledAtUtc;
        var response = ActivePolicyTestContext.Allowed(request) with
        {
            RegistryOrigin = normalizedOrigin,
            ObservedAtUtc = observedAt,
            ValidUntilUtc = observedAt.AddHours(24),
        };
        var now = clock.Read();
        await cache.ObserveAsync(new RegistryPolicyObservation(
            request,
            response,
            now,
            now,
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(response, RegistryActivePolicyClient.Json))),
            cache.ClockGeneration(normalizedOrigin)));
    }

    private sealed class ActivePolicyRegistryFixture
    {
        private readonly Dictionary<string, RegistryResolveInstallPlanResponse> _plans = new(StringComparer.Ordinal);

        internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Func<Task<HttpResponseMessage>> send, CancellationToken token)
        {
            var origin = request.RequestUri!.GetLeftPart(UriPartial.Authority);
            if (request.RequestUri.AbsolutePath == RegistryApiRoutes.PolicySynchronization)
            {
                var subjects = (await request.Content!.ReadFromJsonAsync<RegistryPolicySynchronizationRequest>(RegistryActivePolicyClient.Json, token))!;
                _plans.TryGetValue(origin, out var plan);
                var response = ActivePolicyTestContext.Allowed(subjects, plan?.PackagePolicy?.Revision ?? 1);
                var observedAt = plan?.ExpiresAtUtc > DateTimeOffset.MinValue.AddMinutes(5)
                    ? plan.ExpiresAtUtc.AddMinutes(-5)
                    : DateTimeOffset.UtcNow;
                response = response with
                {
                    RegistryOrigin = origin,
                    ObservedAtUtc = observedAt,
                    ValidUntilUtc = observedAt.AddHours(24),
                    PackagePolicy = plan?.PackagePolicy ?? new(1, ActivePolicyTestContext.Hash("package-policy-1")),
                    Checkpoint = plan?.PolicyCheckpoint ?? new(1, ActivePolicyTestContext.Hash("checkpoint-1"), 1, ActivePolicyTestContext.Hash("topology")),
                };
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(response, options: RegistryActivePolicyClient.Json) };
            }

            var result = await send();
            if (result.Content is JsonContent { Value: RegistryResolveInstallPlanResponse { Success: true } planResult })
            {
                var policy = planResult.PackagePolicy ?? new RegistryPolicyIdentity(1, ActivePolicyTestContext.Hash("package-policy-1"));
                var checkpoint = planResult.PolicyCheckpoint ?? new RegistryPolicyCheckpointIdentity(1, ActivePolicyTestContext.Hash("checkpoint-1"), 1, ActivePolicyTestContext.Hash("topology"));
                var updated = planResult with
                {
                    PackagePolicy = policy,
                    PolicyCheckpoint = checkpoint,
                    ResolutionPolicy = planResult.ResolutionPolicy ?? new(policy, checkpoint, [], [], [], []),
                };
                _plans[origin] = updated;
                result.Content.Dispose();
                result.Content = JsonContent.Create(updated);
            }
            return result;
        }
    }
}
