using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed class RuntimeResourcePolicyLaunchTests
{
    [Fact]
    public void MissingLaunchPayload_UsesStandardAndClearsEnvironment()
    {
        WithLaunchEnvironment(null, () =>
        {
            var launch = RuntimeResourcePolicyLaunch.ReadAndClearEnvironment();

            Assert.Equal(RuntimeResourcePolicies.Standard, launch.EffectivePolicy);
            Assert.Equal(
                RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Standard),
                launch.EffectiveProfileHash);
            Assert.Null(Environment.GetEnvironmentVariable(
                RuntimeResourcePolicyEnvironment.LaunchPayloadVariable));
        });
    }

    [Fact]
    public void ValidLaunchPayload_PreservesExactCustomValuesAndHash()
    {
        var custom = new RuntimeResourcePolicy(
            3 * 1024 * 1024,
            23,
            123_456,
            1_234,
            80 * 1024,
            9 * 1024 * 1024);
        WithLaunchEnvironment(RuntimeResourcePolicyContract.SerializeLaunchPayload(custom), () =>
        {
            var launch = RuntimeResourcePolicyLaunch.ReadAndClearEnvironment();

            Assert.Equal(custom, launch.EffectivePolicy);
            Assert.Equal(
                RuntimeResourcePolicyContract.GetEffectiveProfileHash(custom),
                launch.EffectiveProfileHash);
            Assert.Null(Environment.GetEnvironmentVariable(
                RuntimeResourcePolicyEnvironment.LaunchPayloadVariable));
        });
    }

    [Fact]
    public void InvalidLaunchPayload_ThrowsWithoutClampingAndStillClearsEnvironment()
    {
        var payload = RuntimeResourcePolicyContract.SerializeLaunchPayload(
            RuntimeResourcePolicies.Standard);
        payload = payload.Replace(
            "\"rpcValueBytes\":33554432",
            "\"rpcValueBytes\":67108865",
            StringComparison.Ordinal);

        WithLaunchEnvironment(payload, () =>
        {
            Assert.Throws<InvalidDataException>(
                RuntimeResourcePolicyLaunch.ReadAndClearEnvironment);
            Assert.Null(Environment.GetEnvironmentVariable(
                RuntimeResourcePolicyEnvironment.LaunchPayloadVariable));
        });
    }

    [Fact]
    public void EffectiveProfileHash_IsCanonicalAndStable()
    {
        const string standardHash =
            "193ef7db23f37ba14b8a95a2b3e8f95324c5aae6f2f05313351eb8636a24dc9e";

        Assert.Equal(
            standardHash,
            RuntimeResourcePolicyContract.GetEffectiveProfileHash(RuntimeResourcePolicies.Standard));
        Assert.Equal(
            standardHash,
            RuntimeResourcePolicyContract.ParseLaunchPayload(
                System.Text.Encoding.UTF8.GetBytes(
                    RuntimeResourcePolicyContract.SerializeLaunchPayload(
                        RuntimeResourcePolicies.Standard))).EffectiveProfileHash);
    }

    private static void WithLaunchEnvironment(string? value, Action action)
    {
        var variable = RuntimeResourcePolicyEnvironment.LaunchPayloadVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }
}
