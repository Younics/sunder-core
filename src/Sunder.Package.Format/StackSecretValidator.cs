namespace Sunder.Package.Format;

internal static class StackSecretValidator
{
    public static void Validate(string stagingPath, ICollection<string> errors)
    {
        foreach (var finding in SunderStackSecretScanner.ScanExtractedStack(stagingPath))
        {
            errors.Add(finding);
        }
    }
}
