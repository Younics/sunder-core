using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class RuntimePersistentLauncherTests
{
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
            await new UnixDetachedRuntimeLauncher().LaunchAsync(startInfo, CancellationToken.None);
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
}
