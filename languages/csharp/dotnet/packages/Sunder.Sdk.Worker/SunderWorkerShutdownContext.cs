namespace Sunder.Sdk.Worker;

/// <summary>Contains the reason for stopping a worker activation or prepared candidate.</summary>
public sealed class SunderWorkerShutdownContext
{
    internal SunderWorkerShutdownContext(string reason) => Reason = reason;

    /// <summary>Gets the bounded shutdown reason.</summary>
    public string Reason { get; }
}
