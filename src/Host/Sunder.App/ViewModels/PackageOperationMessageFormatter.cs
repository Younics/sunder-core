using Sunder.App.Services;
using Sunder.Protocol;

namespace Sunder.App.ViewModels;

internal static class PackageOperationMessageFormatter
{
    public static string BuildRegistryResultStatusText(
        RegistryPackageInstallExecutionResult result,
        bool hasWarnings)
    {
        if (!result.Success)
        {
            return result.Errors.FirstOrDefault() ?? result.Message;
        }

        var text = result.Message;
        if (hasWarnings)
        {
            text += " Review warnings below.";
        }

        return text;
    }

    public static string BuildLocalPackageOperationToastMessage(PackageOperationResult result, string fallback)
        => string.IsNullOrWhiteSpace(result.Message) ? fallback : result.Message.Trim();

    public static string BuildRegistryPackageOperationToastMessage(
        RegistryPackageInstallExecutionResult result,
        string fallback)
    {
        if (result.PlanItems.Count == 1)
        {
            var item = result.PlanItems[0];
            if (item.CurrentVersion is null)
            {
                return $"{item.PackageId} {item.Version} was installed.";
            }

            if (string.Equals(item.CurrentVersion, item.Version, StringComparison.OrdinalIgnoreCase))
            {
                return $"{item.PackageId} {item.Version} was reinstalled.";
            }

            return $"{item.PackageId} was updated from {item.CurrentVersion} to {item.Version}.";
        }

        var packageChangeCount = result.PlanItems
            .Select(item => item.PackageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (packageChangeCount > 0)
        {
            return $"{packageChangeCount} package change(s) completed.";
        }

        return string.IsNullOrWhiteSpace(result.Message) ? fallback : result.Message.Trim();
    }

    public static string BuildInstalledStatusText(
        PackageOperationResult? operationResult,
        int installedPackageCount,
        int activePackageCount,
        int disabledPackageCount,
        int failedPackageCount,
        int availableUpdateCount)
    {
        if (operationResult is null)
        {
            var status = $"{installedPackageCount} installed · {activePackageCount} active · {disabledPackageCount} disabled · {failedPackageCount} failed this session";
            if (availableUpdateCount > 0)
            {
                status += $" · {availableUpdateCount} update(s) available";
            }

            return status;
        }

        var message = operationResult.Success
            ? operationResult.Message ?? "Package operation completed."
            : operationResult.Errors.FirstOrDefault() ?? operationResult.Message ?? "Package operation failed.";

        if (operationResult.RequiresAppRestart)
        {
            message += operationResult.RuntimeSessionApplied
                ? " Restart Sunder to apply package UI changes."
                : " Restart Sunder to apply package changes.";
        }

        if (operationResult.Warnings.Count > 0)
        {
            message += " " + string.Join(" ", operationResult.Warnings);
        }

        return message;
    }
}
