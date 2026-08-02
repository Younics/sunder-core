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
    public async Task MacSessionLauncher_BootstrapsPrivateCurrentSessionAgent()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-launchd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var commands = new List<(string FileName, string[] Arguments)>();
        var loaded = false;
        var launcher = new MacOsSessionRuntimeLauncher(
            (fileName, arguments, _) =>
            {
                var command = arguments.ToArray();
                commands.Add((fileName, command));
                if (fileName == "/bin/launchctl" && command[0] == "print")
                {
                    return Task.FromResult(LaunchdPrintResult(
                        command[1].EndsWith("/dev.sunder.host", StringComparison.Ordinal) && loaded));
                }
                if (fileName == "/bin/launchctl" && command[0] == "bootstrap")
                {
                    loaded = true;
                }
                return Task.FromResult(CommandResult(
                    standardOutput: command[0] == "kickstart" ? "4321\n" : string.Empty));
            },
            Path.Combine(root, "legacy.plist"),
            Path.Combine(root, "launchd", "dev.sunder.host.plist"),
            userId: 501);
        var startInfo = CreateMacStartInfo(root);
        startInfo.Environment["SUNDER_REGISTRY_TOKEN"] = "must-not-be-persisted";
        startInfo.Environment["HTTPS_PROXY"] = "http://proxy.example:8080";
        startInfo.Environment["XDG_DATA_HOME"] = Path.Combine(root, "xdg-data");

        try
        {
            var receipt = await launcher.LaunchAsync(
                startInfo,
                replaceExisting: false,
                CancellationToken.None);

            Assert.DoesNotContain(commands, command => command.Arguments[0] is "submit" or "remove");
            Assert.Contains(commands, command => command.Arguments.SequenceEqual(
                ["bootstrap", "gui/501", Path.Combine(root, "launchd", "dev.sunder.host.plist")]));
            Assert.Contains(commands, command => command.Arguments.SequenceEqual(
                ["kickstart", "-p", "gui/501/dev.sunder.host"]));
            var definition = await File.ReadAllTextAsync(
                Path.Combine(root, "launchd", "dev.sunder.host.plist"));
            Assert.Contains("<key>Program</key>", definition, StringComparison.Ordinal);
            Assert.Contains(startInfo.FileName, definition, StringComparison.Ordinal);
            Assert.Contains("SUNDER_RUNTIME_CONNECTION_FILE", definition, StringComparison.Ordinal);
            Assert.Contains("HTTPS_PROXY", definition, StringComparison.Ordinal);
            Assert.Contains("http://proxy.example:8080", definition, StringComparison.Ordinal);
            Assert.Contains("XDG_DATA_HOME", definition, StringComparison.Ordinal);
            Assert.DoesNotContain("SUNDER_REGISTRY_TOKEN", definition, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-be-persisted", definition, StringComparison.Ordinal);
            Assert.Equal("launchd", receipt.Backend);
            Assert.Equal("dev.sunder.host", receipt.ServiceName);
            Assert.Equal(4321, receipt.ProcessId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task MacSessionLauncher_WhenReplacementBootstrapFails_RestoresPreviousDefinition()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-launchd-tests", Guid.NewGuid().ToString("N"));
        var definitionPath = Path.Combine(root, "launchd", "dev.sunder.host.plist");
        Directory.CreateDirectory(Path.GetDirectoryName(definitionPath)!);
        var previousDefinition = "previous-definition";
        await File.WriteAllTextAsync(definitionPath, previousDefinition);
        var loaded = true;
        var bootstrapCount = 0;
        var launcher = new MacOsSessionRuntimeLauncher(
            (_, arguments, _) =>
            {
                var command = arguments.ToArray();
                if (command[0] == "print")
                {
                    return Task.FromResult(LaunchdPrintResult(
                        command[1].EndsWith("/dev.sunder.host", StringComparison.Ordinal) && loaded));
                }
                if (command[0] == "bootout")
                {
                    loaded = false;
                }
                if (command[0] == "bootstrap")
                {
                    bootstrapCount++;
                    if (bootstrapCount == 1)
                    {
                        return Task.FromResult(CommandResult(5, standardError: "bootstrap rejected"));
                    }
                    loaded = true;
                }
                return Task.FromResult(CommandResult(
                    standardOutput: command[0] == "kickstart" ? "4323" : string.Empty));
            },
            Path.Combine(root, "legacy.plist"),
            definitionPath,
            userId: 501);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(
                CreateMacStartInfo(root),
                replaceExisting: true,
                CancellationToken.None));

            Assert.Equal(2, bootstrapCount);
            Assert.True(loaded);
            Assert.Equal(previousDefinition, await File.ReadAllTextAsync(definitionPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task MacSessionLauncher_WhenUnloadedBootstrapFails_PreservesPreviousDefinition()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-launchd-tests", Guid.NewGuid().ToString("N"));
        var definitionPath = Path.Combine(root, "launchd", "dev.sunder.host.plist");
        Directory.CreateDirectory(Path.GetDirectoryName(definitionPath)!);
        var previousDefinition = "previous-unloaded-definition";
        await File.WriteAllTextAsync(definitionPath, previousDefinition);
        var bootstrapCount = 0;
        var launcher = new MacOsSessionRuntimeLauncher(
            (_, arguments, _) =>
            {
                var command = arguments.ToArray();
                if (command[0] == "print")
                {
                    return Task.FromResult(LaunchdPrintResult(loaded: false));
                }
                if (command[0] == "bootstrap")
                {
                    bootstrapCount++;
                    return Task.FromResult(CommandResult(5, standardError: "bootstrap rejected"));
                }
                return Task.FromResult(CommandResult());
            },
            Path.Combine(root, "legacy.plist"),
            definitionPath,
            userId: 501);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(
                CreateMacStartInfo(root),
                replaceExisting: false,
                CancellationToken.None));

            Assert.Equal(1, bootstrapCount);
            Assert.Equal(previousDefinition, await File.ReadAllTextAsync(definitionPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task MacSessionLauncher_WhenLoadedServiceHasNoDefinition_ReportsDisplacement()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-launchd-tests", Guid.NewGuid().ToString("N"));
        var definitionPath = Path.Combine(root, "launchd", "dev.sunder.host.plist");
        Directory.CreateDirectory(Path.GetDirectoryName(definitionPath)!);
        var loaded = true;
        var launcher = new MacOsSessionRuntimeLauncher(
            (_, arguments, _) =>
            {
                var command = arguments.ToArray();
                if (command[0] == "print")
                {
                    return Task.FromResult(LaunchdPrintResult(
                        loaded
                        && command[1].EndsWith("/dev.sunder.host", StringComparison.Ordinal)));
                }
                if (command[0] == "bootout"
                    && command[1].EndsWith("/dev.sunder.host", StringComparison.Ordinal))
                {
                    loaded = false;
                    return Task.FromResult(CommandResult());
                }
                if (command[0] == "bootstrap")
                {
                    return Task.FromResult(CommandResult(5, standardError: "bootstrap rejected"));
                }
                return Task.FromResult(CommandResult());
            },
            Path.Combine(root, "legacy.plist"),
            definitionPath,
            userId: 501);

        try
        {
            await Assert.ThrowsAsync<HostServiceReplacementException>(() => launcher.LaunchAsync(
                CreateMacStartInfo(root),
                replaceExisting: true,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("\"PID\" = 4330;\n\"LastExitStatus\" = 17;\n", (int)HostServiceState.Running, 4330, null)]
    [InlineData("\"LastExitStatus\" = 17;\n", (int)HostServiceState.Starting, null, 17)]
    [InlineData("\"LastExitStatus\" = 0;\n", (int)HostServiceState.Stopped, null, 0)]
    [SupportedOSPlatform("macos")]
    public async Task MacSessionLauncher_ObservesLaunchdState(
        string listOutput,
        int expectedState,
        int? expectedProcessId,
        int? expectedExitCode)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var launcher = new MacOsSessionRuntimeLauncher(
            (_, arguments, _) => Task.FromResult(
                arguments[0] == "print"
                    ? CommandResult()
                    : CommandResult(standardOutput: listOutput)),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "legacy.plist"),
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "host.plist"),
            userId: 501);

        var observation = await launcher.ObserveAsync(
            new HostServiceLaunchReceipt("launchd", "dev.sunder.host"),
            CancellationToken.None);

        Assert.Equal((HostServiceState)expectedState, observation.State);
        Assert.Equal(expectedProcessId, observation.ProcessId);
        Assert.Equal(expectedExitCode, observation.ExitCode);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task MacSessionLauncher_RealLaunchdReplacementRemainsObservable()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-launchd-integration", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var label = $"dev.sunder.host.tests.{Guid.NewGuid():N}";
        var launcher = new MacOsSessionRuntimeLauncher(
            legacyLaunchAgentPath: Path.Combine(root, "legacy.plist"),
            launchAgentPath: Path.Combine(root, "launchd", $"{label}.plist"),
            label: label);
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("sleep 30");
        var commandRunner = new HostServiceCommandRunner();

        try
        {
            var first = await launcher.LaunchAsync(startInfo, false, CancellationToken.None);
            var current = first;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                current = await launcher.LaunchAsync(startInfo, true, CancellationToken.None);
                var currentObservation = await launcher.ObserveAsync(current, CancellationToken.None);
                Assert.Equal(HostServiceState.Running, currentObservation.State);
                Assert.Equal(current.ProcessId, currentObservation.ProcessId);
            }

            Assert.NotNull(first.ProcessId);
            Assert.NotNull(current.ProcessId);
            Assert.NotEqual(first.ProcessId, current.ProcessId);
        }
        finally
        {
            await commandRunner.RunAsync(
                "/bin/launchctl",
                ["bootout", launcher.ServiceTarget],
                TimeSpan.FromSeconds(15));
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public async Task MacSessionLauncher_ReplacementObservesBootoutBeforeBootstrap()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-launchd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var commands = new List<string[]>();
        var loaded = true;
        var launcher = new MacOsSessionRuntimeLauncher(
            (_, arguments, _) =>
            {
                var command = arguments.ToArray();
                commands.Add(command);
                if (command[0] == "print")
                {
                    return Task.FromResult(LaunchdPrintResult(
                        command[1].EndsWith("/dev.sunder.host", StringComparison.Ordinal) && loaded));
                }
                if (command[0] == "bootout")
                {
                    loaded = false;
                }
                if (command[0] == "bootstrap")
                {
                    loaded = true;
                }
                return Task.FromResult(CommandResult(
                    standardOutput: command[0] == "kickstart" ? "4322" : string.Empty));
            },
            Path.Combine(root, "legacy.plist"),
            Path.Combine(root, "launchd", "dev.sunder.host.plist"),
            userId: 501);

        try
        {
            await launcher.LaunchAsync(
                CreateMacStartInfo(root),
                replaceExisting: true,
                CancellationToken.None);

            var bootoutIndex = commands.FindIndex(command => command[0] == "bootout");
            var absentObservationIndex = commands.FindIndex(
                bootoutIndex + 1,
                command => command[0] == "print" && command[1] == "gui/501/dev.sunder.host");
            var bootstrapIndex = commands.FindIndex(command => command[0] == "bootstrap");
            Assert.True(bootoutIndex >= 0);
            Assert.True(absentObservationIndex > bootoutIndex);
            Assert.True(bootstrapIndex > absentObservationIndex);
            Assert.DoesNotContain(commands, command => command[0] is "submit" or "remove");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task LinuxSessionLauncher_ReplacementClearsExistingUnitBeforeRun()
    {
        var commands = new List<(string FileName, string[] Arguments)>();
        var unitLoaded = true;
        var generation = "existing-generation";
        var launcher = new LinuxSystemdRuntimeLauncher(
            (fileName, arguments, _) =>
            {
                var command = arguments.ToArray();
                commands.Add((fileName, command));
                if (fileName == "systemctl" && command.Contains("--property=Version"))
                {
                    return Task.FromResult(CommandResult(standardOutput: "Version=255\n"));
                }
                if (fileName == "systemctl" && command.Contains("show"))
                {
                    return Task.FromResult(CommandResult(
                        standardOutput: unitLoaded
                            ? SystemdState(processId: 4242, generation: generation)
                            : "LoadState=not-found\n"));
                }
                if (fileName == "systemctl" && command.Contains("stop"))
                {
                    unitLoaded = false;
                }
                if (fileName == "systemd-run")
                {
                    generation = command
                        .Single(argument => argument.StartsWith("--description=Sunder current-user Host ", StringComparison.Ordinal))
                        ["--description=Sunder current-user Host ".Length..];
                    unitLoaded = true;
                }
                return Task.FromResult(CommandResult());
            });
        var startInfo = new ProcessStartInfo("/opt/sunder/Sunder.Host.Supervisor")
        {
            WorkingDirectory = "/opt/sunder",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:5275");

        var receipt = await launcher.LaunchAsync(
            startInfo,
            replaceExisting: true,
            CancellationToken.None);

        var stopIndex = commands.FindIndex(command => command.Arguments.Contains("stop"));
        var resetIndex = commands.FindIndex(command => command.Arguments.Contains("reset-failed"));
        var absentIndex = commands.FindIndex(
            resetIndex + 1,
            command => command.Arguments.Contains("show"));
        var launchIndex = commands.FindIndex(command => command.FileName == "systemd-run");
        Assert.True(stopIndex >= 0);
        Assert.True(resetIndex > stopIndex);
        Assert.True(absentIndex > resetIndex);
        Assert.True(launchIndex > absentIndex);
        Assert.Contains("--unit=sunder-host.service", commands[launchIndex].Arguments);
        Assert.DoesNotContain("--collect", commands[launchIndex].Arguments);
        Assert.Contains("--property=KillMode=mixed", commands[launchIndex].Arguments);
        Assert.Contains("/opt/sunder/Sunder.Host.Supervisor", commands[launchIndex].Arguments);
        Assert.Equal("systemd-user", receipt.Backend);
        Assert.Equal("sunder-host.service", receipt.ServiceName);
        Assert.Equal(4242, receipt.ProcessId);
        Assert.Equal(generation, receipt.InstanceId);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task LinuxSessionLauncher_WhenReplacementLaunchFails_ReportsDisplacedService()
    {
        var unitLoaded = true;
        using var launcher = new LinuxSystemdRuntimeLauncher((fileName, arguments, _) =>
        {
            if (fileName == "systemctl" && arguments.Contains("--property=Version"))
            {
                return Task.FromResult(CommandResult(standardOutput: "Version=255\n"));
            }
            if (fileName == "systemctl" && arguments.Contains("show"))
            {
                return Task.FromResult(CommandResult(
                    standardOutput: unitLoaded
                        ? SystemdState(processId: 4242, generation: "existing-generation")
                        : "LoadState=not-found\n"));
            }
            if (fileName == "systemctl" && arguments.Contains("stop"))
            {
                unitLoaded = false;
                return Task.FromResult(CommandResult());
            }
            if (fileName == "systemd-run")
            {
                return Task.FromResult(CommandResult(1, standardError: "launch rejected"));
            }
            return Task.FromResult(CommandResult());
        });

        var exception = await Assert.ThrowsAsync<HostServiceReplacementException>(() =>
            launcher.LaunchAsync(
                CreateLinuxStartInfo(),
                replaceExisting: true,
                CancellationToken.None));

        Assert.Contains("displaced", exception.Message, StringComparison.Ordinal);
        Assert.Contains("launch rejected", exception.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task LinuxSessionLauncher_RejectsPersistentUnitNameCollision()
    {
        var commands = new List<string>();
        using var launcher = new LinuxSystemdRuntimeLauncher((fileName, arguments, _) =>
        {
            commands.Add(fileName);
            return Task.FromResult(arguments.Contains("--property=Version")
                ? CommandResult(standardOutput: "Version=255\n")
                : CommandResult(standardOutput: SystemdState(transient: false)));
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => launcher.LaunchAsync(
            CreateLinuxStartInfo(),
            replaceExisting: true,
            CancellationToken.None));

        Assert.Contains("not a Sunder transient unit", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("systemd-run", commands);
    }

    [Theory]
    [InlineData("activating", "exit-code", 0, 1, 17, (int)HostServiceState.Starting, null)]
    [InlineData("active", "success", 4340, 1, 0, (int)HostServiceState.Running, 4340)]
    [InlineData("failed", "exit-code", 0, 1, 17, (int)HostServiceState.Failed, null)]
    [SupportedOSPlatform("linux")]
    public async Task LinuxSessionLauncher_ObservesSystemdState(
        string activeState,
        string result,
        int processId,
        int execMainCode,
        int execMainStatus,
        int expectedState,
        int? expectedProcessId)
    {
        using var launcher = new LinuxSystemdRuntimeLauncher((_, _, _) => Task.FromResult(CommandResult(
            standardOutput: SystemdState(
                activeState,
                result,
                processId,
                execMainCode,
                execMainStatus))));

        var observation = await launcher.ObserveAsync(
            new HostServiceLaunchReceipt("systemd-user", "sunder-host.service"),
            CancellationToken.None);

        Assert.Equal((HostServiceState)expectedState, observation.State);
        Assert.Equal(expectedProcessId, observation.ProcessId);
        Assert.Equal(execMainCode == 1 ? execMainStatus : null, observation.ExitCode);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task LinuxSessionLauncher_AutomaticRestartKeepsLaunchGeneration()
    {
        using var launcher = new LinuxSystemdRuntimeLauncher((_, _, _) => Task.FromResult(CommandResult(
            standardOutput: SystemdState(
                activeState: "activating",
                result: "exit-code",
                processId: 0,
                execMainStatus: 17,
                generation: "stable-generation"))));

        var observation = await launcher.ObserveAsync(
            new HostServiceLaunchReceipt(
                "systemd-user",
                "sunder-host.service",
                instanceId: "stable-generation"),
            CancellationToken.None);

        Assert.Equal(HostServiceState.Starting, observation.State);
        Assert.False(observation.IsTerminal);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task LinuxSessionLauncher_UsesFallbackOnlyWhenUserManagerIsUnavailable()
    {
        var fallback = new RecordingHostServiceManager(
            new HostServiceLaunchReceipt("test-fallback", "sunder-host", 4350));
        using var launcher = new LinuxSystemdRuntimeLauncher(
            (_, _, _) => Task.FromResult(CommandResult(
                1,
                standardError: "Failed to connect to bus: No medium found")),
            fallback);

        var receipt = await launcher.LaunchAsync(
            CreateLinuxStartInfo(),
            replaceExisting: false,
            CancellationToken.None);

        Assert.Equal("test-fallback", receipt.Backend);
        Assert.Equal(1, fallback.LaunchCount);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsLauncher_QuotesArgumentsForCreateProcess()
    {
        Assert.Equal("plain", WindowsBreakawayRuntimeLauncher.QuoteWindowsArgument("plain"));
        Assert.Equal("\"\"", WindowsBreakawayRuntimeLauncher.QuoteWindowsArgument(string.Empty));
        Assert.Equal("\"two words\"", WindowsBreakawayRuntimeLauncher.QuoteWindowsArgument("two words"));
        Assert.Equal("\"a\\\"b\"", WindowsBreakawayRuntimeLauncher.QuoteWindowsArgument("a\"b"));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task WindowsLauncher_ObservesExactProcessExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-windows-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var suffix = Guid.NewGuid().ToString("N");
        using var launcher = new WindowsBreakawayRuntimeLauncher(
            Path.Combine(root, "process.json"),
            $@"Local\Younics.Sunder.Host.Test.{suffix}",
            $@"Local\Younics.Sunder.Host.Test.{suffix}.Launch");
        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe")
        {
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("exit 23");

        try
        {
            var receipt = await launcher.LaunchAsync(startInfo, false, CancellationToken.None);
            HostServiceObservation observation;
            do
            {
                observation = await launcher.ObserveAsync(receipt, CancellationToken.None);
                if (!observation.IsTerminal)
                {
                    await Task.Delay(10);
                }
            } while (!observation.IsTerminal);

            Assert.Equal(HostServiceState.Failed, observation.State);
            Assert.Equal(23, observation.ExitCode);
            Assert.Equal(receipt.ProcessId, observation.ProcessId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HostServiceCommandRunner_ReturnsBoundedSanitizedOutputAndExitCode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new HostServiceCommandRunner(maxCapturedCharacters: 64);

        var result = await runner.RunAsync(
            "/bin/sh",
            [
                "-c",
                "printf '{\"accessToken\":\"stdout-secret\"}\\nabcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'; "
                + "printf 'password=stderr-secret\\nabcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789' >&2; exit 7",
            ],
            TimeSpan.FromSeconds(5));

        Assert.Equal(7, result.ExitCode);
        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
        Assert.True(result.StandardOutput.Length <= 64);
        Assert.True(result.StandardError.Length <= 64);
        Assert.Contains("accessToken\":[redacted]", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("password=[redacted]", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("stdout-secret", result.StandardOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr-secret", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostServiceCommandRunner_TimeoutTerminatesChildProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new HostServiceCommandRunner();
        var started = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(
            "/bin/sh",
            ["-c", "sleep 30"],
            TimeSpan.FromMilliseconds(100)));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DirectLauncher_ObservesTerminalProcessExit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var launcher = new DirectRuntimeLauncher();
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exit 23");

        var receipt = await launcher.LaunchAsync(
            startInfo,
            replaceExisting: false,
            CancellationToken.None);
        HostServiceObservation observation;
        do
        {
            observation = await launcher.ObserveAsync(receipt, CancellationToken.None);
            if (!observation.IsTerminal)
            {
                await Task.Delay(10);
            }
        } while (!observation.IsTerminal);

        Assert.Equal("direct", receipt.Backend);
        Assert.NotNull(receipt.ProcessId);
        Assert.Equal(HostServiceState.Failed, observation.State);
        Assert.Equal(receipt.ProcessId, observation.ProcessId);
        Assert.Equal(23, observation.ExitCode);
        Assert.Equal("process-exit", observation.Result);
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
        using var launcher = new UnixDetachedRuntimeLauncher(Path.Combine(root, "launcher.json"));
        try
        {
            var receipt = await launcher.LaunchAsync(
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
            launcher.Dispose();
            using var cleanupLauncher = new UnixDetachedRuntimeLauncher(
                Path.Combine(root, "launcher.json"));
            Assert.True(await cleanupLauncher.TryStopAsync(receipt, CancellationToken.None));

            var childExited = false;
            var childExitDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (!childExited && DateTimeOffset.UtcNow < childExitDeadline)
            {
                if (!File.Exists(pidPath)
                    || !int.TryParse(await File.ReadAllTextAsync(pidPath), out var childProcessId))
                {
                    break;
                }
                try
                {
                    using var child = Process.GetProcessById(childProcessId);
                    childExited = child.HasExited;
                }
                catch (ArgumentException)
                {
                    childExited = true;
                }
                if (!childExited)
                {
                    await Task.Delay(50);
                }
            }
            Assert.True(childExited, "The detached child survived exact cross-instance cleanup.");
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

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task LinuxDetachedFallback_ReconcilesAcrossLauncherInstances()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var sleep = new[] { "/usr/bin/sleep", "/bin/sleep" }.FirstOrDefault(File.Exists);
        if (sleep is null || (!File.Exists("/usr/bin/setsid") && !File.Exists("/bin/setsid")))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "sunder-detached-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var markerPath = Path.Combine(root, "launcher.json");
        var startInfo = new ProcessStartInfo(sleep)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("30");
        HostServiceLaunchReceipt? replacement = null;
        using var firstLauncher = new UnixDetachedRuntimeLauncher(markerPath);
        using var secondLauncher = new UnixDetachedRuntimeLauncher(markerPath);
        try
        {
            var first = await firstLauncher.LaunchAsync(startInfo, false, CancellationToken.None);
            firstLauncher.Dispose();
            var adopted = await secondLauncher.LaunchAsync(startInfo, false, CancellationToken.None);
            replacement = await secondLauncher.LaunchAsync(startInfo, true, CancellationToken.None);

            Assert.Equal(first.ProcessId, adopted.ProcessId);
            Assert.Equal(first.InstanceId, adopted.InstanceId);
            Assert.NotEqual(first.ProcessId, replacement.ProcessId);
        }
        finally
        {
            if (replacement is not null)
            {
                await secondLauncher.TryStopAsync(replacement, CancellationToken.None);
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

    private static HostServiceCommandResult CommandResult(
        int exitCode = 0,
        string standardOutput = "",
        string standardError = "")
        => new(exitCode, standardOutput, standardError, false, false);

    private static HostServiceCommandResult LaunchdPrintResult(bool loaded)
        => loaded
            ? CommandResult()
            : CommandResult(3, standardError: "Could not find service in domain");

    private static string SystemdState(
        string activeState = "active",
        string result = "success",
        int processId = 4242,
        int execMainCode = 1,
        int execMainStatus = 0,
        bool transient = true,
        string generation = "test-generation")
        => $"LoadState=loaded\nActiveState={activeState}\nSubState=running\nMainPID={processId}\n"
           + $"Result={result}\nExecMainCode={execMainCode}\nExecMainStatus={execMainStatus}\n"
           + $"InvocationID=test-invocation\nTransient={(transient ? "yes" : "no")}\n"
           + $"Description=Sunder current-user Host {generation}\n";

    private static ProcessStartInfo CreateLinuxStartInfo()
    {
        var startInfo = new ProcessStartInfo("/opt/sunder/Sunder.Host.Supervisor")
        {
            WorkingDirectory = "/opt/sunder",
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add("http://127.0.0.1:5275");
        return startInfo;
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

    private sealed class RecordingHostServiceManager(HostServiceLaunchReceipt receipt)
        : IHostServiceManager
    {
        public int LaunchCount { get; private set; }

        public Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
            ProcessStartInfo startInfo,
            bool replaceExisting,
            CancellationToken cancellationToken)
        {
            LaunchCount++;
            return Task.FromResult(receipt);
        }

        public Task<HostServiceObservation> ObserveAsync(
            HostServiceLaunchReceipt launchReceipt,
            CancellationToken cancellationToken)
            => Task.FromResult(HostServiceObservation.Unknown(launchReceipt.ProcessId));
    }
}
