namespace Sunder.Runtime.Client;

internal sealed record RuntimeV1ResetPolicyOptions
{
    public TimeSpan LeaseRetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan CredentialCommandTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
