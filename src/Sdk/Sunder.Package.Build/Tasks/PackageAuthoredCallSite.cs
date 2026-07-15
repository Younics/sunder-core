namespace Sunder.Package.Build.Tasks;

internal static class PackageAuthoredCallSite
{
    public static string Get(string typeName, string methodName)
    {
        var sourceMethod = ReadGeneratedSourceMethod(methodName);
        var nestedSeparator = typeName.LastIndexOf('+');
        if (nestedSeparator >= 0)
        {
            sourceMethod ??= ReadGeneratedSourceMethod(typeName[(nestedSeparator + 1)..]);
            if (sourceMethod is not null)
            {
                typeName = typeName[..nestedSeparator];
            }
        }
        return $"{typeName}.{sourceMethod ?? methodName}";
    }

    private static string? ReadGeneratedSourceMethod(string value)
    {
        if (!value.StartsWith('<'))
        {
            return null;
        }
        var end = value.IndexOf('>');
        return end > 1 ? value[1..end] : null;
    }
}
