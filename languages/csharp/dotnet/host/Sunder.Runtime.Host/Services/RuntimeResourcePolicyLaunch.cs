using System.Text;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

internal static class RuntimeResourcePolicyLaunch
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static RuntimeResourcePolicyLaunchPayload ReadAndClearEnvironment()
    {
        var serialized = Environment.GetEnvironmentVariable(
            RuntimeResourcePolicyEnvironment.LaunchPayloadVariable);
        Environment.SetEnvironmentVariable(
            RuntimeResourcePolicyEnvironment.LaunchPayloadVariable,
            null);

        if (serialized is null)
        {
            return RuntimeResourcePolicyContract.CreateLaunchPayload(
                RuntimeResourcePolicies.Standard);
        }
        if (serialized.Length > RuntimeResourcePolicyContract.MaximumLaunchPayloadBytes)
        {
            throw new InvalidDataException(
                $"Runtime resource policy launch payload must be between 1 and {RuntimeResourcePolicyContract.MaximumLaunchPayloadBytes} bytes.");
        }

        byte[] payload;
        try
        {
            payload = StrictUtf8.GetBytes(serialized);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Runtime resource policy launch payload is not valid UTF-8.",
                exception);
        }
        return RuntimeResourcePolicyContract.ParseLaunchPayload(payload);
    }
}
