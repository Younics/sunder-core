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
    // These tests focus on package transaction/runtime evidence. Their previously
    // installed fixtures now carry a real durable, exact-subject execution lease.
    private static async Task SeedExistingActivePolicyAsync(RuntimePackagePaths paths, InstalledPackageRecord package)
    {
        var clock = new PolicyContinuousClock();
        var cache = new PackageLifecyclePolicyCache(paths, clock);
        var origin = package.Provenance.RegistryOrigin ?? package.ExecutionRuntimeBindings.First().RegistryOrigin;
        var subjects = new List<PackagePolicySubjects>
        {
            new(RegistryActivePolicyClient.Origin(origin), [new(package.PackageId, package.Version,
                package.Provenance.SourceKind == InstalledPackageSourceKind.Registry ? package.Provenance.SourceIdentity! : package.ContentIdentity)], [], []),
        };
        foreach (var binding in package.ExecutionRuntimeBindings)
            subjects.Add(new(RegistryActivePolicyClient.Origin(binding.RegistryOrigin), [], [], [PackagePolicyAdmission.RuntimeSubject(RuntimePackageExecutionRuntimeBinding.FromInstalled(binding))]));
        foreach (var group in subjects.GroupBy(value => value.Origin, StringComparer.Ordinal))
        {
            var request = await cache.CreateRequestAsync(group.Key, group.SelectMany(value => value.Packages).Distinct().OrderBy(RegistryActivePolicyClient.PackageKey, StringComparer.Ordinal).ToArray(), [],
                group.SelectMany(value => value.Runtimes).Distinct().OrderBy(RegistryActivePolicyClient.RuntimeKey, StringComparer.Ordinal).ToArray(), default);
            request = request with { Nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) };
            var observedAt = package.ExecutionRuntimeBindings.Min(value => value.PolicyObservedAtUtc);
            var response = ActivePolicyTestContext.Allowed(request) with
            { RegistryOrigin = group.Key, ObservedAtUtc = observedAt, ValidUntilUtc = observedAt.AddHours(24) };
            var now = clock.Read();
            await cache.ObserveAsync(new(request, response, now, now,
                Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(response, RegistryActivePolicyClient.Json))), cache.ClockGeneration(group.Key)));
        }
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
                var response = ActivePolicyTestContext.Allowed(subjects, plan?.RuntimePolicyRevision ?? 1);
                var observedAt = plan?.ExpiresAtUtc > DateTimeOffset.MinValue.AddMinutes(5) ? plan.ExpiresAtUtc.AddMinutes(-5) : DateTimeOffset.UtcNow;
                response = response with
                {
                    RegistryOrigin = origin,
                    ObservedAtUtc = observedAt,
                    ValidUntilUtc = observedAt.AddHours(24),
                    PackagePolicy = plan?.PackagePolicy ?? new(1, ActivePolicyTestContext.Hash("package-policy-1")),
                    Runtimes = response.Runtimes.Select(value =>
                    {
                        var artifact = plan?.RuntimeArtifacts.SingleOrDefault(artifact => artifact.Identity == value.Subject.Runtime);
                        return artifact is null ? value : value with
                        { LifecycleState = artifact.Lifecycle.State, ActivationBlockedAtUtc = artifact.Lifecycle.ActivationBlockedAtUtc, RevokedAtUtc = artifact.Lifecycle.RevokedAtUtc };
                    }).ToArray(),
                };
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(response, options: RegistryActivePolicyClient.Json) };
            }
            var result = await send();
            if (result.Content is JsonContent { Value: RegistryResolveInstallPlanResponse { Success: true } planResult })
            {
                var runtimeRevision = Math.Max(1, planResult.RuntimePolicyRevision);
                var policy = new RegistryPolicyIdentity(1, ActivePolicyTestContext.Hash("package-policy-1"));
                var checkpoint = new RegistryPolicyCheckpointIdentity(runtimeRevision, ActivePolicyTestContext.Hash("checkpoint-" + runtimeRevision), 1, ActivePolicyTestContext.Hash("topology"));
                var updated = planResult with
                {
                    RuntimePolicyRevision = runtimeRevision,
                    PackagePolicy = policy,
                    PolicyCheckpoint = checkpoint,
                    ResolutionPolicy = new(policy, checkpoint, [], [], [], []),
                };
                _plans[origin] = updated;
                result.Content.Dispose();
                result.Content = JsonContent.Create(updated);
            }
            return result;
        }
    }
}
