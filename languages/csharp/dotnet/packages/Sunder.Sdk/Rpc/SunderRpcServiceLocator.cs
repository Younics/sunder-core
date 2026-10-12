using System.Text.Json;
using Sunder.Sdk.Packaging;

namespace Sunder.Sdk.Rpc;

internal static class SunderRpcServiceLocator
{
    private static readonly HashSet<string> Members = new(StringComparer.Ordinal)
    {
        "packageId", "packageVersion", "providerId", "contractId", "contractVersion", "descriptorSha256",
        "abiSha256", "activationId", "activationEpoch", "sessionGeneration", "endpoint",
    };

    internal static void Validate(SunderRpcProviderSnapshot snapshot)
    {
        if (!PackageId.TryParse(snapshot.PackageId, out _) || !SemanticVersion.TryParse(snapshot.PackageVersion, out _)
            || !PackageId.TryParse(snapshot.ProviderId, out _) || !PackageId.TryParse(snapshot.ContractId, out _)
            || !SemanticVersion.TryParse(snapshot.ContractVersion, out _) || !IsHash(snapshot.DescriptorSha256)
            || !IsHash(snapshot.AbiSha256) || snapshot.ActivationId == Guid.Empty
            || snapshot.ActivationEpoch is < 0 or > 9_007_199_254_740_991
            || snapshot.SessionGeneration is < 0 or > 9_007_199_254_740_991 || snapshot.Endpoint is null)
            throw new JsonException("A service reference requires a complete, valid exact provider locator.");
    }

    private static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool SameIdentity(SunderRpcProviderSnapshot left, SunderRpcProviderSnapshot right)
        => left.PackageId == right.PackageId && left.PackageVersion == right.PackageVersion
            && left.ProviderId == right.ProviderId && left.ContractId == right.ContractId
            && left.ContractVersion == right.ContractVersion && left.DescriptorSha256 == right.DescriptorSha256
            && left.AbiSha256 == right.AbiSha256 && left.ActivationId == right.ActivationId
            && left.ActivationEpoch == right.ActivationEpoch && left.SessionGeneration == right.SessionGeneration
            && left.Endpoint == right.Endpoint;

    internal static async ValueTask<SunderRpcProviderSnapshot> ResolveAsync(ISunderRpcClient receiver,
        SunderRpcProviderSnapshot locator, CancellationToken cancellationToken)
    {
        var current = await receiver.GetProviderAsync(locator.Endpoint, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (current is null || current.State != SunderRpcProviderState.Active || !SameIdentity(locator, current))
            throw Stale();
        Validate(current);
        return current;
    }

    internal static SunderRpcException Stale()
        => new(new(SunderRpcErrorKind.StaleEndpoint, "rpc.stale-endpoint",
            "The referenced service activation is not visible or is no longer current."));

    internal static SunderRpcProviderSnapshot Read(JsonElement value, JsonSerializerOptions options)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("A service reference must be an object.");
        var encountered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.EnumerateObject())
            if (!Members.Contains(member.Name) || !encountered.Add(member.Name))
                throw new JsonException("A service reference has an unknown or duplicate member.");
        if (encountered.Count != Members.Count) throw new JsonException("A service reference is missing a locator member.");
        var endpoint = Text(value, "endpoint");
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 256)
            throw new JsonException("A service reference has an invalid endpoint.");
        var snapshot = new SunderRpcProviderSnapshot(Text(value, "packageId"), Text(value, "packageVersion"),
            Text(value, "providerId"), Text(value, "contractId"), Text(value, "contractVersion"),
            Text(value, "descriptorSha256"), Text(value, "abiSha256"),
            value.GetProperty("activationId").Deserialize<Guid>(options),
            value.GetProperty("activationEpoch").Deserialize<long>(options),
            value.GetProperty("sessionGeneration").Deserialize<long>(options),
            new(endpoint), 0, SunderRpcProviderState.Active);
        Validate(snapshot);
        return snapshot;
    }

    internal static void Write(Utf8JsonWriter writer, SunderRpcProviderSnapshot value)
    {
        Validate(value);
        writer.WriteStartObject();
        writer.WriteString("packageId", value.PackageId);
        writer.WriteString("packageVersion", value.PackageVersion);
        writer.WriteString("providerId", value.ProviderId);
        writer.WriteString("contractId", value.ContractId);
        writer.WriteString("contractVersion", value.ContractVersion);
        writer.WriteString("descriptorSha256", value.DescriptorSha256);
        writer.WriteString("abiSha256", value.AbiSha256);
        writer.WriteString("activationId", value.ActivationId.ToString("D"));
        writer.WriteNumber("activationEpoch", value.ActivationEpoch);
        writer.WriteNumber("sessionGeneration", value.SessionGeneration);
        writer.WriteString("endpoint", value.Endpoint.Value);
        writer.WriteEndObject();
    }

    private static string Text(JsonElement value, string member)
        => value.GetProperty(member) is { ValueKind: JsonValueKind.String } text ? text.GetString()!
            : throw new JsonException($"Service reference member '{member}' must be a string.");
}
