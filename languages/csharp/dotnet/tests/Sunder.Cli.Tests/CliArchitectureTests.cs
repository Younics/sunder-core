namespace Sunder.Cli.Tests;

public sealed class CliArchitectureTests
{
    [Fact]
    public void Production_cli_source_files_stay_focused()
    {
        var files = Directory.GetFiles(FindCliDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        Assert.All(files, path => Assert.True(
            File.ReadLines(path).Count() < 500,
            $"{Path.GetFileName(path)} should stay below 500 lines."));
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
        foreach (var file in files.Where(path => !path.EndsWith("RegistryClient.Publish.cs", StringComparison.Ordinal)))
            Assert.DoesNotContain("Headers.Authorization", File.ReadAllText(file), StringComparison.Ordinal);
        var automationPublisher = File.ReadAllText(Path.Combine(directory, "RegistryClient.Publish.cs"));
        Assert.Contains("Headers.Authorization", automationPublisher, StringComparison.Ordinal);
        Assert.Contains("RegistryPublishCredential", automationPublisher, StringComparison.Ordinal);
        Assert.DoesNotContain("sunder_cli_", automationPublisher, StringComparison.Ordinal);
        foreach (var file in files.Where(path => !path.EndsWith("RegistryClient.cs", StringComparison.Ordinal)))
            Assert.DoesNotContain("new HttpClient", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_runtime_client_prefers_managed_host_connection()
    {
        var source = File.ReadAllText(Path.Combine(FindCliDirectory(), "CliClients.cs"));

        Assert.Contains("HostConnectionInfoStore.LoadPreferredFor(runtimeUrl)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryAndRuntimeClients_KeepEndpointFamiliesSeparateFromTransport()
    {
        var cliDirectory = FindCliDirectory();
        var root = FindRepositoryRoot();
        var runtimeClientDirectory = Path.Combine(
            root,
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.Runtime.Client");
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
            ["RuntimeStatusCommandHandler.cs"] = "ICliRuntimeSystemClient",
            ["RuntimeResetCommandHandler.cs"] = "ICliRuntimeResetClient",
            ["PackageCommandHandler.cs"] = "ICliRuntimePackageClient",
            ["PackageSettingsCommandHandler.cs"] = "ICliRuntimePackageSettingsClient",
            ["PackageAuthCommandHandler.cs"] = "ICliRuntimePackageAuthClient",
            ["RuntimeStackCommandHandler.cs"] = "ICliRuntimeStackClient",
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

        var lifecycle = File.ReadAllText(Path.Combine(directory, "RuntimeLifecycleCommandHandler.cs"));
        var status = File.ReadAllText(Path.Combine(directory, "RuntimeStatusCommandHandler.cs"));
        Assert.Contains("ICliHostLifecycleClient", lifecycle, StringComparison.Ordinal);
        Assert.Contains("ICliHostStatusClient", status, StringComparison.Ordinal);
    }

    private static string FindCliDirectory()
        => Path.Combine(
            FindRepositoryRoot(),
            "languages",
            "csharp",
            "dotnet",
            "host",
            "Sunder.Cli");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root.");
    }
}
