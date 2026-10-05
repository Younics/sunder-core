using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.Win32.SafeHandles;
using Sunder.Registry.Contracts;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

namespace Sunder.Runtime.Host.Services;

internal sealed record AppPolicyPeer(int ProcessId, RuntimeAuditToken? AuditToken);
internal enum AppPolicyProcessState { Alive, ExitProven, Unproven }
internal interface IAppPolicyProcessHandle : IDisposable
{
    AppPolicyProcessState Probe();
    void Terminate();
}
internal interface IAppPolicyProcessController
{
    IAppPolicyProcessHandle Attach(RuntimeAppProcessIdentity identity, AppPolicyPeer peer, bool recovery);
}

internal static class AppPolicyPeerIdentity
{
    internal static AppPolicyPeer Read(HttpContext context)
    {
        if (OperatingSystem.IsWindows() && context.Features.Get<IConnectionNamedPipeFeature>() is { } pipe)
        {
            using var server = WindowsIdentity.GetCurrent();
            var sameUser = false;
            pipe.NamedPipe.RunAsClient(() => { if (OperatingSystem.IsWindows()) { using var client = WindowsIdentity.GetCurrent(); sameUser = client.User == server.User; } });
            if (sameUser && GetNamedPipeClientProcessId(pipe.NamedPipe.SafePipeHandle, out var pid)) return new(checked((int)pid), null);
        }
        if (context.Features.Get<IConnectionSocketFeature>()?.Socket is { AddressFamily: AddressFamily.Unix } socket)
        {
            var handle = socket.SafeHandle;
            if (OperatingSystem.IsMacOS())
            {
                uint length = 32;
                if (getsockopt_audit(handle, 0, 6, out var token, ref length) == 0 && length == 32 && token.EffectiveUserId == geteuid())
                    return new(checked((int)token.ProcessId), token);
            }
            if (OperatingSystem.IsLinux())
            {
                uint length = 12;
                if (getsockopt_cred(handle, 1, 17, out var peer, ref length) == 0 && length == 12 && peer.UserId == geteuid())
                    return new(peer.ProcessId, null);
            }
        }
        throw new PackagePolicyDeniedException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
    }

    [StructLayout(LayoutKind.Sequential)] private struct UnixPeer { public int ProcessId; public uint UserId; public uint GroupId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)] private static extern int getsockopt_audit(SafeSocketHandle socket, int level, int option, out RuntimeAuditToken value, ref uint length);
    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)] private static extern int getsockopt_cred(SafeSocketHandle socket, int level, int option, out UnixPeer value, ref uint length);
    [DllImport("libc")] private static extern uint geteuid();
}

internal sealed class AppPolicyProcessController : IAppPolicyProcessController
{
    public IAppPolicyProcessHandle Attach(RuntimeAppProcessIdentity identity, AppPolicyPeer peer, bool recovery)
    {
        if (peer.ProcessId != identity.ProcessId || identity.ProcessId <= 0 || identity.ProcessId == Environment.ProcessId
            || identity.AppGenerationId == Guid.Empty || identity.RuntimeInstanceId == Guid.Empty
            || !RegistryActivePolicyClient.Hash(identity.ExecutableSha256))
            throw new PackagePolicyDeniedException(PackagePolicyErrorCodes.AppProcessSupervisionRequired);
        var handle = new AppPolicyNativeProcess(identity, peer);
        try
        {
            var status = handle.Probe();
            if (recovery && status == AppPolicyProcessState.ExitProven) return handle;
            if (status != AppPolicyProcessState.Alive) throw new InvalidDataException("App process identity cannot be proven.");
            if (recovery) return handle; // The durable record already authenticated the original image; updates may replace its on-disk bytes.
            if (RuntimeProcessIdentity.ExecutableHash(handle.Process!) != identity.ExecutableSha256) throw new InvalidDataException("App executable identity differs.");
            var executable = handle.Process!.MainModule!.FileName!;
            // Product App launches use the apphost. A generic dotnet process is not
            // accepted merely because it supplies an App assembly's build identity.
            if (Path.GetFileNameWithoutExtension(executable) != "Sunder.App" && Path.GetFileName(executable) != "Sunder.App")
                throw new InvalidDataException("An App apphost process is required.");
            var assembly = Path.Combine(Path.GetDirectoryName(executable)!, "Sunder.App.dll");
            if (RuntimeProcessIdentity.ReadBuildIdentity(assembly, "Sunder.App") != identity.AppBuildIdentity)
                throw new InvalidDataException("App build identity differs from its process image.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
}

// Signal kernel process identities, never a PID observed earlier: process handle on
// Windows, pidfd on Linux, and PID-version-bound audit token on Darwin.
internal sealed class AppPolicyNativeProcess : IAppPolicyProcessHandle
{
    private readonly RuntimeAppProcessIdentity _identity;
    private readonly AppPolicyPeer _peer;
    private readonly SafeFileHandle? _pidfd;
    internal Process? Process { get; }

    internal AppPolicyNativeProcess(RuntimeAppProcessIdentity identity, AppPolicyPeer peer)
    {
        _identity = identity; _peer = peer;
        try
        {
            Process = System.Diagnostics.Process.GetProcessById(identity.ProcessId);
            _ = Process.SafeHandle;
            if (RuntimeProcessIdentity.Birth(Process, peer.AuditToken) != identity.ProcessBirthIdentity) return;
            if (OperatingSystem.IsLinux())
            {
                int fd;
                try { fd = pidfd_open(identity.ProcessId, 0); }
                catch (EntryPointNotFoundException) { fd = checked((int)syscall_pidfd_open(434, identity.ProcessId, 0)); }
                if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                _pidfd = new SafeFileHandle(fd, ownsHandle: true);
            }
        }
        catch (ArgumentException) { }
        catch { Dispose(); throw; }
    }

    public AppPolicyProcessState Probe()
    {
        try
        {
            if (Process is null || Process.HasExited) return AppPolicyProcessState.ExitProven;
            if (OperatingSystem.IsMacOS())
            {
                if (_peer.AuditToken is not { } token) return AppPolicyProcessState.Unproven;
                var buffer = new byte[4096];
                if (proc_pidpath_audittoken(ref token, buffer, (uint)buffer.Length) <= 0)
                    return Marshal.GetLastPInvokeError() == 3 ? AppPolicyProcessState.ExitProven : AppPolicyProcessState.Unproven;
            }
            if (RuntimeProcessIdentity.Birth(Process, _peer.AuditToken) != _identity.ProcessBirthIdentity) return AppPolicyProcessState.ExitProven;
            return AppPolicyProcessState.Alive;
        }
        catch (ArgumentException) { return AppPolicyProcessState.ExitProven; }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException) { return AppPolicyProcessState.Unproven; }
    }

    public void Terminate()
    {
        if (Probe() != AppPolicyProcessState.Alive) return;
        if (OperatingSystem.IsWindows())
        {
            if (!TerminateProcess(Process!.SafeHandle, 1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        else if (OperatingSystem.IsLinux())
        {
            if (_pidfd is null) throw new InvalidOperationException("A stable Linux process handle is required.");
            int result;
            try { result = pidfd_send_signal(_pidfd, 9, IntPtr.Zero, 0); }
            catch (EntryPointNotFoundException) { result = checked((int)syscall_pidfd_send_signal(424, _pidfd, 9, IntPtr.Zero, 0)); }
            if (result != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        else if (OperatingSystem.IsMacOS() && _peer.AuditToken is { } token)
        {
            if (proc_signal_with_audittoken(ref token, 9) != 0 && Marshal.GetLastPInvokeError() != 3) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        else throw new PlatformNotSupportedException();
    }

    public void Dispose() { _pidfd?.Dispose(); Process?.Dispose(); }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("libc", SetLastError = true)] private static extern int pidfd_open(int pid, uint flags);
    [DllImport("libc", SetLastError = true)] private static extern int pidfd_send_signal(SafeFileHandle fd, int signal, IntPtr info, uint flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long syscall_pidfd_open(long number, int processId, uint flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)] private static extern long syscall_pidfd_send_signal(long number, SafeFileHandle fd, int signal, IntPtr info, uint flags);
    [DllImport("libproc.dylib", SetLastError = true)] private static extern int proc_pidpath_audittoken(ref RuntimeAuditToken token, byte[] buffer, uint size);
    [DllImport("libproc.dylib", SetLastError = true)] private static extern int proc_signal_with_audittoken(ref RuntimeAuditToken token, int signal);
}
