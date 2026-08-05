namespace Sunder.Sdk.Worker;

/// <summary>Represents a fatal worker protocol or transport violation.</summary>
public sealed class SunderWorkerProtocolException : Exception
{
    /// <summary>Creates a worker protocol exception.</summary>
    public SunderWorkerProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a worker protocol exception with its underlying cause.</summary>
    public SunderWorkerProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
