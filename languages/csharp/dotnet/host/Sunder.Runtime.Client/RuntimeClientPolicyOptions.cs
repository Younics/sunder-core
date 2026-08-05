namespace Sunder.Runtime.Client;

public sealed record RuntimeClientPolicyOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan StreamLifetimeTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public long MaxHandshakeResponseBytes { get; init; } = 64 * 1024;
    public long MaxJsonResponseBytes { get; init; } = 4 * 1024 * 1024;
    public long MaxBinaryResponseBytes { get; init; } = 16 * 1024 * 1024;
    public long MaxErrorResponseBytes { get; init; } = 64 * 1024;
    public int MaxStreamEventBytes { get; init; } = 1024 * 1024;
}
