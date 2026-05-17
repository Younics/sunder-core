using Sunder.App.Models;
using Sunder.App.Services;
using Xunit;

namespace Sunder.App.Tests;

public sealed class AppStartupOptionsParserTests
{
    [Fact]
    public void Parse_WhenRuntimeUrlArgumentIsValid_NormalizesUrlAndMarksExplicit()
    {
        using var environment = PreserveRuntimeEnvironment();

        var options = AppStartupOptionsParser.Parse(["--runtime-url", "http://127.0.0.1:6000"]);

        Assert.True(options.HasExplicitRuntimeUrl);
        Assert.Equal(new Uri("http://127.0.0.1:6000/"), options.RuntimeUrl);
        Assert.Empty(options.ParseErrors);
    }

    [Fact]
    public void Parse_WhenRuntimeUrlArgumentIsInvalid_KeepsDefaultAndRecordsError()
    {
        using var environment = PreserveRuntimeEnvironment();

        var options = AppStartupOptionsParser.Parse(["--runtime-url", "file:///tmp/runtime"]);

        Assert.True(options.HasExplicitRuntimeUrl);
        Assert.Equal(RuntimeUrlHelper.Normalize(AppStartupOptions.DefaultRuntimeUrl), options.RuntimeUrl);
        Assert.Contains(options.ParseErrors, error => error.Contains("file:///tmp/runtime", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_WhenEnvironmentRuntimeUrlIsSet_UsesEnvironmentValue()
    {
        using var environment = PreserveRuntimeEnvironment();
        Environment.SetEnvironmentVariable("SUNDER_RUNTIME_URL", "https://runtime.example.test/api");

        var options = AppStartupOptionsParser.Parse([]);

        Assert.True(options.HasExplicitRuntimeUrl);
        Assert.Equal(new Uri("https://runtime.example.test/api/"), options.RuntimeUrl);
    }

    [Fact]
    public void Parse_WhenRuntimeHostPathArgumentIsMissing_RecordsError()
    {
        using var environment = PreserveRuntimeEnvironment();

        var options = AppStartupOptionsParser.Parse(["--runtime-host-path"]);

        Assert.Null(options.RuntimeHostPath);
        Assert.Contains("--runtime-host-path requires a file or directory path.", options.ParseErrors);
    }

    [Fact]
    public void Parse_WhenDevPackagesRepeat_DeduplicatesFullPaths()
    {
        using var environment = PreserveRuntimeEnvironment();
        var currentDirectory = Directory.GetCurrentDirectory();

        var options = AppStartupOptionsParser.Parse([
            "--dev-package", ".",
            "--dev-package=.",
        ]);

        Assert.Equal([currentDirectory], options.DevPackageFolders);
    }

    private static EnvironmentScope PreserveRuntimeEnvironment()
        => new("SUNDER_RUNTIME_URL", "SUNDER_RUNTIME_HOST_PATH");

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues;

        public EnvironmentScope(params string[] names)
        {
            _originalValues = names.ToDictionary(
                name => name,
                Environment.GetEnvironmentVariable,
                StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
