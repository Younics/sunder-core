using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Client;

public sealed record RuntimeClientResourceProfile
{
    public const string FeatureName = RuntimeProtocolFeatures.RuntimeResourcePolicyV1;

    public RuntimeClientResourceProfile(
        Guid runtimeInstanceId,
        string effectiveProfileHash,
        RuntimeResourcePolicy effectivePolicy)
    {
        if (runtimeInstanceId == Guid.Empty)
        {
            throw new ArgumentException("A Runtime instance id is required.", nameof(runtimeInstanceId));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveProfileHash);
        ArgumentNullException.ThrowIfNull(effectivePolicy);
        var expectedHash = RuntimeResourcePolicyContract.GetEffectiveProfileHash(effectivePolicy);
        if (!string.Equals(effectiveProfileHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Runtime resource profile hash does not match its effective policy.");
        }

        RuntimeInstanceId = runtimeInstanceId;
        EffectiveProfileHash = effectiveProfileHash;
        EffectivePolicy = effectivePolicy;
    }

    public Guid RuntimeInstanceId { get; }

    public string EffectiveProfileHash { get; }

    public RuntimeResourcePolicy EffectivePolicy { get; }
}

public sealed record RuntimeClientResourceLimits
{
    public const int EnvelopeByteOverhead = 64 * 1024;
    public const int EnvelopeDepthOverhead = 8;
    public const int MaximumPendingBrowserRequests = 64;
    public const int MaximumQueuedBrowserEvents = 32;
    public static readonly TimeSpan BrowserRequestTimeout = TimeSpan.FromMinutes(10);

    public static int MaximumProtocolEnvelopeBytes { get; } = checked(
        RuntimeResourcePolicies.ProtocolCeilings.MaximumRpcValueBytes + EnvelopeByteOverhead);

    public static RuntimeClientResourceLimits Standard { get; } = new(profile: null);

    public static RuntimeClientResourceLimits FromProfile(RuntimeClientResourceProfile profile)
        => new(profile ?? throw new ArgumentNullException(nameof(profile)));

    internal RuntimeClientResourceLimits(RuntimeClientResourceProfile? profile)
    {
        Profile = profile;
        EffectivePolicy = profile?.EffectivePolicy ?? RuntimeResourcePolicies.Standard;
        MaximumJsonEnvelopeBytes = checked(EffectivePolicy.MaximumRpcValueBytes + EnvelopeByteOverhead);
        MaximumJsonEnvelopeDepth = checked(EffectivePolicy.MaximumValueDepth + EnvelopeDepthOverhead);
        MaximumQueuedBrowserEventBytes = checked(2L * MaximumJsonEnvelopeBytes);
    }

    public RuntimeClientResourceProfile? Profile { get; }

    public RuntimeResourcePolicy EffectivePolicy { get; }

    public bool IsRuntimeReported => Profile is not null;

    public int MaximumJsonEnvelopeBytes { get; }

    public int MaximumJsonEnvelopeDepth { get; }

    public long MaximumQueuedBrowserEventBytes { get; }

    internal void EnsureRpcValue(JsonElement value, string label)
    {
        if (GetDepth(value) > EffectivePolicy.MaximumValueDepth)
        {
            throw new InvalidDataException(
                $"{label} exceeds the negotiated {EffectivePolicy.MaximumValueDepth} level RPC value depth limit.");
        }

        var options = CreateJsonOptions();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options);
        if (bytes.Length > EffectivePolicy.MaximumRpcValueBytes)
        {
            throw new InvalidDataException(
                $"{label} exceeds the negotiated {EffectivePolicy.MaximumRpcValueBytes} byte RPC value limit.");
        }
    }

    internal JsonSerializerOptions CreateJsonOptions()
        => new(JsonSerializerDefaults.Web) { MaxDepth = MaximumJsonEnvelopeDepth };

    internal static int GetDepth(JsonElement root)
    {
        var maximum = 1;
        var pending = new Stack<(JsonElement Value, int Depth)>();
        pending.Push((root, 1));
        while (pending.TryPop(out var item))
        {
            maximum = Math.Max(maximum, item.Depth);
            if (item.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in item.Value.EnumerateArray())
                {
                    pending.Push((child, checked(item.Depth + 1)));
                }
            }
            else if (item.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var child in item.Value.EnumerateObject())
                {
                    pending.Push((child.Value, checked(item.Depth + 1)));
                }
            }
        }
        return maximum;
    }
}

internal static class RuntimeClientResourceProfileParser
{
    private static readonly HashSet<string> ProfileProperties =
    [
        "effectiveProfileHash",
        "maximumRpcValueBytes",
        "maximumValueDepth",
        "maximumValueNodes",
        "maximumObjectMembers",
        "maximumInlineDecodedBytes",
        "maximumContentReferenceBytes",
    ];

    public static RuntimeClientResourceProfile? Parse(
        RuntimeHandshakeResponse handshake,
        ReadOnlySpan<byte> handshakeUtf8)
    {
        if (!handshake.SupportedFeatures.Contains(RuntimeClientResourceProfile.FeatureName, StringComparer.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(handshakeUtf8.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The Runtime handshake must be an object.");
            }

            var profileProperties = document.RootElement.EnumerateObject()
                .Where(static property => property.NameEquals("resourceProfile"))
                .ToArray();
            if (profileProperties.Length != 1
                || profileProperties[0].Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"The Runtime advertises '{RuntimeClientResourceProfile.FeatureName}' without an effective resource profile.");
            }
            var profileElement = profileProperties[0].Value;
            var properties = profileElement.EnumerateObject().ToArray();
            if (properties.Length != ProfileProperties.Count
                || !properties.Select(static property => property.Name)
                    .ToHashSet(StringComparer.Ordinal)
                    .SetEquals(ProfileProperties))
            {
                throw new InvalidDataException(
                    "The Runtime effective resource profile is not a closed policy descriptor.");
            }

            var descriptor = handshake.ResourceProfile;
            if (descriptor is null)
            {
                throw new InvalidDataException(
                    $"The Runtime advertises '{RuntimeClientResourceProfile.FeatureName}' without an effective resource profile.");
            }
            var hash = descriptor.EffectiveProfileHash;
            if (hash is not { Length: 64 }
                || hash.Any(static value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            {
                throw new InvalidDataException("The Runtime resource profile hash is invalid.");
            }

            var policy = new RuntimeResourcePolicy(
                descriptor.MaximumRpcValueBytes,
                descriptor.MaximumValueDepth,
                descriptor.MaximumValueNodes,
                descriptor.MaximumObjectMembers,
                descriptor.MaximumInlineDecodedBytes,
                descriptor.MaximumContentReferenceBytes);
            return new RuntimeClientResourceProfile(handshake.RuntimeInstanceId, hash, policy);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The Runtime resource profile is not valid JSON.", exception);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("The Runtime resource profile exceeds protocol ceilings.", exception);
        }
    }

}

internal sealed class RuntimeClientResourceProfileStore
{
    private const int MaximumEntries = 8;
    private readonly object _gate = new();
    private readonly Dictionary<RuntimeConnectionIdentity, RuntimeClientResourceProfile> _profiles = [];

    public RuntimeClientResourceProfile? Get(RuntimeConnectionInfo connection)
    {
        lock (_gate)
        {
            return _profiles.GetValueOrDefault(RuntimeConnectionIdentity.Create(connection));
        }
    }

    public void Set(RuntimeConnectionInfo connection, RuntimeClientResourceProfile? profile)
    {
        var identity = RuntimeConnectionIdentity.Create(connection);
        lock (_gate)
        {
            _profiles.Remove(identity);
            if (profile is null)
            {
                return;
            }
            if (_profiles.Count >= MaximumEntries)
            {
                _profiles.Clear();
            }
            _profiles.Add(identity, profile);
        }
    }

    public void Remove(RuntimeConnectionInfo connection)
    {
        lock (_gate)
        {
            _profiles.Remove(RuntimeConnectionIdentity.Create(connection));
        }
    }
}

internal readonly record struct RuntimeConnectionIdentity(string RuntimeUrl, string BearerToken)
{
    public static RuntimeConnectionIdentity Create(RuntimeConnectionInfo connection)
        => new(RuntimeConnectionInfo.Normalize(connection.RuntimeUrl).AbsoluteUri, connection.BearerToken);
}
