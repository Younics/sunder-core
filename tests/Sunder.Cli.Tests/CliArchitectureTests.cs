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

    [Fact]
    public void RegistryAndRuntimeClients_KeepEndpointFamiliesSeparateFromTransport()
    {
        var cliDirectory = FindCliDirectory();
        var root = new DirectoryInfo(cliDirectory).Parent!.Parent!.Parent!.FullName;
        var runtimeClientDirectory = Path.Combine(root, "src", "Host", "Sunder.Runtime.Client");
        var endpointFamilyFiles = new[]
        {
            Path.Combine(cliDirectory, "RegistryClient.Packages.cs"),
            Path.Combine(cliDirectory, "RegistryClient.Stacks.cs"),
            Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.System.cs"),
            Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.Registry.cs"),
            Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.Packages.cs"),
            Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.PackageCommit.cs"),
            Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.Snapshots.cs"),
        };
        var transportAndCompositionFiles = new[]
        {
            Path.Combine(cliDirectory, "RegistryClient.cs"),
            Path.Combine(cliDirectory, "CliHttpContentReader.cs"),
            Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.cs"),
            Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.Policy.cs"),
            Path.Combine(runtimeClientDirectory, "VerifiedFileTransfer.cs"),
        };

        Assert.All(endpointFamilyFiles, path => Assert.True(File.Exists(path), $"Missing client endpoint family {path}."));
        Assert.All(transportAndCompositionFiles, path => Assert.True(File.Exists(path), $"Missing client transport or composition boundary {path}."));
        var baselines = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [Path.Combine(cliDirectory, "RegistryClient.cs")] = 113,
            [Path.Combine(cliDirectory, "RegistryClient.Packages.cs")] = 25,
            [Path.Combine(cliDirectory, "RegistryClient.Stacks.cs")] = 59,
            [Path.Combine(cliDirectory, "CliHttpContentReader.cs")] = 46,
            [Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.cs")] = 61,
            [Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.System.cs")] = 16,
            [Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.Registry.cs")] = 59,
            [Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.Packages.cs")] = 53,
            [Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.PackageCommit.cs")] = 53,
            [Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.Snapshots.cs")] = 20,
            [Path.Combine(runtimeClientDirectory, "RuntimeManagementClient.Policy.cs")] = 9,
            [Path.Combine(runtimeClientDirectory, "VerifiedFileTransfer.cs")] = 126,
        };

        Assert.All(baselines, pair => Assert.True(
            File.ReadLines(pair.Key).Count() <= pair.Value,
            $"{pair.Key} exceeded its {pair.Value}-line ratchet."));

        Assert.All(
            Directory.GetFiles(runtimeClientDirectory, "*.cs"),
            path => Assert.True(
                File.ReadLines(path).Count() < 500,
                $"{path} exceeded the 500-line Runtime client ratchet."));

        var stackClientSource = File.ReadAllText(Path.Combine(cliDirectory, "RegistryClient.Stacks.cs"));
        Assert.Contains("VerifiedFileTransfer.PublishAsync", stackClientSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SHA256.HashDataAsync", stackClientSource, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryCapabilityInterfaces_HaveNoDefaultsAndHandlersUseNarrowRoles()
    {
        Assert.All(
            typeof(IRegistryConnection).Assembly.GetTypes()
                .Where(type => type.IsInterface && typeof(IRegistryConnection).IsAssignableFrom(type))
                .SelectMany(type => type.GetMethods(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly)),
            method => Assert.True(method.IsAbstract, $"{method.DeclaringType?.Name}.{method.Name} has a default implementation."));

        var directory = FindCliDirectory();
        Assert.Contains("IRegistryBrowseClient", File.ReadAllText(Path.Combine(directory, "RegistryBrowseCommandHandler.cs")), StringComparison.Ordinal);
        Assert.Contains("IRegistryManageClient", File.ReadAllText(Path.Combine(directory, "DeveloperPublishCommandHandler.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("IRegistryClient", File.ReadAllText(Path.Combine(directory, "RegistryBrowseCommandHandler.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain("IRegistryClient", File.ReadAllText(Path.Combine(directory, "DeveloperPublishCommandHandler.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeCapabilityInterfaces_HaveNoDefaultsAndHandlersUseNarrowRoles()
    {
        Assert.All(
            typeof(ICliRuntimeClientRole).Assembly.GetTypes()
                .Where(type => type.IsInterface && typeof(ICliRuntimeClientRole).IsAssignableFrom(type))
                .SelectMany(type => type.GetMethods(
                    System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.DeclaredOnly)),
            method => Assert.True(method.IsAbstract, $"{method.DeclaringType?.Name}.{method.Name} has a default implementation."));

        var directory = FindCliDirectory();
        var expectedRoles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SystemCommandHandler.cs"] = "ICliRuntimeSystemClient",
            ["RuntimeResetCommandHandler.cs"] = "ICliRuntimeResetClient",
            ["PackageCommandHandler.cs"] = "ICliRuntimePackageClient",
            ["RegistryAuthCommandHandler.cs"] = "ICliRuntimeAuthClient",
            ["RegistryManagementCommandHandler.cs"] = "ICliRuntimeManagementClient",
            ["DeveloperPublishCommandHandler.cs"] = "ICliRuntimePublishClient",
        };
        Assert.All(expectedRoles, pair =>
        {
            var source = File.ReadAllText(Path.Combine(directory, pair.Key));
            Assert.Contains(pair.Value, source, StringComparison.Ordinal);
            Assert.DoesNotContain("ICliRuntimeClient runtime", source, StringComparison.Ordinal);
        });
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
