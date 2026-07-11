namespace Sunder.Cli.Tests;

public sealed class CliArchitectureTests
{
    [Fact]
    public void Every_cli_production_source_file_stays_below_500_lines()
    {
        var directory = FindCliDirectory();
        var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var violations = files
            .Select(path => new { Path = path, Lines = File.ReadLines(path).Count() })
            .Where(file => file.Lines >= 500)
            .ToArray();
        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations.Select(file => $"{file.Path}: {file.Lines} lines")));
    }

    [Fact]
    public void Cli_has_no_runtime_transport_mutation_or_credential_implementation()
    {
        var directory = FindCliDirectory();
        var files = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        var allSource = string.Join('\n', files.Select(File.ReadAllText));
        Assert.DoesNotContain("PackageStoreMutationRequest", allSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeConnectionInfoStore", allSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BearerToken", allSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Headers.Authorization", allSource, StringComparison.Ordinal);
        foreach (var file in files.Where(path => !path.EndsWith("RegistryClient.cs", StringComparison.Ordinal)))
            Assert.DoesNotContain("new HttpClient", File.ReadAllText(file), StringComparison.Ordinal);
    }

    private static string FindCliDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Host", "Sunder.Cli");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate src/Host/Sunder.Cli.");
    }
}
