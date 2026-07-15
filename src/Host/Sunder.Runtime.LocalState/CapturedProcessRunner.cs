using System.Diagnostics;
using System.Text;

namespace Sunder.Runtime.LocalState;

internal sealed record CapturedProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool OutputTruncated);

internal static class CapturedProcessRunner
{
    internal const int MaxCapturedCharactersPerStream = 1024 * 1024;

    public static CapturedProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var outputTruncated = 0;
        process.OutputDataReceived += (_, eventArgs) => AppendLine(standardOutput, eventArgs.Data, ref outputTruncated);
        process.ErrorDataReceived += (_, eventArgs) => AppendLine(standardError, eventArgs.Data, ref outputTruncated);
        if (!process.Start())
        {
            throw new IOException($"Could not start process '{fileName}'.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            try
            {
                if (standardInput is not null)
                {
                    process.StandardInput.Write(standardInput);
                }
            }
            finally
            {
                process.StandardInput.Close();
            }

            if (!process.WaitForExit(timeout))
            {
                StopAndDrain(process);
                return new CapturedProcessResult(-1, standardOutput.ToString(), standardError.ToString(), TimedOut: true, OutputTruncated: outputTruncated != 0);
            }
            process.WaitForExit();
            return new CapturedProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString(), TimedOut: false, OutputTruncated: outputTruncated != 0);
        }
        catch
        {
            if (!process.HasExited)
            {
                StopAndDrain(process);
            }
            throw;
        }
    }

    private static void StopAndDrain(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        try
        {
            if (process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                process.WaitForExit();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
        }
    }

    private static void AppendLine(StringBuilder target, string? line, ref int outputTruncated)
    {
        if (line is null)
        {
            return;
        }

        lock (target)
        {
            var remaining = MaxCapturedCharactersPerStream - target.Length;
            if (remaining <= 0)
            {
                Interlocked.Exchange(ref outputTruncated, 1);
                return;
            }

            var charactersToAppend = Math.Min(line.Length, Math.Max(0, remaining - Environment.NewLine.Length));
            target.Append(line, 0, charactersToAppend);
            if (charactersToAppend == line.Length && target.Length + Environment.NewLine.Length <= MaxCapturedCharactersPerStream)
            {
                target.AppendLine();
            }
            else
            {
                Interlocked.Exchange(ref outputTruncated, 1);
            }
        }
    }
}
