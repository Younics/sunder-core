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
            return new MacOsLaunchdRuntimeLauncher();
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
internal sealed class MacOsLaunchdRuntimeLauncher : IRuntimePersistentLauncher
{
    private const string Label = "dev.sunder.runtime";
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, bool, Task<RuntimeLauncherResult>> _runAsync;
    private readonly string _launchAgentsPath;
    private readonly string _diagnosticsPath;
    private readonly uint _userId;

    public MacOsLaunchdRuntimeLauncher()
        : this(RuntimeLauncherProcess.RunWithResultAsync)
    {
    }

    internal MacOsLaunchdRuntimeLauncher(
        Func<string, IReadOnlyList<string>, CancellationToken, bool, Task<RuntimeLauncherResult>> runAsync,
        string? launchAgentsPath = null,
        uint? userId = null,
        string? diagnosticsPath = null)
    {
        _runAsync = runAsync;
        _launchAgentsPath = launchAgentsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "LaunchAgents");
        _diagnosticsPath = diagnosticsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Logs",
            "Sunder");
        _userId = userId ?? GetUserId();
    }

    public async Task LaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_launchAgentsPath);
        var plistPath = Path.Combine(_launchAgentsPath, $"{Label}.plist");
        var executable = RuntimeLauncherProcess.ResolveExecutable(startInfo.FileName);
        var arguments = new[] { executable }.Concat(startInfo.ArgumentList).ToArray();
        var diagnostics = PrepareDiagnostics();
        var plist = CreatePlist(arguments, startInfo, diagnostics.StandardOutputPath, diagnostics.StandardErrorPath);
        var domain = $"gui/{_userId}";
        var status = await _runAsync(
            "/bin/launchctl",
            ["list", Label],
            cancellationToken,
            false).ConfigureAwait(false);
        if (!replaceExisting
            && status.ExitCode == 0
            && TryGetLaunchdProcessId(status.StandardOutput, out var processId)
            && await PlistMatchesAsync(plistPath, plist, cancellationToken).ConfigureAwait(false))
        {
            AppSessionLog.WriteInfo($"Reusing launchd Runtime job '{Label}' (PID {processId}).");
            return;
        }

        var tempPath = $"{plistPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tempPath, plist, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            if (status.ExitCode == 0)
            {
                await _runAsync(
                    "/bin/launchctl",
                    ["bootout", $"{domain}/{Label}"],
                    cancellationToken,
                    true).ConfigureAwait(false);
            }

            File.Move(tempPath, plistPath, overwrite: true);
            File.SetUnixFileMode(plistPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            ResetDiagnosticFile(diagnostics.StandardOutputPath);
            ResetDiagnosticFile(diagnostics.StandardErrorPath);
            await _runAsync(
                "/bin/launchctl",
                ["bootstrap", domain, plistPath],
                cancellationToken,
                true).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string CreatePlist(
        IReadOnlyList<string> arguments,
        ProcessStartInfo startInfo,
        string standardOutputPath,
        string standardErrorPath)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        builder.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        builder.AppendLine("<plist version=\"1.0\"><dict>");
        AppendKeyString(builder, "Label", Label);
        builder.AppendLine("<key>ProgramArguments</key><array>");
        foreach (var argument in arguments)
        {
            builder.Append("<string>").Append(Escape(argument)).AppendLine("</string>");
        }
        builder.AppendLine("</array>");
        AppendKeyString(builder, "WorkingDirectory", startInfo.WorkingDirectory);
        builder.AppendLine("<key>RunAtLoad</key><true/>");
        builder.AppendLine("<key>ProcessType</key><string>Standard</string>");
        AppendKeyString(builder, "StandardOutPath", standardOutputPath);
        AppendKeyString(builder, "StandardErrorPath", standardErrorPath);
        builder.AppendLine("<key>EnvironmentVariables</key><dict>");
        foreach (var pair in RuntimeLauncherProcess.GetExplicitEnvironment(startInfo)
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            AppendKeyString(builder, pair.Key, pair.Value);
        }
        builder.AppendLine("</dict></dict></plist>");
        return builder.ToString();
    }

    private static async Task<bool> PlistMatchesAsync(
        string plistPath,
        string expected,
        CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(plistPath)
                   && string.Equals(
                       await File.ReadAllTextAsync(plistPath, cancellationToken).ConfigureAwait(false),
                       expected,
                       StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private RuntimeLaunchDiagnostics PrepareDiagnostics()
    {
        Directory.CreateDirectory(_diagnosticsPath);
        File.SetUnixFileMode(
            _diagnosticsPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new RuntimeLaunchDiagnostics(
            Path.Combine(_diagnosticsPath, "runtime-launchd.stdout.log"),
            Path.Combine(_diagnosticsPath, "runtime-launchd.stderr.log"));
    }

    private static void ResetDiagnosticFile(string path)
    {
        File.WriteAllText(path, string.Empty, new UTF8Encoding(false));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static bool TryGetLaunchdProcessId(string output, out int processId)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("\"PID\"", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator >= 0
                && int.TryParse(
                    line[(separator + 1)..].Trim().TrimEnd(';'),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out processId)
                && processId > 0)
            {
                return true;
            }
        }

        processId = 0;
        return false;
    }

    private static void AppendKeyString(StringBuilder builder, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        builder.Append("<key>").Append(Escape(key)).Append("</key><string>")
            .Append(Escape(value)).AppendLine("</string>");
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    [DllImport("libc")]
    private static extern uint getuid();

    private static uint GetUserId() => getuid();

    private sealed record RuntimeLaunchDiagnostics(string StandardOutputPath, string StandardErrorPath);
}

[SupportedOSPlatform("linux")]
internal sealed class LinuxSystemdRuntimeLauncher : IRuntimePersistentLauncher
{
    public async Task LaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        var executable = RuntimeLauncherProcess.ResolveExecutable(startInfo.FileName);
        var arguments = new List<string>
        {
            "--user",
            "--quiet",
            "--collect",
            "--unit=sunder-runtime",
            "--service-type=exec",
            $"--working-directory={startInfo.WorkingDirectory}",
        };
        arguments.AddRange(RuntimeLauncherProcess.GetExplicitEnvironment(startInfo)
            .Select(pair => $"--setenv={pair.Key}={pair.Value}"));
        arguments.Add(executable);
        arguments.AddRange(startInfo.ArgumentList);

        try
        {
            await RuntimeLauncherProcess.RunAsync("systemd-run", arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            AppSessionLog.WriteInfo($"systemd user launch was unavailable; using detached Runtime fallback: {exception.Message}");
            await new UnixDetachedRuntimeLauncher().LaunchAsync(startInfo, replaceExisting, cancellationToken).ConfigureAwait(false);
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
        foreach (var pair in RuntimeLauncherProcess.GetExplicitEnvironment(startInfo))
        {
            detached.Environment[pair.Key] = pair.Value;
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
                           && (pair.Key.StartsWith("SUNDER_", StringComparison.Ordinal)
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

    public static async Task<int> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool throwOnFailure = true)
        => (await RunWithResultAsync(
            fileName,
            arguments,
            cancellationToken,
            throwOnFailure).ConfigureAwait(false)).ExitCode;

    public static async Task<RuntimeLauncherResult> RunWithResultAsync(
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
        return new RuntimeLauncherResult(process.ExitCode, output, error);
    }
}

internal sealed record RuntimeLauncherResult(int ExitCode, string StandardOutput, string StandardError);
