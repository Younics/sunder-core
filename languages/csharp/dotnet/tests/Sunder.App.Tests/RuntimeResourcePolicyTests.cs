using System.Text;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimeResourcePolicyTests
{
    [Fact]
    public void BuiltInProfilesAndProtocolCeilingsHaveFixedValues()
    {
        AssertPolicy(
            RuntimeResourcePolicies.ProtocolCeilings,
            64 * 1024 * 1024,
            64,
            8_388_608,
            131_072,
            8 * 1024 * 1024,
            512 * 1024 * 1024);
        AssertPolicy(
            RuntimeResourcePolicies.Standard,
            32 * 1024 * 1024,
            64,
            4_194_304,
            65_536,
            4 * 1024 * 1024,
            256 * 1024 * 1024);
        AssertPolicy(
            RuntimeResourcePolicies.Large,
            64 * 1024 * 1024,
            64,
            8_388_608,
            131_072,
            8 * 1024 * 1024,
            512 * 1024 * 1024);
    }

    [Fact]
    public void CustomPolicy_AcceptsExactRangeBoundariesAndRejectsEveryOutsideValue()
    {
        _ = RuntimeResourcePolicies.CustomMinimums;
        _ = RuntimeResourcePolicies.ProtocolCeilings;

        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(rpcValueBytes: 1024 * 1024 - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(rpcValueBytes: 64 * 1024 * 1024 + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(valueDepth: 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(valueDepth: 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(valueNodes: 16_383));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(valueNodes: 8_388_609));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(objectMembers: 255));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(objectMembers: 131_073));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(inlineDecodedBytes: 16 * 1024 - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(inlineDecodedBytes: 8 * 1024 * 1024 + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(contentReferenceBytes: 1024 * 1024 - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePolicy(contentReferenceBytes: 512 * 1024 * 1024 + 1));
    }

    [Fact]
    public void SettingsContract_RoundTripsPresetAndPreservedCustomValues()
    {
        var custom = new RuntimeResourcePolicy(
            3 * 1024 * 1024,
            23,
            123_456,
            1_234,
            80 * 1024,
            9 * 1024 * 1024);
        var desired = new RuntimeResourcePolicySelection(RuntimeResourcePolicyPreset.Large, custom);

        var json = RuntimeResourcePolicyContract.SerializeSettings(desired);
        var reloaded = RuntimeResourcePolicyContract.ParseSettings(json);

        Assert.Equal(RuntimeResourcePolicyPreset.Large, reloaded.Preset);
        Assert.Equal(custom, reloaded.CustomPolicy);
        Assert.Equal(RuntimeResourcePolicies.Large, reloaded.EffectivePolicy);
        Assert.Contains("\"version\": 1", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        Assert.Contains("\"preset\": \"large\"", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsContract_RejectsUnknownVersionShapeAndOutOfRangeValuesWithoutClamping()
    {
        var valid = Encoding.UTF8.GetString(RuntimeResourcePolicyContract.SerializeSettings(
            RuntimeResourcePolicies.DefaultSelection,
            writeIndented: false));

        Assert.Throws<InvalidDataException>(() => RuntimeResourcePolicyContract.ParseSettings(
            Encoding.UTF8.GetBytes(valid.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => RuntimeResourcePolicyContract.ParseSettings(
            Encoding.UTF8.GetBytes(valid.Replace(
                "\"rpcValueBytes\":33554432",
                "\"rpcValueBytes\":67108865",
                StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => RuntimeResourcePolicyContract.ParseSettings(
            Encoding.UTF8.GetBytes(valid[..^1] + ",\"unexpected\":true}")));
    }

    [Fact]
    public void EffectiveHashAndLaunchPayloadAreDeterministicCanonicalAndSelfValidating()
    {
        var firstHash = RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Standard);
        var secondHash = RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Standard);
        var largeHash = RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Large);
        var json = RuntimeResourcePolicyContract.SerializeLaunchPayload(RuntimeResourcePolicies.Standard);
        var payload = RuntimeResourcePolicyContract.ParseLaunchPayload(Encoding.UTF8.GetBytes(json));

        Assert.Equal(firstHash, secondHash);
        Assert.NotEqual(firstHash, largeHash);
        Assert.Matches("^[0-9a-f]{64}$", firstHash);
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain("  ", json, StringComparison.Ordinal);
        Assert.Equal(RuntimeResourcePolicyContract.LaunchPayloadVersion, payload.Version);
        Assert.Equal(firstHash, payload.EffectiveProfileHash);
        Assert.Equal(RuntimeResourcePolicies.Standard, payload.EffectivePolicy);

        var tampered = json.Replace(firstHash, largeHash, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => RuntimeResourcePolicyContract.ParseLaunchPayload(
            Encoding.UTF8.GetBytes(tampered)));
    }

    private static RuntimeResourcePolicy CreatePolicy(
        int rpcValueBytes = 8 * 1024 * 1024,
        int valueDepth = 64,
        int valueNodes = 524_288,
        int objectMembers = 4_096,
        int inlineDecodedBytes = 512 * 1024,
        int contentReferenceBytes = 64 * 1024 * 1024)
        => new(
            rpcValueBytes,
            valueDepth,
            valueNodes,
            objectMembers,
            inlineDecodedBytes,
            contentReferenceBytes);

    private static void AssertPolicy(
        RuntimeResourcePolicy policy,
        int rpcValueBytes,
        int valueDepth,
        int valueNodes,
        int objectMembers,
        int inlineDecodedBytes,
        int contentReferenceBytes)
    {
        Assert.Equal(rpcValueBytes, policy.MaximumRpcValueBytes);
        Assert.Equal(valueDepth, policy.MaximumValueDepth);
        Assert.Equal(valueNodes, policy.MaximumValueNodes);
        Assert.Equal(objectMembers, policy.MaximumObjectMembers);
        Assert.Equal(inlineDecodedBytes, policy.MaximumInlineDecodedBytes);
        Assert.Equal(contentReferenceBytes, policy.MaximumContentReferenceBytes);
    }
}
