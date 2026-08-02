using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Sunder.App.Services;

[SupportedOSPlatform("macos")]
internal sealed class MacOsSessionRuntimeLauncher : RuntimePersistentLauncherBase
{
    private const string DefaultLabel = "dev.sunder.host";
    private const string LegacyLabel = "dev.sunder.runtime";
    private static readonly TimeSpan ReconciliationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReconciliationPollInterval = TimeSpan.FromMilliseconds(50);
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<HostServiceCommandResult>> _runAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly string _legacyLaunchAgentPath;
    private readonly string _launchAgentPath;
    private readonly string _reconciliationLockPath;
    private readonly string _domain;
    private readonly string _label;
    private readonly string _serviceTarget;

    public MacOsSessionRuntimeLauncher(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<HostServiceCommandResult>>? runAsync = null,
        string? legacyLaunchAgentPath = null,
        string? launchAgentPath = null,
        uint? userId = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        string? label = null)
    {
        _runAsync = runAsync ?? RuntimeLauncherProcess.RunResultAsync;
        _delayAsync = delayAsync ?? Task.Delay;
        _legacyLaunchAgentPath = legacyLaunchAgentPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "LaunchAgents",
            $"{LegacyLabel}.plist");
        _label = string.IsNullOrWhiteSpace(label) ? DefaultLabel : label;
        _launchAgentPath = Path.GetFullPath(launchAgentPath ?? GetDefaultLaunchAgentPath());
        _reconciliationLockPath = $"{_launchAgentPath}.lock";
        _domain = $"gui/{userId ?? GetEffectiveUserId()}";
        _serviceTarget = $"{_domain}/{_label}";
    }

    internal string ServiceTarget => _serviceTarget;

    public override async Task<HostServiceLaunchReceipt> ReconcileAndLaunchAsync(
        ProcessStartInfo startInfo,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateStartInfo(startInfo);
        using var reconciliationLock = await AcquireReconciliationLockAsync(cancellationToken)
            .ConfigureAwait(false);
        await RemoveLegacyServiceAsync(cancellationToken).ConfigureAwait(false);

        var loaded = await IsLoadedAsync(_serviceTarget, cancellationToken).ConfigureAwait(false);
        if (loaded && !replaceExisting)
        {
            await RunRequiredAsync(
                    "/bin/launchctl",
                    ["enable", _serviceTarget],
                    cancellationToken)
                .ConfigureAwait(false);
            return await KickStartAsync(
                    cancellationToken,
                    ComputeDefinitionIdentity(TryReadExistingDefinition()))
                .ConfigureAwait(false);
        }

        var previousDefinition = TryReadExistingDefinition();
        try
        {
            if (loaded)
            {
                await _runAsync(
                        "/bin/launchctl",
                        ["bootout", _serviceTarget],
                        cancellationToken)
                    .ConfigureAwait(false);
                await WaitForAbsentAsync(_serviceTarget, cancellationToken).ConfigureAwait(false);
            }
            var definition = CreateLaunchAgentDefinition(startInfo, _label);
            var definitionIdentity = ComputeDefinitionIdentity(definition);
            await PublishDefinitionAsync(definition, cancellationToken)
                .ConfigureAwait(false);
            await RunRequiredAsync(
                    "/bin/launchctl",
                    ["enable", _serviceTarget],
                    cancellationToken)
                .ConfigureAwait(false);
            var bootstrap = await _runAsync(
                    "/bin/launchctl",
                    ["bootstrap", _domain, _launchAgentPath],
                    cancellationToken)
                .ConfigureAwait(false);
            if (bootstrap.ExitCode != 0)
            {
                throw CreateCommandFailure("launchctl", bootstrap);
            }

            return await KickStartAsync(cancellationToken, definitionIdentity).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (!await TryRestoreDefinitionAsync(previousDefinition, loaded).ConfigureAwait(false)
                && loaded)
            {
                throw HostServiceReplacementFailure.Wrap(exception);
            }
            throw;
        }
    }

    public override async Task<HostServiceObservation> ObserveAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt.InstanceId is not null
            && !string.Equals(
                receipt.InstanceId,
                ComputeDefinitionIdentity(TryReadExistingDefinition()),
                StringComparison.Ordinal))
        {
            return new HostServiceObservation(
                HostServiceState.Absent,
                receipt.ProcessId,
                result: "launchd-definition-replaced",
                diagnosticPaths: [_launchAgentPath]);
        }
        if (!await IsLoadedAsync(_serviceTarget, cancellationToken).ConfigureAwait(false))
        {
            return new HostServiceObservation(
                HostServiceState.Absent,
                receipt.ProcessId,
                result: "launchd-service-absent",
                diagnosticPaths: [_launchAgentPath]);
        }

        var result = await _runAsync(
                "/bin/launchctl",
                ["list", _label],
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return HostServiceObservation.Unknown(receipt.ProcessId);
        }

        var processId = TryReadLaunchdValue(result.StandardOutput, "PID");
        if (processId is > 0 and <= int.MaxValue)
        {
            return new HostServiceObservation(HostServiceState.Running, (int)processId.Value);
        }

        var lastExitStatus = TryReadLaunchdValue(result.StandardOutput, "LastExitStatus");
        if (lastExitStatus is null)
        {
            return new HostServiceObservation(HostServiceState.Starting, receipt.ProcessId);
        }
        if (lastExitStatus < 0)
        {
            return new HostServiceObservation(
                HostServiceState.Starting,
                receipt.ProcessId,
                result: "launchd-restarting-after-signal",
                safeDetail: $"The previous Host process ended after signal {-lastExitStatus.Value}.",
                diagnosticPaths: [_launchAgentPath]);
        }
        if (lastExitStatus > int.MaxValue)
        {
            return HostServiceObservation.Unknown(receipt.ProcessId);
        }
        if (lastExitStatus == 0)
        {
            return new HostServiceObservation(
                HostServiceState.Stopped,
                receipt.ProcessId,
                exitCode: 0,
                result: "launchd-clean-exit",
                diagnosticPaths: [_launchAgentPath]);
        }
        return new HostServiceObservation(
            HostServiceState.Starting,
            receipt.ProcessId,
            exitCode: (int)lastExitStatus.Value,
            result: "launchd-restarting",
            safeDetail: "The Host exited unsuccessfully and launchd is retrying it.",
            diagnosticPaths: [_launchAgentPath]);
    }

    public override async Task<bool> TryStopAsync(
        HostServiceLaunchReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(receipt.ServiceName, _label, StringComparison.Ordinal))
        {
            return false;
        }
        using var reconciliationLock = await AcquireReconciliationLockAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await IsLoadedAsync(_serviceTarget, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (receipt.InstanceId is not null)
        {
            if (!string.Equals(
                    receipt.InstanceId,
                    ComputeDefinitionIdentity(TryReadExistingDefinition()),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        else
        {
            var observation = await ObserveAsync(receipt, cancellationToken).ConfigureAwait(false);
            if (receipt.ProcessId is not null && observation.ProcessId != receipt.ProcessId)
            {
                return false;
            }
        }
        await _runAsync(
                "/bin/launchctl",
                ["bootout", _serviceTarget],
                cancellationToken)
            .ConfigureAwait(false);
        await WaitForAbsentAsync(_serviceTarget, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<HostServiceLaunchReceipt> KickStartAsync(
        CancellationToken cancellationToken,
        string? definitionIdentity)
    {
        var result = await _runAsync(
                "/bin/launchctl",
                ["kickstart", "-p", _serviceTarget],
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw CreateCommandFailure("launchctl", result);
        }

        var output = result.StandardOutput.Trim();
        if (!int.TryParse(
                output,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var processId)
            || processId <= 0)
        {
            throw new InvalidOperationException("launchctl kickstart did not return a valid Host process ID.");
        }
        return new HostServiceLaunchReceipt(
            "launchd",
            _label,
            processId,
            definitionIdentity);
    }

    private async Task RemoveLegacyServiceAsync(CancellationToken cancellationToken)
    {
        var legacyTarget = $"{_domain}/{LegacyLabel}";
        if (await IsLoadedAsync(legacyTarget, cancellationToken).ConfigureAwait(false))
        {
            await _runAsync(
                    "/bin/launchctl",
                    ["bootout", legacyTarget],
                    cancellationToken)
                .ConfigureAwait(false);
            await WaitForAbsentAsync(legacyTarget, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(_legacyLaunchAgentPath))
        {
            return;
        }
        var legacyDefinition = new FileInfo(_legacyLaunchAgentPath);
        if (legacyDefinition.LinkTarget is not null
            || (legacyDefinition.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            AppSessionLog.WriteInfo("Preserved an unsafe legacy Sunder LaunchAgent definition.");
            return;
        }
        File.Delete(_legacyLaunchAgentPath);
    }

    private async Task<bool> IsLoadedAsync(string serviceTarget, CancellationToken cancellationToken)
    {
        var result = await _runAsync(
                "/bin/launchctl",
                ["print", serviceTarget],
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode == 0)
        {
            return true;
        }
        var diagnostic = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        if (diagnostic.Contains("Could not find service", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Contains("No such process", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        throw CreateCommandFailure("launchctl", result);
    }

    private async Task WaitForAbsentAsync(string serviceTarget, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ReconciliationTimeout);
        try
        {
            while (await IsLoadedAsync(serviceTarget, deadline.Token).ConfigureAwait(false))
            {
                await _delayAsync(ReconciliationPollInterval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"launchd did not remove Host service '{_label}' within {ReconciliationTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<FileStream> AcquireReconciliationLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_reconciliationLockPath)!;
        Directory.CreateDirectory(directory);
        EnsureNotLink(new DirectoryInfo(directory), "launchd state directory");
        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var lockFile = new FileInfo(_reconciliationLockPath);
                if (lockFile.LinkTarget is not null
                    || (lockFile.Exists && (lockFile.Attributes & FileAttributes.ReparsePoint) != 0))
                {
                    throw new InvalidDataException(
                        "The Sunder launchd reconciliation lock is not a regular file.");
                }
                var stream = new FileStream(_reconciliationLockPath, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 1,
                    Options = FileOptions.WriteThrough,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                File.SetUnixFileMode(
                    _reconciliationLockPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return stream;
            }
            catch (IOException)
            {
                await _delayAsync(ReconciliationPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private byte[]? TryReadExistingDefinition()
    {
        var definition = new FileInfo(_launchAgentPath);
        if (definition.LinkTarget is not null
            || (definition.Exists && (definition.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException(
                "The Sunder launchd service definition is not a regular file.");
        }
        if (!definition.Exists)
        {
            return null;
        }
        return File.ReadAllBytes(_launchAgentPath);
    }

    private async Task PublishDefinitionAsync(byte[] definition, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_launchAgentPath)!;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_launchAgentPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            await using (var stream = new FileStream(temporaryPath, options))
            {
                await stream.WriteAsync(definition, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            await RunRequiredAsync(
                    "/usr/bin/plutil",
                    ["-lint", "--", temporaryPath],
                    cancellationToken)
                .ConfigureAwait(false);
            var existingDefinition = new FileInfo(_launchAgentPath);
            if (existingDefinition.LinkTarget is not null
                || (existingDefinition.Exists
                    && (existingDefinition.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new InvalidDataException(
                    "The Sunder launchd service definition is not a regular file.");
            }
            File.Move(temporaryPath, _launchAgentPath, overwrite: true);
            File.SetUnixFileMode(
                _launchAgentPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<bool> TryRestoreDefinitionAsync(
        byte[]? previousDefinition,
        bool previousDefinitionWasLoaded)
    {
        try
        {
            using var deadline = new CancellationTokenSource(ReconciliationTimeout);
            if (await IsLoadedAsync(_serviceTarget, deadline.Token).ConfigureAwait(false))
            {
                await _runAsync(
                        "/bin/launchctl",
                        ["bootout", _serviceTarget],
                        deadline.Token)
                    .ConfigureAwait(false);
                await WaitForAbsentAsync(_serviceTarget, deadline.Token).ConfigureAwait(false);
            }
            if (previousDefinition is null)
            {
                if (File.Exists(_launchAgentPath))
                {
                    File.Delete(_launchAgentPath);
                }
                return !previousDefinitionWasLoaded;
            }

            await PublishDefinitionAsync(previousDefinition, deadline.Token).ConfigureAwait(false);
            if (!previousDefinitionWasLoaded)
            {
                return true;
            }
            await RunRequiredAsync(
                    "/bin/launchctl",
                    ["enable", _serviceTarget],
                    deadline.Token)
                .ConfigureAwait(false);
            await RunRequiredAsync(
                    "/bin/launchctl",
                    ["bootstrap", _domain, _launchAgentPath],
                    deadline.Token)
                .ConfigureAwait(false);
            await KickStartAsync(
                    deadline.Token,
                    ComputeDefinitionIdentity(previousDefinition))
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            AppSessionLog.WriteError("Failed to restore the previous Sunder Host launchd definition.", exception);
            return false;
        }
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

    private static InvalidOperationException CreateCommandFailure(
        string fileName,
        HostServiceCommandResult result)
    {
        var diagnostic = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        return new InvalidOperationException(
            $"Host service command '{HostServiceDiagnostics.SanitizeCommandName(fileName)}' exited with code {result.ExitCode}"
            + (string.IsNullOrWhiteSpace(diagnostic) ? "." : $": {diagnostic.Trim()}"));
    }

    private static void ValidateStartInfo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var executable = RuntimeLauncherProcess.ResolveExecutable(startInfo.FileName);
        if (!Path.IsPathFullyQualified(executable))
        {
            throw new InvalidOperationException("The Sunder Host executable could not be resolved to an absolute path for launchd.");
        }
        if (string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            || !Path.IsPathFullyQualified(startInfo.WorkingDirectory))
        {
            throw new InvalidOperationException("The Sunder Host working directory must be absolute for launchd.");
        }
    }

    private static byte[] CreateLaunchAgentDefinition(ProcessStartInfo startInfo, string label)
    {
        var executable = RuntimeLauncherProcess.ResolveExecutable(startInfo.FileName);
        var programArguments = new XElement("array",
            new XElement("string", executable),
            startInfo.ArgumentList.Select(argument => new XElement("string", argument)));
        var environment = new XElement("dict");
        foreach (var pair in RuntimeLauncherProcess.GetExplicitEnvironment(startInfo))
        {
            environment.Add(new XElement("key", pair.Key));
            environment.Add(new XElement("string", pair.Value));
        }
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType(
                "plist",
                "-//Apple//DTD PLIST 1.0//EN",
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd",
                null),
            new XElement("plist",
                new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"),
                    new XElement("string", label),
                    new XElement("key", "Program"),
                    new XElement("string", executable),
                    new XElement("key", "ProgramArguments"),
                    programArguments,
                    new XElement("key", "WorkingDirectory"),
                    new XElement("string", startInfo.WorkingDirectory),
                    new XElement("key", "EnvironmentVariables"),
                    environment,
                    new XElement("key", "LimitLoadToSessionType"),
                    new XElement("string", "Aqua"),
                    new XElement("key", "KeepAlive"),
                    new XElement("dict",
                        new XElement("key", "SuccessfulExit"),
                        new XElement("false")),
                    new XElement("key", "ExitTimeOut"),
                    new XElement("integer", "10"),
                    new XElement("key", "Umask"),
                    new XElement("integer", "63"))));
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            OmitXmlDeclaration = false,
        }))
        {
            document.Save(writer);
        }
        return stream.ToArray();
    }

    private static long? TryReadLaunchdValue(string output, string name)
    {
        var prefix = $"\"{name}\" = ";
        foreach (var line in output.Split('\n'))
        {
            var candidate = line.Trim();
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal)
                || !candidate.EndsWith(';'))
            {
                continue;
            }
            var value = candidate[prefix.Length..^1];
            return long.TryParse(
                value,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
                    ? parsed
                    : null;
        }
        return null;
    }

    private static string? ComputeDefinitionIdentity(byte[]? definition)
        => definition is null
            ? null
            : Convert.ToHexString(SHA256.HashData(definition)).ToLowerInvariant();

    private static string GetDefaultLaunchAgentPath()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The current user's application data directory is unavailable.");
        }
        return Path.Combine(
            localApplicationData,
            "Sunder",
            "host",
            "launchd",
            $"{DefaultLabel}.plist");
    }

    private static void EnsureNotLink(FileSystemInfo entry, string description)
    {
        if (entry.LinkTarget is not null
            || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"The Sunder {description} is not a regular filesystem entry.");
        }
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    private static uint GetEffectiveUserId() => geteuid();
}
