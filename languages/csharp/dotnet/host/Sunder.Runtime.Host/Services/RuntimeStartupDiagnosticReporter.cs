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
                var message = exception.Message;
                if (message.Length > 2048)
                {
                    message = message[..2048];
                }
                var payload = JsonSerializer.SerializeToUtf8Bytes(
                    new RuntimeStartupDiagnostic("runtime.v1.internal", message));
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
}
