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
    public async Task MacLaunchdLauncher_RetryReusesMatchingRunningJob()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-launchd-tests", Guid.NewGuid().ToString("N"));
        var launchAgents = Path.Combine(root, "LaunchAgents");
        var commands = new List<string[]>();
        var loaded = false;
        var launcher = new MacOsLaunchdRuntimeLauncher(
            (_, arguments, _, _) =>
            {
                commands.Add(arguments.ToArray());
                if (arguments[0] == "list")
                {
                    return Task.FromResult(loaded
                        ? new RuntimeLauncherResult(0, "\"PID\" = 4242;\n", string.Empty)
                        : new RuntimeLauncherResult(3, string.Empty, "service not found"));
                }
                if (arguments[0] == "bootstrap")
                {
                    loaded = true;
                }
                return Task.FromResult(new RuntimeLauncherResult(0, string.Empty, string.Empty));
            },
            launchAgents,
            userId: 501,
            diagnosticsPath: Path.Combine(root, "Logs"));
        var startInfo = CreateMacStartInfo(root);

        try
        {
            await launcher.LaunchAsync(startInfo, replaceExisting: false, CancellationToken.None);
            await launcher.LaunchAsync(startInfo, replaceExisting: false, CancellationToken.None);

            Assert.Equal(2, commands.Count(command => command[0] == "list"));
            Assert.Single(commands, command => command[0] == "bootstrap");
            Assert.DoesNotContain(commands, command => command[0] == "bootout");
            var plist = await File.ReadAllTextAsync(Path.Combine(launchAgents, "dev.sunder.runtime.plist"));
            Assert.Contains("<key>ProcessType</key><string>Standard</string>", plist, StringComparison.Ordinal);
            Assert.DoesNotContain("<string>Background</string>", plist, StringComparison.Ordinal);
            Assert.Contains("<key>StandardOutPath</key>", plist, StringComparison.Ordinal);
            Assert.Contains("<key>StandardErrorPath</key>", plist, StringComparison.Ordinal);

            await launcher.LaunchAsync(startInfo, replaceExisting: true, CancellationToken.None);

            Assert.Single(commands, command => command[0] == "bootout");
            Assert.Equal(2, commands.Count(command => command[0] == "bootstrap"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task MacLaunchdLauncher_BootoutFailureDoesNotBootstrapReplacement()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-launchd-tests", Guid.NewGuid().ToString("N"));
        var launchAgents = Path.Combine(root, "LaunchAgents");
        Directory.CreateDirectory(launchAgents);
        var plistPath = Path.Combine(launchAgents, "dev.sunder.runtime.plist");
        await File.WriteAllTextAsync(plistPath, "stale");
        var commands = new List<string[]>();
        var bootoutAttempts = 0;
        var loaded = true;
        var launcher = new MacOsLaunchdRuntimeLauncher(
            (_, arguments, _, _) =>
            {
                commands.Add(arguments.ToArray());
                return arguments[0] switch
                {
                    "list" => Task.FromResult(loaded
                        ? new RuntimeLauncherResult(0, "\"PID\" = 4242;\n", string.Empty)
                        : new RuntimeLauncherResult(3, string.Empty, "service not found")),
                    "bootout" when Interlocked.Increment(ref bootoutAttempts) == 1
                        => Task.FromException<RuntimeLauncherResult>(new InvalidOperationException("bootout failed")),
                    "bootout" => CompleteBootout(),
                    "bootstrap" => CompleteBootstrap(),
                    _ => Task.FromResult(new RuntimeLauncherResult(0, string.Empty, string.Empty)),
                };

                Task<RuntimeLauncherResult> CompleteBootout()
                {
                    loaded = false;
                    return Task.FromResult(new RuntimeLauncherResult(0, string.Empty, string.Empty));
                }

                Task<RuntimeLauncherResult> CompleteBootstrap()
                {
                    loaded = true;
                    return Task.FromResult(new RuntimeLauncherResult(0, string.Empty, string.Empty));
                }
            },
            launchAgents,
            userId: 501,
            diagnosticsPath: Path.Combine(root, "Logs"));

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => launcher.LaunchAsync(
                    CreateMacStartInfo(root),
                    replaceExisting: false,
                    CancellationToken.None));

            Assert.Contains("bootout failed", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(commands, command => command[0] == "bootstrap");
            Assert.Equal("stale", await File.ReadAllTextAsync(plistPath));

            await launcher.LaunchAsync(
                CreateMacStartInfo(root),
                replaceExisting: false,
                CancellationToken.None);

            Assert.Equal(2, bootoutAttempts);
            Assert.Single(commands, command => command[0] == "bootstrap");
            Assert.NotEqual("stale", await File.ReadAllTextAsync(plistPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeLauncherProcess.RunWithResultAsync(
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
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                }
            }
            Directory.Delete(root, recursive: true);
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
