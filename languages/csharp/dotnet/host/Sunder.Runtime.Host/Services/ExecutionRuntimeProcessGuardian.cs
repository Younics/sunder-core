using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Sunder.Runtime.Host.Services;

internal static class ExecutionRuntimeProcessGuardian
{
    internal const string InvocationArgument = "--execution-runtime-process-guardian-v1";

    public static bool IsInvocation(IReadOnlyList<string> arguments)
        => arguments.Count > 0
           && string.Equals(arguments[0], InvocationArgument, StringComparison.Ordinal);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Count < 6
            || !string.Equals(arguments[0], InvocationArgument, StringComparison.Ordinal)
            || !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId)
            || parentProcessId <= 0
            || !Guid.TryParseExact(arguments[2], "N", out var generation)
            || generation == Guid.Empty
            || !Path.IsPathFullyQualified(arguments[3])
            || !Path.IsPathFullyQualified(arguments[4])
            || !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out var argumentCount)
            || argumentCount < 0
            || arguments.Count != 6 + argumentCount)
        {
            return 64;
        }

        var payloadReportPath = Path.GetFullPath(arguments[3]);
        var payloadExecutable = Path.GetFullPath(arguments[4]);
        if (!File.Exists(payloadExecutable))
        {
            return 66;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            if (UnixProcessGroups.CreateSession() != Environment.ProcessId)
            {
                return 70;
            }
        }
        else if (!OperatingSystem.IsWindows())
        {
            return 69;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parentMonitor = OperatingSystem.IsWindows()
            ? Task.CompletedTask
            : MonitorParentAsync(parentProcessId, lifetime.Token);
        var hostInput = Console.OpenStandardInput();
        var hostOutput = Console.OpenStandardOutput();
        var hostError = Console.OpenStandardError();
        Process? payload = null;
        try
        {
            if (!await ReadAuthorizationAsync(
                    hostInput,
                    generation,
                    cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = payloadExecutable,
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            for (var index = 0; index < argumentCount; index++)
            {
                startInfo.ArgumentList.Add(arguments[6 + index]);
            }

            payload = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!payload.Start())
            {
                return 71;
            }
            var payloadIdentity = new SystemExecutionRuntimeProcessController().Observe(payload);
            await WritePayloadReportAsync(
                    payloadReportPath,
                    new ExecutionRuntimeGuardianPayloadReportV1(
                        ExecutionRuntimeGuardianPayloadReportV1.CurrentSchemaVersion,
                        generation,
                        payload.Id,
                        payloadIdentity.BirthIdentity),
                    cancellationToken)
                .ConfigureAwait(false);

            _ = ObserveAsync(PumpAsync(
                hostInput,
                payload.StandardInput.BaseStream,
                cancellationToken));
            var outputPump = PumpAsync(
                payload.StandardOutput.BaseStream,
                hostOutput,
                cancellationToken);
            var errorPump = PumpAsync(
                payload.StandardError.BaseStream,
                hostError,
                cancellationToken);

            await payload.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            payload.StandardInput.Close();
            if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                && UnixProcessGroups.HasMemberOtherThan(Environment.ProcessId, Environment.ProcessId))
            {
                UnixProcessGroups.Signal(Environment.ProcessId, UnixProcessGroups.SignalKill);
                Environment.FailFast("The execution-runtime payload exited while descendants remained in its process group.");
            }
            await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);
            return payload.ExitCode;
        }
        catch
        {
            if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                && (payload is { HasExited: false }
                    || UnixProcessGroups.HasMemberOtherThan(Environment.ProcessId, Environment.ProcessId)))
            {
                UnixProcessGroups.Signal(Environment.ProcessId, UnixProcessGroups.SignalKill);
                Environment.FailFast("The execution-runtime guardian could not terminate its failed process group.");
            }
            if (payload is { HasExited: false })
            {
                try
                {
                    payload.Kill(entireProcessTree: true);
                    await payload.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
            throw;
        }
        finally
        {
            lifetime.Cancel();
            await ObserveAsync(parentMonitor).ConfigureAwait(false);
            payload?.Dispose();
        }
    }

    private static async Task<bool> ReadAuthorizationAsync(
        Stream input,
        Guid generation,
        CancellationToken cancellationToken)
    {
        var expected = generation.ToString("N");
        var bytes = new List<byte>(expected.Length);
        var singleByte = new byte[1];
        while (bytes.Count <= 64)
        {
            var read = await input.ReadAsync(singleByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }
            if (singleByte[0] == (byte)'\n')
            {
                var actual = Encoding.ASCII.GetString(bytes.ToArray());
                return string.Equals(actual, expected, StringComparison.Ordinal);
            }
            bytes.Add(singleByte[0]);
        }
        return false;
    }

    private static async Task MonitorParentAsync(
        int parentProcessId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (UnixProcessGroups.GetParentProcessId() != parentProcessId)
            {
                UnixProcessGroups.Signal(Environment.ProcessId, UnixProcessGroups.SignalKill);
                return;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WritePayloadReportAsync(
        string path,
        ExecutionRuntimeGuardianPayloadReportV1 report,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        report,
                        ExecutionRuntimeStore.JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
