using System.Security.Cryptography;
using System.Text;

namespace Sunder.App.Services;

internal static class AppWebViewProfile
{
    // WebView2's databases/cache paths are not all long-path aware. Reserve space
    // below MAX_PATH for those native descendants, not just the UDF itself. The
    // pinned runtime can start successfully with an overlong descendant and then
    // stall during shutdown while still owning the profile lock.
    internal const int WindowsMaximumUserDataPathLength = 170;

    internal static string ForPackage(string stateRoot, string packageId, Guid activationId, bool windows)
    {
        var root = Path.GetFullPath(stateRoot);
        if (windows)
        {
            // Full SHA-256, encoded without path separators: package and exact
            // activation remain isolated without unbounded package-id nesting.
            var identity = Encoding.UTF8.GetBytes(packageId + "\0" + activationId.ToString("N"));
            var leaf = Convert.ToBase64String(SHA256.HashData(identity)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var path = Path.Combine(root, "webview-data", leaf);
            ValidateWindowsPath(path);
            return path;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var segment = new string(packageId.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return Path.Combine(root, "webview-data", segment, activationId.ToString("N"));
    }

    internal static void ValidateWindowsPath(string path)
    {
        if (path.Length > WindowsMaximumUserDataPathLength)
            throw new PathTooLongException(
                $"The package browser profile path exceeds {WindowsMaximumUserDataPathLength} characters, leaving insufficient space for WebView2 data. Select a shorter {AppLocalState.StateRootEnvironmentVariable}; browser data is not relocated outside that root.");
    }
}
