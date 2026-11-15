namespace Sunder.Runtime.Host.Services;

// Native opening rules belong here, not in Worker protocol/lifecycle handling.
internal static partial class WorkerContentFile
{
    internal static FileStream OpenRead(string path)
        => OperatingSystem.IsWindows()
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan)
            : OpenUnixRead(path);
}
