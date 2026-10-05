namespace Sunder.Sdk.Worker.Internal;

internal static class WorkerProtocol
{
    public const string Name = "sunder.worker.v1";
    public const int Version = 1;
    public const long MaximumSafeInteger = 9_007_199_254_740_991;
    public const int EnvelopeReserveBytes = 64 * 1024;
}

internal sealed class WorkerProtocolException : Exception
{
    public WorkerProtocolException(string message)
        : base(message)
    {
    }

    public WorkerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class WorkerTransportResourceException : Exception
{
    public WorkerTransportResourceException(string message)
        : base(message)
    {
    }

    public WorkerTransportResourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
