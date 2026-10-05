namespace Sunder.Package.Format;

internal interface ISunderExecutionRuntimeArchiveInspectionObserver
{
    void ExtractionStarted();

    void ExtractionCompleted(bool succeeded, bool canceled);
}
