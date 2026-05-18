using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Sunder.Runtime.Host.Services;

internal sealed class PackageSessionFileMaterializer
{
    private readonly Dictionary<FileFingerprint, string> _materializedFiles = new();

    public void MaterializeDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var filePath in Directory.GetFiles(sourceDirectory))
        {
            MaterializeFile(filePath, Path.Combine(destinationDirectory, Path.GetFileName(filePath)));
        }

        foreach (var directoryPath in Directory.GetDirectories(sourceDirectory))
        {
            MaterializeDirectory(directoryPath, Path.Combine(destinationDirectory, Path.GetFileName(directoryPath)));
        }
    }

    private void MaterializeFile(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var fingerprint = FileFingerprint.Create(sourcePath);
        if (_materializedFiles.TryGetValue(fingerprint, out var existingPath)
            && TryCreateHardLink(existingPath, destinationPath))
        {
            return;
        }

        File.Copy(sourcePath, destinationPath, overwrite: true);
        _materializedFiles[fingerprint] = destinationPath;
    }

    private static bool TryCreateHardLink(string existingPath, string destinationPath)
    {
        try
        {
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            if (OperatingSystem.IsWindows())
            {
                return CreateHardLink(destinationPath, existingPath, IntPtr.Zero);
            }

            return UnixLink(existingPath, destinationPath) == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int UnixLink(string oldPath, string newPath);

    private readonly record struct FileFingerprint(long Length, string Sha256)
    {
        public static FileFingerprint Create(string path)
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return new FileFingerprint(stream.Length, Convert.ToHexString(hash));
        }
    }
}
