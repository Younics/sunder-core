using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Worker.Internal;

namespace Sunder.Sdk.Worker;

/// <summary>Runs a standalone .NET worker over the Sunder worker V1 process protocol.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
public static class SunderWorker
{
    private const int MaximumDiagnosticCharacters = 4096;
    private static readonly object DiagnosticGate = new();

    /// <summary>Runs the worker over standard input and standard output until the Host shuts it down.</summary>
    /// <remarks>Standard output is reserved exclusively for worker protocol frames.</remarks>
    public static async Task RunAsync(
        SunderWorkerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            await RunAsync(
                options,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                Console.Error,
                Environment.GetEnvironmentVariable,
                WorkerLimits.Default,
                cancellationToken).ConfigureAwait(false);
        }
        catch (WorkerProtocolException exception)
        {
            throw new SunderWorkerProtocolException(exception.Message, exception);
        }
    }

    /// <summary>Writes one sanitized, bounded diagnostic line to standard error.</summary>
    /// <remarks>Diagnostics must not contain credentials, secrets, request content, or other sensitive data.</remarks>
    public static void WriteDiagnostic(string message, Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        WorkerDiagnosticSink.Write(
            Console.Error,
            DiagnosticGate,
            MaximumDiagnosticCharacters,
            message,
            exception);
    }

    internal static Task RunAsync(
        SunderWorkerOptions options,
        Stream input,
        Stream output,
        TextWriter diagnostics,
        Func<string, string?> getEnvironmentVariable,
        WorkerLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(limits);
        return new SunderWorkerRuntime(
            _ => WorkerRuntimeConfiguration.FromV1(options),
            WorkerProtocol.V1,
            input,
            output,
            new WorkerDiagnosticSink(diagnostics, limits.MaximumDiagnosticCharacters),
            WorkerEnvironment.Capture(getEnvironmentVariable, WorkerProtocol.V1),
            limits.Validate()).RunAsync(cancellationToken);
    }
}
