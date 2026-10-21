using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using static Sunder.Core.TestSupport.SdkCorpusInput;

namespace Sunder.Core.TestSupport;

/// <summary>Offline, flat npm lock-v3 profile. No install scripts, ambient cache or registry.</summary>
public static class SdkCorpusNpm
{
    public static void ExtractPackage(string archive, string destination)
    {
        Require(!Directory.Exists(destination), "npm extraction requires a fresh directory.");
        Directory.CreateDirectory(destination);
        using var source = File.OpenRead(archive);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        var count = 0;
        string? archiveRoot = null;
        while (reader.GetNextEntry() is { } entry)
        {
            var separator = entry.Name.IndexOf('/');
            Require(++count <= 8192 && separator is > 0 and <= 128, "Invalid npm tar root/inventory: " + entry.Name);
            var prefix = entry.Name[..separator];
            // DefinitelyTyped also publishes single-root 'node vX.Y.Z/' tarballs.
            Require(prefix is not ("." or "..") && !prefix.Contains('\\') && (archiveRoot is null || prefix == archiveRoot), "Ambiguous npm archive root.");
            archiveRoot = prefix;
            var relative = entry.Name[(separator + 1)..].TrimEnd('/');
            if (relative.Length == 0 && entry.EntryType == TarEntryType.Directory) continue;
            var target = Contained(destination, relative);
            Require(seen.Add(relative), "Duplicate npm tar path.");
            Require(entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory,
                "Linked/special npm tar member.");
            bytes = checked(bytes + entry.Length);
            Require(bytes <= 256L * 1024 * 1024, "npm package expansion exceeds bound.");
            if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var output = new FileStream(target, FileMode.CreateNew)) entry.DataStream!.CopyTo(output);
            Require(new FileInfo(target).Length == entry.Length, "Truncated npm member.");
            if (!OperatingSystem.IsWindows() && (entry.Mode & UnixFileMode.UserExecute) != 0)
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Require(seen.Contains("package.json"), "Missing npm identity.");
    }

    public static string Integrity(string archive)
    {
        using var stream = File.OpenRead(archive);
        return "sha512-" + Convert.ToBase64String(SHA512.HashData(stream));
    }

    public static void RequireIntegrity(string archive, string expected)
        => Require(Integrity(archive) == expected, "Retained npm lock integrity differs: " + Path.GetFileName(archive));

    public static bool Applies(JsonElement package, string os, string cpu)
    {
        static bool Allows(JsonElement package, string field, string actual) => !package.TryGetProperty(field, out var values)
            || (!values.EnumerateArray().Any(x => x.GetString() == "!" + actual)
                && (!values.EnumerateArray().Any(x => !x.GetString()!.StartsWith('!')) || values.EnumerateArray().Any(x => x.GetString() == actual)));
        return Allows(package, "os", os) && Allows(package, "cpu", cpu);
    }

    public static string PackageName(string lockPath)
    {
        Require(lockPath.StartsWith("node_modules/", StringComparison.Ordinal), "Non-package npm lock path.");
        var name = lockPath[13..];
        _ = Contained(Path.GetTempPath(), name);
        Require(name.StartsWith('@') ? name.Split('/').Length == 2 : !name.Contains('/'), "Nested/link npm trees require another reviewed profile.");
        return name;
    }
}
