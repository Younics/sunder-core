using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RegistryPolicyObservation(RegistryPolicySynchronizationRequest request, RegistryPolicySynchronizationResponse response,
    PolicyClockReading started, PolicyClockReading completed, string responseSha256, long clockGeneration)
{
    private int _consumed;
    public RegistryPolicySynchronizationRequest Request { get; } = request;
    public RegistryPolicySynchronizationResponse Response { get; } = response;
    public PolicyClockReading Started { get; } = started;
    public PolicyClockReading Completed { get; } = completed;
    public string ResponseSha256 { get; } = responseSha256;
    public long ClockGeneration { get; } = clockGeneration;
    public bool TryConsume() => Interlocked.Exchange(ref _consumed, 1) == 0;
}

internal sealed partial class RegistryActivePolicyClient(RegistryHttpClient http, IPolicyContinuousClock clock,
    TimeProvider timeProvider, PackageLifecyclePolicyCache policies)
{
    internal const int MaximumBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true, NumberHandling = JsonNumberHandling.Strict, MaxDepth = 32,
    };

    public async Task<RegistryPolicyObservation> SynchronizeAsync(string registryOrigin, RegistryPolicySynchronizationRequest subjects, CancellationToken cancellationToken)
    {
        var origin = Origin(registryOrigin);
        var request = subjects with { Nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(RegistryPolicyLimits.NonceBytes)),
            Packages = subjects.Packages.OrderBy(PackageKey, StringComparer.Ordinal).ToArray(),
            PlatformCatalogs = subjects.PlatformCatalogs.OrderBy(PlatformKey, StringComparer.Ordinal).ToArray(),
            Runtimes = subjects.Runtimes.OrderBy(RuntimeKey, StringComparer.Ordinal).ToArray() };
        ValidateRequest(request);
        var started = clock.Read(); ValidateClock(started);
        var generation = policies.BeginRefresh(origin, started);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(RuntimePolicyTiming.RequestDeadlineSeconds), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(origin + "/"), RegistryApiRoutes.ToHttpClientRelativePath(RegistryApiRoutes.PolicySynchronization)))
            { Content = JsonContent.Create(request, options: Json) };
            message.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var result = await http.SendAsync(message, linked.Token);
            await http.EnsureSuccessAsync(result, linked.Token);
            Require(result.StatusCode == HttpStatusCode.OK, "Policy refresh must return a complete successful snapshot.");
            Require(string.Equals(result.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase),
                "Policy refresh must return JSON.");
            Require(result.Content.Headers.ContentLength is null or <= MaximumBytes, "Policy response exceeds its byte limit.");
            await using var stream = await result.Content.ReadAsStreamAsync(linked.Token);
            using var buffer = new MemoryStream(); var chunk = new byte[16384];
            while (true)
            {
                var count = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, MaximumBytes + 1 - buffer.Length)), linked.Token);
                if (count == 0) break;
                buffer.Write(chunk, 0, count); Require(buffer.Length <= MaximumBytes, "Policy response exceeds its byte limit.");
            }
            var bytes = buffer.ToArray();
            var response = JsonSerializer.Deserialize<RegistryPolicySynchronizationResponse>(bytes, Json) ?? throw new InvalidDataException("Policy response is empty.");
            var completed = clock.Read(); ValidateClock(completed);
            Require(started.SameEpoch(completed) && completed.Ticks >= started.Ticks, "Policy clock continuity was lost during refresh.");
            Require((Int128)completed.Ticks - started.Ticks <= (Int128)started.Frequency * RuntimePolicyTiming.RequestDeadlineSeconds, "Policy refresh exceeded its continuous-time deadline.");
            ValidateResponse(origin, request, response);
            return new(request, response, started, completed, Convert.ToHexStringLower(SHA256.HashData(bytes)), generation);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Active policy refresh exceeded its request deadline."); }
    }

    internal static void ValidateResponse(string origin, RegistryPolicySynchronizationRequest request, RegistryPolicySynchronizationResponse response)
    {
        ValidateRequest(request);
        Require(response.RegistryOrigin == origin && response.Nonce == request.Nonce && response.ObservedAtUtc.Offset == TimeSpan.Zero
            && response.ValidUntilUtc.Offset == TimeSpan.Zero && response.ValidUntilUtc - response.ObservedAtUtc == TimeSpan.FromSeconds(RuntimePolicyTiming.LeaseSeconds), "Policy origin, nonce or fixed lease interval differs.");
        Require(response.PackagePolicy.Revision > 0 && Hash(response.PackagePolicy.Fingerprint) && response.RuntimePolicyRevision > 0
            && response.Checkpoint.Sequence > 0 && Hash(response.Checkpoint.Digest) && response.Checkpoint.TopologyGeneration > 0 && Hash(response.Checkpoint.TopologyFingerprint), "Policy watermarks are incomplete.");
        Require(request.Packages.SequenceEqual(response.Packages.Select(value => value.Package)) && request.PlatformCatalogs.SequenceEqual(response.PlatformCatalogs.Select(value => value.Catalog))
            && request.Runtimes.SequenceEqual(response.Runtimes.Select(value => value.Subject)), "Policy response does not cover exactly the requested subjects.");
        Require(request.MinimumPackagePolicy is null || response.PackagePolicy.Revision >= request.MinimumPackagePolicy.Revision
            && (response.PackagePolicy.Revision != request.MinimumPackagePolicy.Revision || response.PackagePolicy.Fingerprint == request.MinimumPackagePolicy.Fingerprint), "Package policy rolled back or conflicted.");
        Require(response.RuntimePolicyRevision >= request.MinimumRuntimePolicyRevision, "Runtime policy rolled back.");
        if (request.MinimumCheckpoint is { } minimum)
            Require(response.Checkpoint.Sequence >= minimum.Sequence && response.Checkpoint.TopologyGeneration >= minimum.TopologyGeneration
                && (response.Checkpoint.Sequence != minimum.Sequence || response.Checkpoint == minimum)
                && (response.Checkpoint.TopologyGeneration != minimum.TopologyGeneration || response.Checkpoint.TopologyFingerprint == minimum.TopologyFingerprint), "Checkpoint policy rolled back or conflicted.");
        foreach (var platform in response.PlatformCatalogs) ValidatePlatform(platform, response.ObservedAtUtc);
        foreach (var package in response.Packages)
        {
            ValidatePackage(package);
            var dependencies = package.PlatformDependencies.Select(value => response.PlatformCatalogs.SingleOrDefault(platform => platform.Catalog == value)).ToArray();
            Require(dependencies.All(value => value is not null), "A package platform dependency has no complete decision.");
            if (package.PackageState == "Active" && package.VersionAvailability is "Listed" or "Yanked")
            {
                var denied = dependencies.Any(value => value!.Disposition == "Denied");
                var missing = dependencies.Any(value => value!.Disposition == "PolicyRequired");
                Require(package.Disposition == (denied ? "Denied" : missing ? "PolicyRequired" : "Allowed"), "Package permission contradicts its platform dependencies.");
            }
        }
        foreach (var runtime in response.Runtimes) ValidateRuntime(runtime, response.ObservedAtUtc);
    }

    internal static string Origin(string value)
    {
        var uri = RegistryOrigin.Normalize(value);
        Require(uri.Scheme == Uri.UriSchemeHttps && uri.AbsolutePath == "/", "Active policy requires an authenticated HTTPS Registry origin without a path.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    internal static string PackageKey(RegistryPackageVersionIdentity value) => value.PackageId + "\0" + value.Version + "\0" + value.PackageContentIdentity;
    internal static string PlatformKey(RegistryPlatformCatalogIdentity value) => value.AuthorityId + "\0" + value.ReleaseVersion + "\0" + value.CatalogSha256;
    internal static string RuntimeKey(RegistryRuntimePolicySubject value) => value.Runtime.RuntimeId + "\0" + value.Runtime.Version + "\0" + value.Runtime.PackagingRevision.ToString(CultureInfo.InvariantCulture)
        + "\0" + value.Runtime.Rid + "\0" + value.Runtime.ModuleAbi + "\0" + value.ManifestSha256 + "\0" + value.ArtifactSha256;
    internal static bool Hash(string? value) => Hex(value, 64);
    internal static bool Hex(string? value, int length) => value?.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void ValidateClock(PolicyClockReading value) => Require(value.BootSessionId is { Length: > 0 and <= 128 }
        && value.Source is { Length: > 0 and <= 128 } && value.Frequency is > 0 and <= long.MaxValue / RuntimePolicyTiming.LeaseSeconds
        && value.Ticks >= 0 && value.VirtualMachineGeneration is null or { Length: > 0 and <= 128 }, "Invalid continuous policy clock.");
    internal static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
}
