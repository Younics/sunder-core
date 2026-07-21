using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace Sunder.App.Services;

internal interface IRuntimePersistentLauncher
{
    Task LaunchAsync(ProcessStartInfo startInfo, bool replaceExisting, CancellationToken cancellationToken);
}

internal static class RuntimePersistentLauncher
{
    public static IRuntimePersistentLauncher Create()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacOsSessionRuntimeLauncher();
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxSystemdRuntimeLauncher();
        }
        if (OperatingSystem.IsWindows())
        {
            return new WindowsBreakawayRuntimeLauncher();
        }

        return new DirectRuntimeLauncher();
    }
}

[SupportedOSPlatform("macos")]
internal sealed class MacOsSessionRuntimeLauncher(
    Func<string, IReadOnlyList<string>, CancellationToken, bool, Task>? runAsync = null,
    string? legacyLaunchAgentPath = null)
    : IRuntimePersistentLauncher
{
    private const string Label = "dev.sunder.host";
    private const string LegacyLabel = "dev.sunder.runtime";
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, bool, Task> _runAsync
        = runAsync ?? RuntimeLauncherProcess.RunAsync;
    private readonly string _legacyLaunchAgentPath = legacyLaunchAgentPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents",
        $"{LegacyLabel}.plist");

    public async Task LaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(_legacyLaunchAgentPath))
        {
            await _runAsync(
                    "/bin/launchctl",
                    ["remove", LegacyLabel],
                    cancellationToken,
                    false)
                .ConfigureAwait(false);
            File.Delete(_legacyLaunchAgentPath);
        }
        if (replaceExisting)
        {
            await _runAsync(
                    "/bin/launchctl",
                    ["remove", Label],
                    cancellationToken,
                    false)
                .ConfigureAwait(false);
        }

        var arguments = new List<string> { "submit", "-l", Label, "--", "/usr/bin/env" };
        arguments.AddRange(RuntimeLauncherProcess.GetExplicitEnvironment(startInfo)
            .Select(pair => $"{pair.Key}={pair.Value}"));
        arguments.Add(RuntimeLauncherProcess.ResolveExecutable(startInfo.FileName));
        arguments.AddRange(startInfo.ArgumentList);
        await _runAsync("/bin/launchctl", arguments, cancellationToken, true).ConfigureAwait(false);
    }
}

[SupportedOSPlatform("linux")]
internal sealed class LinuxSystemdRuntimeLauncher(
    Func<string, IReadOnlyList<string>, CancellationToken, bool, Task>? runAsync = null,
    Func<ProcessStartInfo, bool, CancellationToken, Task>? launchFallbackAsync = null)
    : IRuntimePersistentLauncher
{
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, bool, Task> _runAsync
        = runAsync ?? RuntimeLauncherProcess.RunAsync;
    private readonly Func<ProcessStartInfo, bool, CancellationToken, Task> _launchFallbackAsync
        = launchFallbackAsync ?? new UnixDetachedRuntimeLauncher().LaunchAsync;

    public async Task LaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        if (replaceExisting)
        {
            try
            {
                await _runAsync(
                        "systemctl",
                        ["--user", "stop", "sunder-host.service"],
                        cancellationToken,
                        false)
                    .ConfigureAwait(false);
                await _runAsync(
                        "systemctl",
                        ["--user", "reset-failed", "sunder-host.service"],
                        cancellationToken,
                        false)
                    .ConfigureAwait(false);
            }
            catch (Win32Exception)
            {
            }
        }

        var executable = RuntimeLauncherProcess.ResolveExecutable(startInfo.FileName);
        var arguments = new List<string>
        {
            "--user",
            "--quiet",
            "--collect",
            "--unit=sunder-host",
            "--service-type=exec",
            "--property=Restart=on-failure",
            "--property=RestartSec=2",
            $"--working-directory={startInfo.WorkingDirectory}",
        };
        arguments.AddRange(RuntimeLauncherProcess.GetExplicitEnvironment(startInfo)
            .Select(pair => $"--setenv={pair.Key}={pair.Value}"));
        arguments.Add(executable);
        arguments.AddRange(startInfo.ArgumentList);

        try
        {
            await _runAsync("systemd-run", arguments, cancellationToken, true).ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            AppSessionLog.WriteInfo($"systemd user launch was unavailable; using detached Runtime fallback: {exception.Message}");
            await _launchFallbackAsync(startInfo, replaceExisting, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("Failed to connect to bus", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("not been booted with systemd", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("No medium found", StringComparison.OrdinalIgnoreCase))
        {
            AppSessionLog.WriteInfo($"systemd user launch was unavailable; using detached Runtime fallback: {exception.Message}");
            await _launchFallbackAsync(startInfo, replaceExisting, cancellationToken).ConfigureAwait(false);
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class UnixDetachedRuntimeLauncher : IRuntimePersistentLauncher
{
    public Task LaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var setsid = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : "/bin/setsid";
        if (!File.Exists(setsid))
        {
            throw new InvalidOperationException("A systemd user manager and setsid are both unavailable for persistent Runtime launch.");
        }

        var detached = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = startInfo.WorkingDirectory,
        };
        detached.Environment.Clear();
        foreach (var pair in startInfo.Environment.Where(static pair => pair.Value is not null))
        {
            detached.Environment[pair.Key] = pair.Value!;
        }
        detached.ArgumentList.Add("-c");
        detached.ArgumentList.Add("exec \"$@\" </dev/null >/dev/null 2>&1");
        detached.ArgumentList.Add("sunder-runtime-launcher");
        detached.ArgumentList.Add(setsid);
        detached.ArgumentList.Add(RuntimeLauncherProcess.ResolveExecutable(startInfo.FileName));
        foreach (var argument in startInfo.ArgumentList)
        {
            detached.ArgumentList.Add(argument);
        }

        var process = Process.Start(detached)
            ?? throw new InvalidOperationException("setsid did not start the Runtime process.");
        process.Dispose();
        return Task.CompletedTask;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsBreakawayRuntimeLauncher : IRuntimePersistentLauncher
{
    private const uint DetachedProcess = 0x00000008;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNoWindow = 0x08000000;

    public Task LaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = RuntimeLauncherProcess.ResolveExecutable(startInfo.FileName);
        var commandLine = new StringBuilder(string.Join(
            ' ',
            new[] { executable }.Concat(startInfo.ArgumentList).Select(QuoteWindowsArgument)));
        var environment = CreateEnvironmentBlock(startInfo);
        var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
        var flags = DetachedProcess
                    | CreateNewProcessGroup
                    | CreateUnicodeEnvironment
                    | CreateBreakawayFromJob
                    | CreateNoWindow;
        var created = CreateProcess(
            executable,
            commandLine,
            IntPtr.Zero,
            IntPtr.Zero,
            false,
            flags,
            environment,
            startInfo.WorkingDirectory,
            ref startup,
            out var processInformation);
        var error = created ? 0 : Marshal.GetLastWin32Error();
        Marshal.FreeHGlobal(environment);
        if (!created)
        {
            throw new Win32Exception(error, "Windows could not create an independent Runtime process.");
        }

        CloseHandle(processInformation.Thread);
        CloseHandle(processInformation.Process);
        return Task.CompletedTask;
    }

    private static IntPtr CreateEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var block = string.Join('\0', startInfo.Environment
            .Where(static pair => pair.Value is not null)
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && value.All(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append(character);
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr ReservedPointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class DirectRuntimeLauncher : IRuntimePersistentLauncher
{
    public Task LaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null.");
        process.Dispose();
        return Task.CompletedTask;
    }
}

internal static class RuntimeLauncherProcess
{
    public static IEnumerable<KeyValuePair<string, string>> GetExplicitEnvironment(ProcessStartInfo startInfo)
        => startInfo.Environment
            .Where(pair => pair.Value is not null
                            && (pair.Key.StartsWith("SUNDER_", StringComparison.OrdinalIgnoreCase)
                                || pair.Key is "DOTNET_ROOT" or "PATH"))
            .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value!));

    public static string ResolveExecutable(string fileName)
    {
        if (Path.IsPathFullyQualified(fileName))
        {
            return fileName;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return fileName;
    }

    public static async Task RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
    {
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
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
            throw;
        }

        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{fileName}' exited with code {process.ExitCode}: {(string.IsNullOrWhiteSpace(error) ? output : error).Trim()}");
        }
    }
}
