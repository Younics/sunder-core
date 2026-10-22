using System.Globalization;

namespace Sunder.Package.Build.Tasks;

// A private watch session owns this file until all compiler/publisher children drain.
// Builder changes it in place with FileShare.None before cancelling a superseded child.
// Hold that same exclusive handle through the existing output transaction: checking
// a revision and then releasing the handle before Commit would be a publication race.
internal static class SunderDevelopmentPublication
{
    internal static FileStream? Acquire(string path, string revision)
    {
        if (path.Length == 0 && revision.Length == 0) return null;
        if (!Path.IsPathFullyQualified(path) || !long.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new InvalidDataException("package-build.watch-publication-invalid: An exact watch revision and absolute revision file are required.");
        GeneratedOutputPathSafety.EnsureSafeAncestors(path, includeLeaf: true, "watch publication revision");
        var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            if (file.Length > 32) throw new InvalidDataException("The watch publication revision is invalid.");
            using var reader = new StreamReader(file, leaveOpen: true);
            if (reader.ReadToEnd() != revision + "\n")
                throw new InvalidDataException("package-build.watch-superseded: Source changed before development output publication.");
            return file;
        }
        catch { file.Dispose(); throw; }
    }
}
