using Sunder.App.Services;
using Sunder.App.ViewModels;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.App.Tests;

public sealed class DeveloperLogServiceTests
{
    [Fact]
    public void Enable_IncludesRecentApplicationErrors()
    {
        var marker = $"app-error-{Guid.NewGuid():N}";
        AppSessionLog.WriteError(marker, new InvalidOperationException("expected developer log exception"));
        using var developerLog = new DeveloperLogService();

        developerLog.Enable();

        Assert.Contains(
            developerLog.Snapshot(),
            entry => entry.Scope == DeveloperLogEntryScope.Application
                     && entry.Source == "application"
                     && entry.Message.Contains(marker, StringComparison.Ordinal)
                     && entry.Message.Contains("expected developer log exception", StringComparison.Ordinal));
    }

    [Fact]
    public void WritePackageLog_DoesNotDuplicateAsApplicationEntry()
    {
        var marker = $"package-error-{Guid.NewGuid():N}";
        using var developerLog = new DeveloperLogService();
        developerLog.Enable();

        developerLog.WritePackageLog(PackageLogLevel.Error, "local.package", marker);

        var matchingEntries = developerLog.Snapshot()
            .Where(entry => entry.Message.Contains(marker, StringComparison.Ordinal))
            .ToArray();
        var entry = Assert.Single(matchingEntries);
        Assert.Equal(DeveloperLogEntryScope.Package, entry.Scope);
        Assert.Equal("local.package", entry.Source);
    }

    [Fact]
    public void Enable_ClassifiesPackageScopedApplicationErrorsUnderPackageSource()
    {
        var marker = $"package-scoped-app-error-{Guid.NewGuid():N}";
        AppSessionLog.WriteError(
            marker,
            developerLogScope: DeveloperLogEntryScope.Package,
            developerLogSource: "faulted.package");
        using var developerLog = new DeveloperLogService();

        developerLog.Enable();

        var entry = Assert.Single(
            developerLog.Snapshot(),
            entry => entry.Message.Contains(marker, StringComparison.Ordinal));
        Assert.Equal(DeveloperLogEntryScope.Package, entry.Scope);
        Assert.Equal("faulted.package", entry.Source);
    }

    [Fact]
    public void DeveloperLogWindowViewModel_FiltersApplicationPackageAndSearchResults()
    {
        var marker = $"filter-{Guid.NewGuid():N}";
        using var developerLog = new DeveloperLogService();
        developerLog.Enable();
        developerLog.Info("package.session", $"{marker} application");
        developerLog.WritePackageLog(PackageLogLevel.Information, "package.one", $"{marker} first package");
        developerLog.WritePackageLog(PackageLogLevel.Error, "package.two", $"{marker} second package");
        using var viewModel = new DeveloperLogWindowViewModel(developerLog);

        viewModel.SearchText = marker;
        Assert.Equal(3, viewModel.FilteredEntries.Count);

        viewModel.SelectedFilter = FindFilter(viewModel, "app");
        var appEntry = Assert.Single(viewModel.FilteredEntries);
        Assert.Equal(DeveloperLogEntryScope.Application, appEntry.Scope);

        viewModel.SelectedFilter = FindFilter(viewModel, "packages");
        Assert.Equal(2, viewModel.FilteredEntries.Count);

        viewModel.SelectedFilter = FindFilter(viewModel, "package:package.one");
        var packageEntry = Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("package.one", packageEntry.Source);

        viewModel.SelectedFilter = FindFilter(viewModel, "all");
        viewModel.SearchText = "second package";
        var searchedEntry = Assert.Single(viewModel.FilteredEntries);
        Assert.Equal("package.two", searchedEntry.Source);
    }

    private static DeveloperLogFilterOptionViewModel FindFilter(DeveloperLogWindowViewModel viewModel, string key)
        => viewModel.FilterOptions.Single(option => string.Equals(option.Key, key, StringComparison.Ordinal));
}
