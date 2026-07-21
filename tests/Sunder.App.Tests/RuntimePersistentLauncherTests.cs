using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimePersistentLauncherTests
{
    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task MacSessionLauncher_SubmitsTransientCurrentSessionJob()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var commands = new List<string[]>();
        var launcher = new MacOsSessionRuntimeLauncher(
            (_, arguments, _, _) =>
            {
                commands.Add(arguments.ToArray());
                return Task.CompletedTask;
            },
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "legacy.plist"));
        var startInfo = CreateMacStartInfo(Path.GetTempPath());

        await launcher.LaunchAsync(startInfo, replaceExisting: false, CancellationToken.None);

        var submit = Assert.Single(commands);
        Assert.Equal(["submit", "-l", "dev.sunder.host", "--", "/usr/bin/env"], submit[..5]);
        Assert.Contains(submit, argument => argument.StartsWith("SUNDER_RUNTIME_CONNECTION_FILE=", StringComparison.Ordinal));
        Assert.Contains(startInfo.FileName, submit);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task MacSessionLauncher_ReplacementRemovesExistingJobBeforeSubmit()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var commands = new List<string[]>();
        var launcher = new MacOsSessionRuntimeLauncher(
            (_, arguments, _, _) =>
            {
                commands.Add(arguments.ToArray());
                return Task.CompletedTask;
            },
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "legacy.plist"));

        await launcher.LaunchAsync(
            CreateMacStartInfo(Path.GetTempPath()),
            replaceExisting: true,
            CancellationToken.None);

        Assert.Equal(["remove", "dev.sunder.host"], commands[0]);
        Assert.Equal("submit", commands[1][0]);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task LinuxSessionLauncher_ReplacementClearsExistingUnitBeforeRun()
    {
        var commands = new List<(string FileName, string[] Arguments, bool ThrowOnFailure)>();
        var launcher = new LinuxSystemdRuntimeLauncher(
            (fileName, arguments, _, throwOnFailure) =>
            {
                commands.Add((fileName, arguments.ToArray(), throwOnFailure));
                return Task.CompletedTask;
            });
        var startInfo = new ProcessStartInfo("/opt/sunder/Sunder.Host.Supervisor")
        {
            WorkingDirectory = "/opt/sunder",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:5275");

        await launcher.LaunchAsync(startInfo, replaceExisting: true, CancellationToken.None);

        Assert.Equal(3, commands.Count);
        Assert.Equal("systemctl", commands[0].FileName);
        Assert.Equal(["--user", "stop", "sunder-host.service"], commands[0].Arguments);
        Assert.False(commands[0].ThrowOnFailure);
        Assert.Equal("systemctl", commands[1].FileName);
        Assert.Equal(["--user", "reset-failed", "sunder-host.service"], commands[1].Arguments);
        Assert.False(commands[1].ThrowOnFailure);
        Assert.Equal("systemd-run", commands[2].FileName);
        Assert.True(commands[2].ThrowOnFailure);
        Assert.Contains("--unit=sunder-host", commands[2].Arguments);
        Assert.Contains("/opt/sunder/Sunder.Host.Supervisor", commands[2].Arguments);
    }

    [Fact]
    public async Task LauncherCommandCancellationTerminatesChildProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var started = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeLauncherProcess.RunAsync(
            "/bin/sh",
            ["-c", "sleep 30"],
            cancellation.Token));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LinuxDetachedFallback_HelperExitLeavesServiceReachable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (!File.Exists("/bin/sh")
            || (!File.Exists("/usr/bin/setsid") && !File.Exists("/bin/setsid")))
        {
            return;
        }
        var python = new[] { "/usr/bin/python3", "/usr/local/bin/python3" }.FirstOrDefault(File.Exists);
        if (python is null)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-detached-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidPath = Path.Combine(root, "server.pid");
        var port = ReservePort();
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("\"$1\" -m http.server \"$2\" --bind 127.0.0.1 >/dev/null 2>&1 & printf '%s' \"$!\" > \"$3\"");
        startInfo.ArgumentList.Add("sunder-runtime-parent-helper");
        startInfo.ArgumentList.Add(python);
        startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(pidPath);
        try
        {
            await new UnixDetachedRuntimeLauncher().LaunchAsync(
                startInfo,
                replaceExisting: false,
                CancellationToken.None);
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(250) };
            var reachable = false;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (!reachable && DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    using var response = await client.GetAsync($"http://127.0.0.1:{port}/");
                    reachable = response.StatusCode == HttpStatusCode.OK;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(50);
                }
                catch (TaskCanceledException)
                {
                    await Task.Delay(50);
                }
            }

            Assert.True(reachable, "Detached service was not reachable after its launching helper exited.");
        }
        finally
        {
            if (File.Exists(pidPath)
                && int.TryParse(await File.ReadAllTextAsync(pidPath), out var processId))
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                }
            }
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static ProcessStartInfo CreateMacStartInfo(string root)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(root, "Sunder.Runtime.Host"))
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:5275");
        startInfo.Environment["SUNDER_RUNTIME_CONNECTION_FILE"] = Path.Combine(root, "runtime", "connection.json");
        return startInfo;
    }
}
