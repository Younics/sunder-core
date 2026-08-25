using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Sunder.Core.TestSupport;

public sealed class CapturedProcess : IAsyncDisposable
{
    private readonly ConcurrentQueue<string> _standardOutput = new();
    private readonly ConcurrentQueue<string> _standardError = new();
    private readonly SemaphoreSlim _outputChanged = new(0);
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _outputPump;
    private readonly Task _errorPump;
    private int _disposeStarted;

    private CapturedProcess(Process process)
    {
        Process = process;
        Process.EnableRaisingEvents = true;
        Process.Exited += (_, _) => _exited.TrySetResult(Process.ExitCode);
        if (Process.HasExited) _exited.TrySetResult(Process.ExitCode);
        _outputPump = PumpAsync(Process.StandardOutput, _standardOutput);
        _errorPump = PumpAsync(Process.StandardError, _standardError);
    }

    public Process Process { get; }

    public int Id => Process.Id;

    public bool HasExited => Process.HasExited;

    public Task<int> ExitTask => _exited.Task;

    public string StandardOutput => string.Join(Environment.NewLine, _standardOutput);

    public string StandardError => string.Join(Environment.NewLine, _standardError);

    public string Diagnostics
        => $"pid={Id}; exited={SafeHasExited()}; exitCode={SafeExitCode()}{Environment.NewLine}stdout:{Environment.NewLine}{StandardOutput}{Environment.NewLine}stderr:{Environment.NewLine}{StandardError}";

    public static CapturedProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute)
        {
            throw new ArgumentException("Captured processes require UseShellExecute=false.", nameof(startInfo));
        }
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException($"Could not start process '{startInfo.FileName}'.");
        return new CapturedProcess(process);
    }

    public async Task WaitForOutputAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        while (true)
        {
            var output = StandardOutput + Environment.NewLine + StandardError;
            if (predicate(output)) return;
            if (_exited.Task.IsCompleted)
            {
                throw new ProcessFixtureException("Process exited before expected output was observed.", Diagnostics);
            }
            try
            {
                await _outputChanged.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ProcessFixtureException(
                    $"Timed out after {timeout.TotalMilliseconds:F0} ms waiting for process output.",
                    Diagnostics);
            }
        }
    }

    public async Task<CapturedProcessResult> WaitForExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        int exitCode;
        try
        {
            exitCode = await _exited.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new ProcessFixtureException(
                $"Process did not exit within {timeout.TotalMilliseconds:F0} ms.",
                Diagnostics,
                exception);
        }
        var remaining = timeout - stopwatch.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            throw new ProcessFixtureException(
                "Process exited, but its redirected output did not close within the watchdog.",
                Diagnostics);
        }
        var outputDrainTimeout = remaining < TimeSpan.FromSeconds(2)
            ? remaining
            : TimeSpan.FromSeconds(2);
        try
        {
            await Task.WhenAll(_outputPump, _errorPump).WaitAsync(outputDrainTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new ProcessFixtureException(
                "Process exited, but its redirected output did not close within the watchdog.",
                Diagnostics,
                exception);
        }
        return new CapturedProcessResult(exitCode, StandardOutput, StandardError);
    }

    public async Task<CapturedProcessResult> EnsureSuccessAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var result = await WaitForExitAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ProcessFixtureException(
                $"Process exited with code {result.ExitCode}.",
                Diagnostics);
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }
            await _exited.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (!_outputPump.IsCompleted || !_errorPump.IsCompleted)
            {
                Process.StandardOutput.Dispose();
                Process.StandardError.Dispose();
            }
            await Task.WhenAll(_outputPump, _errorPump).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when (Process.HasExited)
        {
        }
        catch (TimeoutException exception)
        {
            throw new ProcessFixtureException("Process tree cleanup did not complete within its watchdog.", Diagnostics, exception);
        }
        finally
        {
            Process.Dispose();
            _outputChanged.Dispose();
        }
    }

    private async Task PumpAsync(StreamReader reader, ConcurrentQueue<string> destination)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                destination.Enqueue(line);
                _outputChanged.Release();
            }
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposeStarted) != 0)
        {
        }
        catch (IOException) when (Volatile.Read(ref _disposeStarted) != 0)
        {
        }
    }

    private bool SafeHasExited()
    {
        try { return Process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private string SafeExitCode()
    {
        try { return Process.HasExited ? Process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<running>"; }
        catch (InvalidOperationException) { return "<unavailable>"; }
    }
}

public sealed record CapturedProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput
        => StandardOutput + Environment.NewLine + StandardError;
}

public sealed class ProcessFixtureException : Exception
{
    public ProcessFixtureException(string message, string diagnostics, Exception? innerException = null)
        : base(message + Environment.NewLine + diagnostics, innerException)
        => Diagnostics = diagnostics;

    public string Diagnostics { get; }
}
