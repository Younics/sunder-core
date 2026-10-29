using System.IO.Pipes;
using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal sealed class RuntimeStartupDiagnosticReporter
{
    internal const string EnvironmentVariable = "SUNDER_RUNTIME_STARTUP_DIAGNOSTIC_PIPE";
    internal const int MaximumPayloadBytes = 4096;
    private readonly object _gate = new();
    private AnonymousPipeClientStream? _pipe;

    private RuntimeStartupDiagnosticReporter(string handle)
    {
        _pipe = new AnonymousPipeClientStream(PipeDirection.Out, handle);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    internal static RuntimeStartupDiagnosticReporter? StartFromEnvironment()
    {
        var handle = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Environment.SetEnvironmentVariable(EnvironmentVariable, null);
        return string.IsNullOrWhiteSpace(handle) ? null : new RuntimeStartupDiagnosticReporter(handle);
    }

    internal void Complete()
    {
        lock (_gate)
        {
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
            _pipe?.Dispose();
            _pipe = null;
        }
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is not Exception exception)
        {
            return;
        }

        lock (_gate)
        {
            if (_pipe is null)
            {
                return;
            }

            try
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(CreateDiagnostic(exception));
                if (payload.Length <= MaximumPayloadBytes)
                {
                    _pipe.Write(payload);
                    _pipe.Flush();
                }
            }
            catch
            {
            }
            finally
            {
                _pipe.Dispose();
                _pipe = null;
            }
        }
    }

    internal static RuntimeStartupDiagnostic CreateDiagnostic(Exception exception)
        => new(exception is PackageStorageActivationRecoveryException
                ? PackageStorageActivationRecoveryException.Code
                : "runtime.v1.internal",
            exception.Message.Length > 2048 ? exception.Message[..2048] : exception.Message);
}
