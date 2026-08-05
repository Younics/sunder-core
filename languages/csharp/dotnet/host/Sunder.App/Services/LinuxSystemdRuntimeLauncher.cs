using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Sunder.App.Services;

[SupportedOSPlatform("linux")]
internal sealed class LinuxSystemdRuntimeLauncher : RuntimePersistentLauncherBase
{
    private const string ServiceName = "sunder-host.service";
    private const string DescriptionPrefix = "Sunder current-user Host ";
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReconciliationPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly string[] UnitProperties =
    [
        "LoadState",
        "ActiveState",
        "SubState",
        "MainPID",
        "Result",
        "ExecMainCode",
        "ExecMainStatus",
        "InvocationID",
        "Transient",
        "Description",
    ];
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<HostServiceCommandResult>> _runAsync;
    private readonly IHostServiceManager _fallback;
    private readonly IDisposable? _ownedFallback;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public LinuxSystemdRuntimeLauncher(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<HostServiceCommandResult>>? runAsync = null,
        IHostServiceManager? fallback = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _runAsync = runAsync ?? RuntimeLauncherProcess.RunResultAsync;
        _fallback = fallback ?? new UnixDetachedRuntimeLauncher();
        _ownedFallback = fallback is null ? _fallback as IDisposable : null;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public override async Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HostServiceCommandResult preflight;
        try
        {
            preflight = await _runAsync(
                    "systemctl",
                    ["--user", "--no-pager", "show", "--property=Version"],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return await LaunchFallbackAsync(startInfo, replaceExisting, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }

        if (preflight.ExitCode != 0)
        {
            if (IsUserManagerUnavailable(preflight))
            {
                return await LaunchFallbackAsync(
                        startInfo,
                        replaceExisting,
                        GetDiagnostic(preflight),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            throw CreateCommandFailure("systemctl", preflight);
        }

        var existingServiceDisplaced = false;
        try
        {
            var existing = await ReadUnitStateAsync(cancellationToken).ConfigureAwait(false);
            if (existing.IsLoaded)
            {
                if (!existing.IsTransient)
                {
                    throw new InvalidOperationException(
                        $"The systemd user unit '{ServiceName}' already exists and is not a Sunder transient unit.");
                }
                if (!replaceExisting && !existing.IsSunderUnit)
                {
                    throw new InvalidOperationException(
                        $"The transient systemd user unit '{ServiceName}' is not owned by Sunder.");
                }
                if (!replaceExisting && existing.IsActiveOrStarting)
                {
                    return CreateReceipt(existing);
                }

                existingServiceDisplaced = true;
                await RunRequiredAsync(
                        "systemctl",
                        ["--user", "--no-ask-password", "stop", ServiceName],
                        cancellationToken)
                    .ConfigureAwait(false);
                await RunRequiredAsync(
                        "systemctl",
                        ["--user", "--no-ask-password", "reset-failed", ServiceName],
                        cancellationToken)
                    .ConfigureAwait(false);
                await WaitForUnitAbsentAsync(cancellationToken).ConfigureAwait(false);
            }

            var executable = RuntimeLauncherProcess.ResolveExecutable(startInfo.FileName);
            if (!Path.IsPathFullyQualified(executable)
                || string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
                || !Path.IsPathFullyQualified(startInfo.WorkingDirectory))
            {
                throw new InvalidOperationException(
                    "The Sunder Host executable and working directory must be absolute for systemd.");
            }
            var generation = Guid.NewGuid().ToString("N");
            var arguments = new List<string>
        {
            "--user",
            "--quiet",
            "--no-ask-password",
            $"--unit={ServiceName}",
            $"--description={DescriptionPrefix}{generation}",
            "--service-type=exec",
            "--property=Restart=on-failure",
            "--property=RestartSec=2s",
            "--property=StartLimitIntervalSec=30s",
            "--property=StartLimitBurst=5",
            "--property=KillMode=mixed",
            "--property=TimeoutStopSec=10s",
            $"--working-directory={startInfo.WorkingDirectory}",
        };
            arguments.AddRange(RuntimeLauncherProcess.GetExplicitEnvironment(startInfo)
                .Select(pair => $"--setenv={pair.Key}={pair.Value}"));
            arguments.Add(EscapeSystemdArgument(executable));
            arguments.AddRange(startInfo.ArgumentList.Select(EscapeSystemdArgument));

            HostServiceCommandResult launch;
            try
            {
                launch = await _runAsync("systemd-run", arguments, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TryRemoveGenerationAsync(generation).ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException)
            {
                await TryRemoveGenerationAsync(generation).ConfigureAwait(false);
                throw;
            }
            catch (Win32Exception exception)
            {
                var secondPreflight = await TryPreflightAsync(cancellationToken).ConfigureAwait(false);
                if (secondPreflight is null || IsUserManagerUnavailable(secondPreflight))
                {
                    return await LaunchFallbackAsync(
                            startInfo,
                            replaceExisting,
                            exception.Message,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                throw;
            }
            if (launch.ExitCode != 0)
            {
                var raced = await ReadUnitStateAsync(cancellationToken).ConfigureAwait(false);
                if (raced is
                    {
                        IsLoaded: true,
                        IsTransient: true,
                        IsSunderUnit: true,
                        IsActiveOrStarting: true,
                    })
                {
                    if (!replaceExisting)
                    {
                        return CreateReceipt(raced);
                    }
                }
                if (string.Equals(raced.GenerationId, generation, StringComparison.Ordinal))
                {
                    await TryRemoveGenerationAsync(generation).ConfigureAwait(false);
                }
                throw CreateCommandFailure("systemd-run", launch);
            }

            try
            {
                var launched = await ReadUnitStateAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(launched.GenerationId, generation, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"The systemd user unit '{ServiceName}' was replaced while Sunder was launching it.");
                }
                return CreateReceipt(launched);
            }
            catch
            {
                await TryRemoveGenerationAsync(generation).ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception exception) when (
            existingServiceDisplaced && exception is not IHostServiceReplacementFailure)
        {
            throw HostServiceReplacementFailure.Wrap(exception);
        }
    }

    public override async Task<HostServiceObservation> ObserveAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(receipt.Backend, "systemd-user", StringComparison.Ordinal))
        {
            return await _fallback.ObserveAsync(receipt, cancellationToken).ConfigureAwait(false);
        }

        var state = await ReadUnitStateAsync(cancellationToken).ConfigureAwait(false);
        if (!state.IsLoaded)
        {
            return new HostServiceObservation(
                HostServiceState.Absent,
                receipt.ProcessId,
                result: "systemd-unit-not-found");
        }
        if (receipt.InstanceId is not null
            && !string.Equals(receipt.InstanceId, state.GenerationId, StringComparison.Ordinal))
        {
            return new HostServiceObservation(
                HostServiceState.Absent,
                receipt.ProcessId,
                result: "systemd-invocation-replaced");
        }

        var contractState = state.ActiveState switch
        {
            "activating" => HostServiceState.Starting,
            "active" or "reloading" => HostServiceState.Running,
            "inactive" when string.Equals(state.Result, "success", StringComparison.Ordinal)
                => HostServiceState.Stopped,
            "inactive" or "failed" => HostServiceState.Failed,
            _ => HostServiceState.Unknown,
        };
        var exitCode = state.ExecMainCode == 1 ? state.ExecMainStatus : null;
        var detail = state.ExecMainCode is 2 or 3 && state.ExecMainStatus is { } signal
            ? $"The Host process ended after signal {signal}."
            : null;
        return new HostServiceObservation(
            contractState,
            state.ProcessId ?? receipt.ProcessId,
            exitCode,
            state.Result,
            detail);
    }

    public override async Task<bool> TryStopAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(receipt.Backend, "systemd-user", StringComparison.Ordinal))
        {
            return await _fallback.TryStopAsync(receipt, cancellationToken).ConfigureAwait(false);
        }
        var state = await ReadUnitStateAsync(cancellationToken).ConfigureAwait(false);
        if (!state.IsLoaded)
        {
            return true;
        }
        if (receipt.InstanceId is not null
            && !string.Equals(receipt.InstanceId, state.GenerationId, StringComparison.Ordinal))
        {
            return false;
        }
        await RunRequiredAsync(
                "systemctl",
                ["--user", "--no-ask-password", "stop", ServiceName],
                cancellationToken)
            .ConfigureAwait(false);
        await RunRequiredAsync(
                "systemctl",
                ["--user", "--no-ask-password", "reset-failed", ServiceName],
                cancellationToken)
            .ConfigureAwait(false);
        await WaitForUnitAbsentAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override void Dispose() => _ownedFallback?.Dispose();

    private async Task<SystemdUnitState> ReadUnitStateAsync(CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "--user", "--no-pager", "--all" };
        arguments.AddRange(UnitProperties.Select(property => $"--property={property}"));
        arguments.Add("show");
        arguments.Add(ServiceName);
        var result = await _runAsync("systemctl", arguments, cancellationToken).ConfigureAwait(false);
        var properties = ParseProperties(result.StandardOutput);
        if (properties.TryGetValue("LoadState", out var loadState)
            && string.Equals(loadState, "not-found", StringComparison.Ordinal))
        {
            return SystemdUnitState.Absent;
        }
        if (result.ExitCode != 0)
        {
            throw CreateCommandFailure("systemctl", result);
        }
        return new SystemdUnitState(
            Get(properties, "LoadState"),
            Get(properties, "ActiveState"),
            Get(properties, "SubState"),
            TryParsePositiveInt32(Get(properties, "MainPID")),
            Get(properties, "Result"),
            TryParseInt32(Get(properties, "ExecMainCode")),
            TryParseInt32(Get(properties, "ExecMainStatus")),
            Get(properties, "InvocationID"),
            string.Equals(Get(properties, "Transient"), "yes", StringComparison.Ordinal),
            Get(properties, "Description"));
    }

    private async Task WaitForUnitAbsentAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReconciliationTimeout);
        try
        {
            while ((await ReadUnitStateAsync(deadline.Token).ConfigureAwait(false)).IsLoaded)
            {
                await _delayAsync(ReconciliationPollInterval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"systemd did not remove Host unit '{ServiceName}' within {ReconciliationTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<HostServiceCommandResult?> TryPreflightAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _runAsync(
                    "systemctl",
                    ["--user", "--no-pager", "show", "--property=Version"],
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private async Task TryRemoveGenerationAsync(string generation)
    {
        try
        {
            using var deadline = new CancellationTokenSource(ReconciliationTimeout);
            var state = await ReadUnitStateAsync(deadline.Token).ConfigureAwait(false);
            if (!state.IsLoaded
                || !string.Equals(state.GenerationId, generation, StringComparison.Ordinal))
            {
                return;
            }
            await RunRequiredAsync(
                    "systemctl",
                    ["--user", "--no-ask-password", "stop", ServiceName],
                    deadline.Token)
                .ConfigureAwait(false);
            await RunRequiredAsync(
                    "systemctl",
                    ["--user", "--no-ask-password", "reset-failed", ServiceName],
                    deadline.Token)
                .ConfigureAwait(false);
            await WaitForUnitAbsentAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError(
                "Failed to clean up a canceled systemd Host launch.",
                exception);
        }
    }

    private async Task<HostServiceLaunchReceipt> LaunchFallbackAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        string? reason,
        CancellationToken cancellationToken)
    {
        AppSessionLog.WriteInfo(
            $"systemd user launch was unavailable; using detached Host fallback: {HostServiceDiagnostics.SanitizeDetail(reason) ?? "user manager unavailable"}");
        return await _fallback
            .ReconcileAndLaunchAsync(startInfo, replaceExisting, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunRequiredAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _runAsync(fileName, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandFailure(fileName, result);
        }
    }

    private static bool IsUserManagerUnavailable(HostServiceCommandResult result)
    {
        var diagnostic = GetDiagnostic(result);
        return diagnostic.Contains("Failed to connect to bus", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("not been booted with systemd", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("No medium found", StringComparison.OrdinalIgnoreCase)
               || diagnostic.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDiagnostic(HostServiceCommandResult result)
        => string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;

    private static InvalidOperationException CreateCommandFailure(
        string fileName,
        HostServiceCommandResult result)
    {
        var diagnostic = GetDiagnostic(result);
        return new InvalidOperationException(
            $"Host service command '{HostServiceDiagnostics.SanitizeCommandName(fileName)}' exited with code {result.ExitCode}"
            + (string.IsNullOrWhiteSpace(diagnostic) ? "." : $": {diagnostic.Trim()}"));
    }

    private static Dictionary<string, string> ParseProperties(string output)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            properties[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        return properties;
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string name)
        => values.TryGetValue(name, out var value) ? value : string.Empty;

    private static int? TryParsePositiveInt32(string value)
        => int.TryParse(
               value,
               System.Globalization.NumberStyles.None,
               System.Globalization.CultureInfo.InvariantCulture,
               out var parsed)
           && parsed > 0
            ? parsed
            : null;

    private static int? TryParseInt32(string value)
        => int.TryParse(
            value,
            System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
                ? parsed
                : null;

    private static string EscapeSystemdArgument(string value)
        => value.Replace("$", "$$", StringComparison.Ordinal);

    private static HostServiceLaunchReceipt CreateReceipt(SystemdUnitState state)
        => new("systemd-user", ServiceName, state.ProcessId, state.GenerationId);

    private sealed record SystemdUnitState(
        string LoadState,
        string ActiveState,
        string SubState,
        int? ProcessId,
        string Result,
        int? ExecMainCode,
        int? ExecMainStatus,
        string InvocationId,
        bool IsTransient,
        string Description)
    {
        public static SystemdUnitState Absent { get; } = new(
            "not-found",
            "inactive",
            "dead",
            null,
            string.Empty,
            null,
            null,
            string.Empty,
            false,
            string.Empty);

        public bool IsLoaded => !string.Equals(LoadState, "not-found", StringComparison.Ordinal);

        public bool IsActiveOrStarting => ActiveState is "active" or "activating" or "reloading";

        public bool IsSunderUnit => Description.StartsWith(DescriptionPrefix, StringComparison.Ordinal);

        public string? GenerationId => IsSunderUnit
            ? Description[DescriptionPrefix.Length..]
            : null;
    }
}

[SupportedOSPlatform("linux")]
internal sealed class UnixDetachedRuntimeLauncher : RuntimePersistentLauncherBase
{
    private const int MarkerVersion = 1;
    private const int SignalTerminate = 15;
    private const int SignalKill = 9;
    private const int ErrorNoSuchProcess = 3;
    private const string GenerationEnvironmentVariable = "SUNDER_HOST_SERVICE_GENERATION";
    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<int, Process> _processes = new();
    private readonly string _markerPath;
    private readonly string _lockPath;

    public UnixDetachedRuntimeLauncher(string? markerPath = null)
    {
        _markerPath = Path.GetFullPath(markerPath ?? GetDefaultMarkerPath());
        _lockPath = $"{_markerPath}.lock";
    }

    public override async Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reconciliationLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var existingServiceDisplaced = false;
        try
        {
            var existing = ReadMarker();
            if (existing is not null)
            {
                if (IsSameProcess(existing))
                {
                    if (!replaceExisting)
                    {
                        return CreateReceipt(existing);
                    }
                    await TerminateProcessGroupAsync(
                            existing,
                            cancellationToken,
                            () => existingServiceDisplaced = true)
                        .ConfigureAwait(false);
                }
                else if (IsProcessGroupAlive(existing.ProcessId))
                {
                    await TerminateProcessGroupAsync(
                            existing,
                            cancellationToken,
                            () => existingServiceDisplaced = true)
                        .ConfigureAwait(false);
                }
            }
            DeleteMarker();

            var setsid = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : "/bin/setsid";
            if (!File.Exists(setsid))
            {
                throw new InvalidOperationException(
                    "A systemd user manager and setsid are both unavailable for persistent Host launch.");
            }

            var detached = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = startInfo.WorkingDirectory,
            };
            detached.Environment.Clear();
            foreach (var pair in RuntimeLauncherProcess.GetServiceEnvironment(startInfo))
            {
                detached.Environment[pair.Key] = pair.Value;
            }
            var generation = Guid.NewGuid();
            detached.Environment[GenerationEnvironmentVariable] = generation.ToString("N");
            detached.ArgumentList.Add("-c");
            detached.ArgumentList.Add("exec \"$@\" </dev/null >/dev/null 2>&1");
            detached.ArgumentList.Add("sunder-host-launcher");
            detached.ArgumentList.Add(setsid);
            var executable = ResolvePhysicalPath(RuntimeLauncherProcess.ResolveExecutable(startInfo.FileName));
            if (!Path.IsPathFullyQualified(executable))
            {
                throw new InvalidOperationException(
                    "The detached Sunder Host executable could not be resolved to an absolute path.");
            }
            detached.ArgumentList.Add(executable);
            foreach (var argument in startInfo.ArgumentList)
            {
                detached.ArgumentList.Add(argument);
            }

            var process = Process.Start(detached)
                ?? throw new InvalidOperationException("setsid did not start the detached Host process.");
            var tracked = false;
            try
            {
                var marker = new DetachedHostProcessMarker(
                    MarkerVersion,
                    generation,
                    process.Id,
                    ReadProcessStartTicks(process.Id)
                        ?? throw new InvalidOperationException("The detached Host process identity was unavailable."),
                    executable,
                    "starting");
                WriteMarker(marker);
                if (!_processes.TryAdd(process.Id, process))
                {
                    throw new InvalidOperationException("The detached Host process could not be tracked.");
                }
                tracked = true;
                if (HasExpectedImage(marker))
                {
                    marker = marker with { Phase = "running" };
                    WriteMarker(marker);
                }
                return CreateReceipt(marker);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                if (tracked)
                {
                    _processes.TryRemove(process.Id, out _);
                }
                DeleteMarkerIfMatches(generation);
                process.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (
            existingServiceDisplaced && exception is not IHostServiceReplacementFailure)
        {
            throw HostServiceReplacementFailure.Wrap(exception);
        }
    }

    public override async Task<HostServiceObservation> ObserveAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reconciliationLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        if (receipt.ProcessId is not { } processId)
        {
            return HostServiceObservation.Unknown();
        }
        if (_processes.TryGetValue(processId, out var process))
        {
            if (!process.HasExited)
            {
                var runningMarker = ReadMarker();
                return new HostServiceObservation(
                    runningMarker is not null && HasExpectedImage(runningMarker)
                        ? HostServiceState.Running
                        : HostServiceState.Starting,
                    processId);
            }

            var exitCode = process.ExitCode;
            var trackedMarker = ReadMarker();
            var expectedGeneration = ParseGeneration(receipt.InstanceId);
            if (IsProcessGroupAlive(processId))
            {
                if (trackedMarker is not null && trackedMarker.Generation == expectedGeneration)
                {
                    await TerminateProcessGroupAsync(trackedMarker, cancellationToken).ConfigureAwait(false);
                }
                else if (expectedGeneration is { } generation
                         && IsOwnedProcessGroup(processId, generation))
                {
                    await TerminateVerifiedProcessGroupAsync(processId, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException(
                        "The exited detached Sunder Host left a process group whose identity could not be verified.");
                }
            }
            if (_processes.TryRemove(processId, out var completed))
            {
                completed.Dispose();
            }
            DeleteMarkerIfMatches(ParseGeneration(receipt.InstanceId));
            return new HostServiceObservation(
                exitCode == 0 ? HostServiceState.Stopped : HostServiceState.Failed,
                processId,
                exitCode,
                result: "detached-process-exit",
                diagnosticPaths: [_markerPath]);
        }

        var marker = ReadMarker();
        if (marker is null
            || marker.ProcessId != processId
            || receipt.InstanceId is not null
            && !string.Equals(receipt.InstanceId, marker.Generation.ToString("N"), StringComparison.Ordinal))
        {
            return new HostServiceObservation(
                HostServiceState.Absent,
                processId,
                result: "detached-process-not-found",
                diagnosticPaths: [_markerPath]);
        }
        if (!IsSameProcess(marker))
        {
            if (IsProcessGroupAlive(processId))
            {
                await TerminateProcessGroupAsync(marker, cancellationToken).ConfigureAwait(false);
            }
            DeleteMarkerIfMatches(marker.Generation);
            return new HostServiceObservation(
                HostServiceState.Failed,
                processId,
                result: "detached-process-leader-exit",
                diagnosticPaths: [_markerPath]);
        }
        return new HostServiceObservation(
            HasExpectedImage(marker) ? HostServiceState.Running : HostServiceState.Starting,
            processId);
    }

    public override async Task<bool> TryStopAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        using var reconciliationLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        if (receipt.ProcessId is not { } processId)
        {
            return false;
        }
        var expectedGeneration = ParseGeneration(receipt.InstanceId);
        var marker = ReadMarker();
        if (marker is null)
        {
            if (expectedGeneration is { } generation
                && IsOwnedProcessGroup(processId, generation))
            {
                await TerminateVerifiedProcessGroupAsync(processId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (IsProcessGroupAlive(processId))
            {
                return false;
            }
            if (_processes.TryRemove(processId, out var unmarkedProcess))
            {
                unmarkedProcess.Dispose();
            }
            return true;
        }
        if (expectedGeneration is null
            || marker.Generation != expectedGeneration
            || marker.ProcessId != processId)
        {
            return false;
        }
        if (IsSameProcess(marker) || IsProcessGroupAlive(processId))
        {
            await TerminateProcessGroupAsync(marker, cancellationToken).ConfigureAwait(false);
        }
        if (_processes.TryRemove(processId, out var completed))
        {
            completed.Dispose();
        }
        DeleteMarkerIfMatches(expectedGeneration);
        return true;
    }

    public override void Dispose()
    {
        foreach (var pair in _processes)
        {
            if (_processes.TryRemove(pair.Key, out var process))
            {
                process.Dispose();
            }
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_lockPath)!;
        Directory.CreateDirectory(directory);
        EnsureNotLink(new DirectoryInfo(directory));
        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var lockFile = new FileInfo(_lockPath);
                EnsureNotLink(lockFile, allowMissing: true);
                var stream = new FileStream(_lockPath, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 1,
                    Options = FileOptions.WriteThrough,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                File.SetUnixFileMode(_lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return stream;
            }
            catch (IOException)
            {
                await Task.Delay(LockPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private DetachedHostProcessMarker? ReadMarker()
    {
        EnsureNotLink(new FileInfo(_markerPath), allowMissing: true);
        if (!File.Exists(_markerPath))
        {
            return null;
        }
        try
        {
            var marker = JsonSerializer.Deserialize<DetachedHostProcessMarker>(
                File.ReadAllBytes(_markerPath),
                JsonOptions);
            return marker is { Version: MarkerVersion, ProcessId: > 0 }
                ? marker
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            AppSessionLog.WriteError("Ignored an invalid detached Host process marker.", exception);
            return null;
        }
    }

    private void WriteMarker(DetachedHostProcessMarker marker)
    {
        var directory = Path.GetDirectoryName(_markerPath)!;
        Directory.CreateDirectory(directory);
        EnsureNotLink(new DirectoryInfo(directory));
        EnsureNotLink(new FileInfo(_markerPath), allowMissing: true);
        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_markerPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(marker, JsonOptions);
            using (var stream = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _markerPath, overwrite: true);
            File.SetUnixFileMode(_markerPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private bool IsSameProcess(DetachedHostProcessMarker marker)
        => ReadProcessStartTicks(marker.ProcessId) == marker.StartTimeTicks
           && (HasExpectedImage(marker)
                || string.Equals(marker.Phase, "starting", StringComparison.Ordinal));

    private static async Task TerminateProcessGroupAsync(
        DetachedHostProcessMarker marker,
        CancellationToken cancellationToken,
        Action? terminationStarted = null)
    {
        if (!IsSameProcessStatic(marker) && !IsOwnedProcessGroup(marker))
        {
            if (IsProcessGroupAlive(marker.ProcessId))
            {
                throw new InvalidOperationException(
                    "The detached Sunder Host process-group identity could not be verified.");
            }
            return;
        }
        await TerminateVerifiedProcessGroupAsync(
                marker.ProcessId,
                cancellationToken,
                terminationStarted)
            .ConfigureAwait(false);
    }

    private static async Task TerminateVerifiedProcessGroupAsync(
        int processGroupId,
        CancellationToken cancellationToken,
        Action? terminationStarted = null)
    {
        SendSignalToProcessGroup(processGroupId, SignalTerminate);
        terminationStarted?.Invoke();
        if (await WaitForProcessExitAsync(processGroupId, TerminationTimeout, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        SendSignalToProcessGroup(processGroupId, SignalKill);
        if (!await WaitForProcessExitAsync(processGroupId, TerminationTimeout, cancellationToken).ConfigureAwait(false))
        {
            throw new TimeoutException("The detached Sunder Host process group did not terminate in time.");
        }
    }

    private static async Task<bool> WaitForProcessExitAsync(
        int processGroupId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (IsProcessGroupAlive(processGroupId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                return false;
            }
            await Task.Delay(LockPollInterval, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private static bool IsProcessGroupAlive(int processGroupId)
    {
        if (kill(-processGroupId, 0) == 0)
        {
            return true;
        }
        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNoSuchProcess)
        {
            return false;
        }
        throw new Win32Exception(error, "Linux could not inspect the detached Sunder Host process group.");
    }

    private static void SendSignalToProcessGroup(int processId, int signal)
    {
        if (kill(-processId, signal) == 0)
        {
            return;
        }
        var error = Marshal.GetLastPInvokeError();
        if (error != ErrorNoSuchProcess)
        {
            throw new Win32Exception(error, "Linux could not signal the detached Sunder Host process group.");
        }
    }

    private static bool IsSameProcessStatic(DetachedHostProcessMarker marker)
        => ReadProcessStartTicks(marker.ProcessId) == marker.StartTimeTicks
           && (HasExpectedImage(marker)
                || string.Equals(marker.Phase, "starting", StringComparison.Ordinal));

    private static bool IsOwnedProcessGroup(DetachedHostProcessMarker marker)
        => IsOwnedProcessGroup(marker.ProcessId, marker.Generation);

    private static bool IsOwnedProcessGroup(int processGroupId, Guid generation)
    {
        try
        {
            foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(
                        Path.GetFileName(processDirectory),
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var processId)
                    || ReadProcessGroupId(processId) != processGroupId)
                {
                    continue;
                }
                if (HasGenerationEnvironment(processId, generation))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    private static bool HasGenerationEnvironment(int processId, Guid generation)
    {
        try
        {
            var expected = Encoding.UTF8.GetBytes(
                $"{GenerationEnvironmentVariable}={generation:N}");
            var environment = File.ReadAllBytes($"/proc/{processId}/environ");
            var start = 0;
            for (var index = 0; index <= environment.Length; index++)
            {
                if (index < environment.Length && environment[index] != 0)
                {
                    continue;
                }
                if (environment.AsSpan(start, index - start).SequenceEqual(expected))
                {
                    return true;
                }
                start = index + 1;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    private static bool HasExpectedImage(DetachedHostProcessMarker marker)
    {
        try
        {
            var image = new FileInfo($"/proc/{marker.ProcessId}/exe").ResolveLinkTarget(true)?.FullName;
            return image is not null && PathEquals(image, marker.ExecutablePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long? ReadProcessStartTicks(int processId)
        => ReadProcessStatField(processId, 19);

    private static int? ReadProcessGroupId(int processId)
    {
        var value = ReadProcessStatField(processId, 2);
        return value is { } numericValue
               && numericValue is >= int.MinValue and <= int.MaxValue
            ? (int)numericValue
            : null;
    }

    private static long? ReadProcessStatField(int processId, int fieldIndex)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var commandEnd = stat.LastIndexOf(')');
            if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
            {
                return null;
            }
            var fields = stat[(commandEnd + 2)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > fieldIndex
                   && long.TryParse(
                        fields[fieldIndex],
                       System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out var ticks)
                ? ticks
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void DeleteMarkerIfMatches(Guid? generation)
    {
        if (generation is not null && ReadMarker()?.Generation == generation)
        {
            DeleteMarker();
        }
    }

    private void DeleteMarker()
    {
        if (File.Exists(_markerPath))
        {
            EnsureNotLink(new FileInfo(_markerPath));
            File.Delete(_markerPath);
        }
    }

    private static HostServiceLaunchReceipt CreateReceipt(DetachedHostProcessMarker marker)
        => new(
            "setsid",
            "sunder-host",
            marker.ProcessId,
            marker.Generation.ToString("N"));

    private static Guid? ParseGeneration(string? value)
        => Guid.TryParseExact(value, "N", out var generation) ? generation : null;

    private static string ResolvePhysicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return new FileInfo(fullPath).ResolveLinkTarget(true)?.FullName ?? fullPath;
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.Ordinal);

    private static void EnsureNotLink(FileSystemInfo item, bool allowMissing = false)
    {
        if (allowMissing && !item.Exists && item.LinkTarget is null)
        {
            return;
        }
        if (item.LinkTarget is not null
            || item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The detached Host launcher state contains an unsafe link.");
        }
    }

    private static string GetDefaultMarkerPath()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The current user's local application data directory is unavailable.");
        }
        return Path.Combine(localApplicationData, "Sunder", "host", "launcher", "linux-v1.json");
    }

    private sealed record DetachedHostProcessMarker(
        int Version,
        Guid Generation,
        int ProcessId,
        long StartTimeTicks,
        string ExecutablePath,
        string Phase);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);
}
