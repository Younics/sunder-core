namespace Sunder.Package.Format;

internal interface ISunderPackageValidationIoObserver
{
    void Record(long bytesRead, long bytesHashed);
}

internal static class SunderPackageValidationIoObserverExtensions
{
    public static void TryRecord(
        this ISunderPackageValidationIoObserver? observer,
        long bytesRead,
        long bytesHashed)
    {
        try
        {
            observer?.Record(bytesRead, bytesHashed);
        }
        catch
        {
            // Diagnostics must not affect archive validation.
        }
    }
}
