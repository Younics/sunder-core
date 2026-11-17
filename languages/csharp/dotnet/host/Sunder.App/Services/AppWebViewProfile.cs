using System.Security.Cryptography;
using System.Text;

namespace Sunder.App.Services;

internal static class AppWebViewProfile
{
    internal static string ForView(string activationRoot, string viewId)
        => Path.Combine(activationRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(viewId))).ToLowerInvariant());
    // WebView2's databases/cache paths are not all long-path aware. Reserve space
    // below MAX_PATH for those native descendants, not just the UDF itself. The
    // pinned runtime can start successfully with an overlong descendant and then
    // stall during shutdown while still owning the profile lock.
    internal const int WindowsMaximumUserDataPathLength = 170;
    // The previous fixed profile name had 14 characters; independent profiles
    // use "view-" plus a 32-character GUID. Reserve the additional 23 characters
    // within the same native descendant budget, not by lengthening that budget.
    internal const int WindowsMaximumSharedUserDataPathLength = WindowsMaximumUserDataPathLength - 23;

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

    internal static void ValidateWindowsPath(string path, int maximumLength = WindowsMaximumUserDataPathLength)
    {
        if (path.Length > maximumLength)
            throw new PathTooLongException(
                $"The package browser profile path exceeds {maximumLength} characters, leaving insufficient space for WebView2 data. Select a shorter {AppLocalState.StateRootEnvironmentVariable}; browser data is not relocated outside that root.");
    }
}
