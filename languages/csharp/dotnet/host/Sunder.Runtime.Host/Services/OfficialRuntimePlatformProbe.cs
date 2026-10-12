using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Sunder.Runtime.Contracts;

namespace Sunder.Runtime.Host.Services;

internal interface IOfficialRuntimePlatformProbe
{
    Task VerifyAsync(RuntimeExecutionRuntimeVendorArtifact artifact, string rootPath, string launcherRelativePath, CancellationToken token);
}

internal sealed class OfficialRuntimePlatformProbe : IOfficialRuntimePlatformProbe
{
    internal static void ValidateCurrentPlatform(string rid, ConfiguredOfficialSourceId? source = null)
    {
        if (rid != PackageTargetSelection.GetCurrentRuntimeIdentifier()
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)
            || !(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()))
            throw new RuntimeExecutionRuntimeException(ExecutionRuntimeResolutionConflictCodes.RidUnavailable);
        if (OperatingSystem.IsLinux())
        {
            if (!NativeLibrary.TryLoad("libc.so.6", out var libc)) throw Missing("A glibc Linux host is required; musl is not an alias for linux-x64/linux-arm64.");
            try
            {
                if (!NativeLibrary.TryGetExport(libc, "gnu_get_libc_version", out var address)
                    || !Version.TryParse(Marshal.PtrToStringAnsi(Marshal.GetDelegateForFunctionPointer<GlibcVersion>(address)()), out var version)
                    || version < (source == ConfiguredOfficialSourceId.Node ? new Version(2, 28) : new Version(2, 27))
                    || source == ConfiguredOfficialSourceId.Node && Environment.OSVersion.Version < new Version(4, 18))
                    throw Missing(source == ConfiguredOfficialSourceId.Node
                        ? "Official Node 24 requires Linux kernel 4.18+ and glibc 2.28+."
                        : "Official .NET 10 requires glibc 2.27+.");
            }
            finally { NativeLibrary.Free(libc); }
        }
        if (OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
            throw Missing("Official managed runtimes require Windows 10/Server 2016 or later.");
        if (OperatingSystem.IsMacOS() && !OperatingSystem.IsMacOSVersionAtLeast(13, 5))
            throw Missing("Official Node 24 requires macOS 13.5 or later.");
        if (source == ConfiguredOfficialSourceId.DotNet && OperatingSystem.IsMacOS() && !OperatingSystem.IsMacOSVersionAtLeast(15))
            throw Missing("The current supported .NET 10 macOS line requires macOS 15 or later.");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GlibcVersion();

    public async Task VerifyAsync(RuntimeExecutionRuntimeVendorArtifact artifact, string rootPath, string launcherRelativePath, CancellationToken token)
    {
        ValidateCurrentPlatform(artifact.Identity.Rid, artifact.Identity.SourceId);
        var launcher = Path.GetFullPath(Path.Combine(rootPath, launcherRelativePath));
        PackageStorePathGuard.EnsureSafe(launcher, rootPath);
        await ValidateExecutableAsync(launcher, artifact.Identity.Rid, token).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var start = new ProcessStartInfo(launcher)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = Path.GetFullPath(rootPath),
        };
        ConfigureEnvironment(start.Environment, rootPath, WorkerProcessEnvironment.GetCurrentPlatform());
        if (artifact.Identity.SourceId == ConfiguredOfficialSourceId.DotNet) start.ArgumentList.Add("--list-runtimes");
        else
        {
            start.ArgumentList.Add("--eval");
            start.ArgumentList.Add("process.stdout.write(JSON.stringify({version:process.versions.node,arch:process.arch,platform:process.platform,icu:!!new Intl.DateTimeFormat('en-US').format(new Date(0))}))");
        }
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw Missing("The managed official executable could not start.");
            var stdout = ReadBoundedAsync(process.StandardOutput, deadline.Token);
            var stderr = ReadBoundedAsync(process.StandardError, deadline.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw Missing($"The official runtime native smoke failed (exit {process.ExitCode}); verify OS native dependencies. {await stderr}");
            var output = (await stdout).Trim();
            if (artifact.Identity.SourceId == ConfiguredOfficialSourceId.DotNet)
            {
                var expected = $"Microsoft.NETCore.App {artifact.Identity.VendorVersion} [{Path.Combine(PhysicalDirectory(rootPath), "shared", "Microsoft.NETCore.App")}]";
                if (!string.Equals(output, expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw Missing("The managed .NET host did not report the exact isolated Microsoft.NETCore.App runtime.");
            }
            else
            {
                using var result = JsonDocument.Parse(output);
                var expectedPlatform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
                var expectedArch = artifact.Identity.Rid.EndsWith("-arm64", StringComparison.Ordinal) ? "arm64" : "x64";
                if (result.RootElement.GetProperty("version").GetString() != artifact.Identity.VendorVersion
                    || result.RootElement.GetProperty("arch").GetString() != expectedArch
                    || result.RootElement.GetProperty("platform").GetString() != expectedPlatform
                    || !result.RootElement.GetProperty("icu").GetBoolean()) throw Missing("The managed Node runtime failed its exact native/version/ICU smoke.");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Missing("The official runtime native smoke exceeded its timeout."); }
        catch (System.ComponentModel.Win32Exception error) { throw Missing("The official executable needs compatible native OS dependencies: " + error.Message); }
        finally
        {
            try
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            }
            catch (InvalidOperationException) { }
        }
    }

    internal static void ConfigureEnvironment(IDictionary<string, string?> environment, string rootPath, WorkerProcessPlatform platform)
    {
        var inherited = new Dictionary<string, string?>(platform == WorkerProcessPlatform.Windows
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var variable in environment) inherited[variable.Key] = variable.Value;
        var root = Path.GetFullPath(rootPath);
        var baseline = WorkerProcessEnvironment.CreateBaseline(name => inherited.GetValueOrDefault(name),
            Path.GetDirectoryName(root)!, platform, allowDockerEnvironment: false);
        // Reuse the controlled Worker baseline, including Windows case-insensitive lookup.
        // No inherited Node/OpenSSL/.NET/loader injection setting enters the native probe.
        environment.Clear();
        foreach (var variable in baseline) environment[variable.Key] = variable.Value;
        environment["PATH"] = root;
        environment["DOTNET_ROOT"] = root;
        environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        environment["DOTNET_EnableDiagnostics"] = "0";
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > 64 * 1024) throw Missing("The official runtime smoke exceeded its output limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    private static string PhysicalDirectory(string directory)
    {
        // Vendor host reports physical ancestors (e.g. macOS /var -> /private/var).
        // Payload/root links were rejected above; this only canonicalizes the Host-owned destination.
        var full = Path.GetFullPath(directory);
        var current = Path.GetPathRoot(full)!;
        foreach (var segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return current;
    }

    private static async Task ValidateExecutableAsync(string path, string rid, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[64];
        await file.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var arm = rid.EndsWith("-arm64", StringComparison.Ordinal);
        bool valid;
        if (rid.StartsWith("win-", StringComparison.Ordinal))
        {
            var offset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(60));
            valid = header[0] == 'M' && header[1] == 'Z' && offset is >= 64 and <= 1024 * 1024 && offset + 24 <= file.Length;
            if (valid)
            {
                file.Position = offset;
                await file.ReadExactlyAsync(header.AsMemory(0, 24), token).ConfigureAwait(false);
                valid = BinaryPrimitives.ReadUInt32LittleEndian(header) == 0x4550
                    && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) == (arm ? 0xaa64 : 0x8664);
            }
        }
        else if (rid.StartsWith("osx-", StringComparison.Ordinal))
            valid = BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xfeedfacf
                && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) == (arm ? 0x0100000cU : 0x01000007U);
        else
            valid = header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F' })
                && header[4] == 2 && header[5] == 1
                && BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18)) == (arm ? 183 : 62);
        if (!valid) throw Missing("The official executable native architecture does not match its exact RID.");
    }

    private static RuntimeExecutionRuntimeException Missing(string message)
        => new(ExecutionRuntimeErrorCodes.PlatformPrerequisiteMissing, message);
}
