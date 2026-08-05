using Sunder.Sdk.Compatibility;
using Sunder.Sdk.Worker.Internal;

namespace Sunder.Sdk.Worker;

/// <summary>Runs a native standalone .NET worker over the clean-break Sunder worker V2 process protocol.</summary>
[SunderSdkCapability(SunderSdkCapabilities.RpcV1)]
[SunderSdkCapability(SunderWorkerCapabilities.ProtocolV2)]
public static class SunderWorkerV2
{
    /// <summary>Configures and runs the worker until the Host shuts it down.</summary>
    /// <remarks>Configuration runs once before the immutable contribution catalog is advertised. Standard output is reserved exclusively for worker protocol frames.</remarks>
    public static async Task RunAsync(
        Func<SunderWorkerContext, SunderWorkerV2Options> configure,
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

    internal static Task RunAsync(
        Func<SunderWorkerContext, SunderWorkerV2Options> configure,
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
                              ?? throw new InvalidOperationException("Worker V2 configuration returned null options.");
                return WorkerRuntimeConfiguration.FromV2(options, context);
            },
            WorkerProtocol.V2,
            input,
            output,
            new WorkerDiagnosticSink(diagnostics, limits.MaximumDiagnosticCharacters),
            WorkerEnvironment.Capture(getEnvironmentVariable, WorkerProtocol.V2),
            limits.Validate()).RunAsync(cancellationToken);
    }
}
