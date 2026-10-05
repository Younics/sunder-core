using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Worker.Internal;

namespace Sunder.Sdk.Worker;

/// <summary>Runs a standalone .NET worker over the canonical Sunder Worker V1 process protocol.</summary>
public static class SunderWorker
{
    private const int MaximumDiagnosticCharacters = 4096;
    private static readonly object DiagnosticGate = new();

    /// <summary>Configures and runs a canonical Worker V1 activation until the Host shuts it down.</summary>
    /// <remarks>Configuration runs once before the immutable contribution catalog is advertised. Standard output is reserved exclusively for worker protocol frames.</remarks>
    [SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
    [SunderSdkCapability(SunderWorkerCapabilities.ProtocolV1)]
    public static async Task RunAsync(
        Func<SunderWorkerContext, SunderWorkerOptions> configure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        try
        {
            await RunAsync(
                configure,
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
        Func<SunderWorkerContext, SunderWorkerOptions> configure,
        Stream input,
        Stream output,
        TextWriter diagnostics,
        Func<string, string?> getEnvironmentVariable,
        WorkerLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(limits);
        return new SunderWorkerRuntime(
            client =>
            {
                var context = new SunderWorkerContext(client);
                var options = configure(context)
                              ?? throw new InvalidOperationException("Worker V1 configuration returned null options.");
                return WorkerRuntimeConfiguration.FromOptions(options, context);
            },
            input,
            output,
            new WorkerDiagnosticSink(diagnostics, limits.MaximumDiagnosticCharacters),
            WorkerEnvironment.Capture(getEnvironmentVariable),
            limits.Validate()).RunAsync(cancellationToken);
    }
}
