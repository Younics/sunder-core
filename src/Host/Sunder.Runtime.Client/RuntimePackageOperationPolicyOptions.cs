namespace Sunder.Runtime.Client;

public sealed record RuntimePackageOperationPolicyOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan StreamLifetimeTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxRequestBytes { get; init; } = 1024 * 1024;
    public int MaxResponseBytes { get; init; } = 4 * 1024 * 1024;
    public int MaxStreamRecordBytes { get; init; } = 1024 * 1024 + 16 * 1024;
}

public sealed class RuntimePackageStreamException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
