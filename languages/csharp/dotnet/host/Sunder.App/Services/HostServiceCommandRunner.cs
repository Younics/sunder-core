using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Sunder.App.Services;

internal sealed record HostServiceCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool StandardOutputTruncated,
    bool StandardErrorTruncated);

internal sealed class HostServiceCommandRunner
{
    internal const int DefaultMaxCapturedCharacters = 16 * 1024;
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly int _maxCapturedCharacters;

    public HostServiceCommandRunner(int maxCapturedCharacters = DefaultMaxCapturedCharacters)
    {
        if (maxCapturedCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCapturedCharacters),
                "Command output capture limit must be positive.");
        }

        _maxCapturedCharacters = maxCapturedCharacters;
    }

    public Task<HostServiceCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
        => RunAsync(fileName, arguments, DefaultTimeout, cancellationToken);

    public async Task<HostServiceCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Command timeout must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Could not start Host service command '{HostServiceDiagnostics.SanitizeCommandName(fileName)}'.");
        var standardOutput = ReadBoundedAsync(process.StandardOutput);
        var standardError = ReadBoundedAsync(process.StandardError);
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        BoundedText output;
        BoundedText error;
        try
        {
            await process.WaitForExitAsync(waitCancellation.Token).ConfigureAwait(false);
            var captures = await Task.WhenAll(standardOutput, standardError)
                .WaitAsync(waitCancellation.Token)
                .ConfigureAwait(false);
            output = captures[0];
            error = captures[1];
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync(process).ConfigureAwait(false);
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            await IgnoreCaptureFailureAsync(standardOutput, standardError).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"Host service command '{HostServiceDiagnostics.SanitizeCommandName(fileName)}' did not finish within {timeout.TotalSeconds:0.###} seconds.");
        }

        return new HostServiceCommandResult(
            process.ExitCode,
            HostServiceDiagnostics.SanitizeCommandOutput(output.Text, _maxCapturedCharacters),
            HostServiceDiagnostics.SanitizeCommandOutput(error.Text, _maxCapturedCharacters),
            output.Truncated,
            error.Truncated);
    }

    private async Task<BoundedText> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder(Math.Min(_maxCapturedCharacters, 4096));
        var buffer = new char[4096];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = _maxCapturedCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(remaining, read));
            }
            if (read > remaining)
            {
                truncated = true;
            }
        }

        return new BoundedText(builder.ToString(), truncated);
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or TimeoutException)
        {
        }
    }

    private static async Task IgnoreCaptureFailureAsync(params Task<BoundedText>[] captures)
    {
        try
        {
            await Task.WhenAll(captures).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or ObjectDisposedException or TimeoutException)
        {
        }
    }

    private sealed record BoundedText(string Text, bool Truncated);
}

internal static partial class HostServiceDiagnostics
{
    private const int MaxLabelCharacters = 128;
    private const int MaxDetailCharacters = 2048;
    private const int MaxDiagnosticPathCharacters = 1024;
    private const int MaxDiagnosticPaths = 8;

    public static string SanitizeLabel(string? value, string fallback)
        => Sanitize(value, MaxLabelCharacters, singleLine: true) ?? fallback;

    public static string? SanitizeOptionalLabel(string? value)
        => Sanitize(value, MaxLabelCharacters, singleLine: true);

    public static string? SanitizeDetail(string? value)
        => Sanitize(value, MaxDetailCharacters, singleLine: true);

    public static string SanitizeCommandName(string fileName)
        => SanitizeLabel(Path.GetFileName(fileName), "command");

    public static string SanitizeCommandOutput(string value, int maxCharacters)
        => Sanitize(value, maxCharacters, singleLine: false) ?? string.Empty;

    public static IReadOnlyList<string> SanitizePaths(IEnumerable<string>? paths)
    {
        if (paths is null)
        {
            return [];
        }

        return paths
            .Select(path => Sanitize(path, MaxDiagnosticPathCharacters, singleLine: true))
            .Where(static path => path is not null)
            .Take(MaxDiagnosticPaths)
            .Select(static path => path!)
            .ToArray();
    }

    private static string? Sanitize(string? value, int maxCharacters, bool singleLine)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var inspectionLimit = maxCharacters > int.MaxValue / 2
            ? int.MaxValue
            : maxCharacters * 2;
        var inputLimit = Math.Min(value.Length, inspectionLimit);
        var builder = new StringBuilder(inputLimit);
        var previousWasWhitespace = false;
        foreach (var character in value.AsSpan(0, inputLimit))
        {
            if (char.IsControl(character) && character is not '\r' and not '\n' and not '\t')
            {
                continue;
            }

            if (singleLine && char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        var sanitized = SensitiveAssignmentRegex().Replace(
            builder.ToString(),
            static match => $"{match.Groups["name"].Value}{match.Groups["closing"].Value}{match.Groups["separator"].Value}[redacted]");
        sanitized = BearerValueRegex().Replace(sanitized, "Bearer [redacted]").Trim();
        if (sanitized.Length > maxCharacters)
        {
            sanitized = sanitized[..maxCharacters];
        }
        return sanitized.Length == 0 ? null : sanitized;
    }

    [GeneratedRegex(
        """(?i)(?<name>\b(?:SUNDER_[A-Z0-9_]*(?:TOKEN|SECRET|PASSWORD|KEY)|access[_-]?token|refresh[_-]?token|bearer[_-]?token|client[_-]?secret|password|secret|token|api[_-]?key))(?<closing>["']?)(?<separator>\s*[:=]\s*)(?:"[^"]*"|'[^']*'|[^\s,;]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentRegex();

    [GeneratedRegex(@"(?i)\bBearer\s+[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerValueRegex();
}
