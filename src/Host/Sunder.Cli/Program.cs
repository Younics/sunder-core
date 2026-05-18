using Sunder.PackageManagement;
using Sunder.Protocol;
using Sunder.Registry.Shared;

namespace Sunder.Cli;

internal static class Program
{
    private const int Success = 0;
    private const int Failure = 1;

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            return await RunAsync(args.ToList(), cancellation.Token);
        }
        catch (CliTimeoutException ex)
        {
            ConsoleOutput.WriteError(ex.Message);
            return Failure;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            ConsoleOutput.WriteError("Operation timed out. Use --timeout <duration> to increase the registry request timeout.");
            return Failure;
        }
        catch (OperationCanceledException)
        {
            ConsoleOutput.WriteWarning("Operation cancelled.");
            return Failure;
        }
        catch (ArgumentException ex)
        {
            ConsoleOutput.WriteError(ex.Message);
            return Failure;
        }
        catch (HttpRequestException ex)
        {
            ConsoleOutput.WriteError($"HTTP request failed: {ex.Message}");
            return Failure;
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteError(ex.Message);
            return Failure;
        }
    }

    private static async Task<int> RunAsync(List<string> args, CancellationToken cancellationToken)
    {
        if (CommandLine.ConsumeFlag(args, "--help") || CommandLine.ConsumeFlag(args, "-h"))
        {
            PrintHelp();
            return Success;
        }

        var options = CliOptions.Parse(args);
        if (args.Count == 0)
        {
            PrintHelp();
            return Success;
        }

        var command = args[0].ToLowerInvariant();
        args.RemoveAt(0);

        using var registryClient = new RegistryClient(options.RegistryApiUrl, options.RegistryTimeout);
        using var runtimeClient = new RuntimeClient(options.RuntimeUrl);

        return command switch
        {
            "auth" => await AuthAsync(args, registryClient, options.RegistryWebUrl, cancellationToken),
            "search" => await SearchAsync(args, registryClient, cancellationToken),
            "info" => await InfoAsync(args, registryClient, cancellationToken),
            "list" => await ListInstalledAsync(args, runtimeClient, cancellationToken),
            "install" => await InstallAsync(args, registryClient, runtimeClient, cancellationToken),
            "update" => await UpdateAsync(args, registryClient, runtimeClient, cancellationToken),
            "publish" => await PublishAsync(args, registryClient, cancellationToken),
            "yank" => await SetYankedAsync(args, isYanked: true, registryClient, cancellationToken),
            "unyank" => await SetYankedAsync(args, isYanked: false, registryClient, cancellationToken),
            "deprecate" => await SetDeprecationAsync(args, clear: false, registryClient, cancellationToken),
            "undeprecate" => await SetDeprecationAsync(args, clear: true, registryClient, cancellationToken),
            "dist-tag" => await DistTagAsync(args, registryClient, cancellationToken),
            "validate" => await ValidatePackageAsync(args, cancellationToken),
            "package" => await PackageAsync(args, cancellationToken),
            _ => throw new ArgumentException($"Unknown command '{command}'. Run 'sunder --help' for usage.")
        };
    }

    private static async Task<int> AuthAsync(
        List<string> args,
        RegistryClient registryClient,
        Uri registryWebUrl,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("Usage: sunder auth <login|status|logout>");
        }

        var command = args[0].ToLowerInvariant();
        args.RemoveAt(0);
        return command switch
        {
            "login" => await AuthLoginAsync(args, registryClient, registryWebUrl, cancellationToken),
            "status" => await AuthStatusAsync(args, registryClient, cancellationToken),
            "logout" => AuthLogout(args, registryClient),
            _ => throw new ArgumentException("Usage: sunder auth <login|status|logout>")
        };
    }

    private static async Task<int> AuthLoginAsync(
        List<string> args,
        RegistryClient registryClient,
        Uri registryWebUrl,
        CancellationToken cancellationToken)
    {
        CommandLine.EnsureNoExtraArguments(args, "Usage: sunder auth login");
        var flow = new CliBrowserAuthFlow(registryWebUrl, registryClient);
        var result = await flow.LoginAsync(cancellationToken);
        var store = CliAuthStore.Load();
        store.SetToken(registryClient.RegistryUrl, result.Token, result.UserId, result.ExpiresAtUtc);
        store.Save();

        ConsoleOutput.WriteSuccess($"Signed in to {registryClient.RegistryUrl} as {result.UserId ?? "registry user"}.");
        if (result.ExpiresAtUtc is not null)
        {
            ConsoleOutput.WriteInfo($"CLI token expires {result.ExpiresAtUtc.Value.LocalDateTime:g}.");
        }

        return Success;
    }

    private static async Task<int> AuthStatusAsync(List<string> args, RegistryClient registryClient, CancellationToken cancellationToken)
    {
        CommandLine.EnsureNoExtraArguments(args, "Usage: sunder auth status");
        var token = CliAuthStore.Load().GetToken(registryClient.RegistryUrl);
        if (token is null)
        {
            ConsoleOutput.WriteInfo($"Not signed in to {registryClient.RegistryUrl}. Run 'sunder auth login'.");
            return Success;
        }

        var user = await registryClient.GetCurrentUserAsync(token.Token, cancellationToken);
        if (user is null)
        {
            ConsoleOutput.WriteWarning($"Saved token for {registryClient.RegistryUrl} is invalid or expired. Run 'sunder auth login'.");
            return Failure;
        }

        ConsoleOutput.WriteSuccess($"Signed in to {registryClient.RegistryUrl} as {user.DisplayName ?? user.UserId}.");
        if (token.ExpiresAtUtc is not null)
        {
            ConsoleOutput.WriteInfo($"CLI token expires {token.ExpiresAtUtc.Value.LocalDateTime:g}.");
        }

        return Success;
    }

    private static int AuthLogout(List<string> args, RegistryClient registryClient)
    {
        CommandLine.EnsureNoExtraArguments(args, "Usage: sunder auth logout");
        var store = CliAuthStore.Load();
        if (store.RemoveToken(registryClient.RegistryUrl))
        {
            store.Save();
            ConsoleOutput.WriteSuccess($"Signed out from {registryClient.RegistryUrl}.");
            return Success;
        }

        ConsoleOutput.WriteInfo($"No saved sign-in for {registryClient.RegistryUrl}.");
        return Success;
    }

    private static async Task<int> SearchAsync(List<string> args, RegistryClient registryClient, CancellationToken cancellationToken)
    {
        var skip = CommandLine.ConsumeInt32Option(args, "--skip", 0);
        var take = CommandLine.ConsumeInt32Option(args, "--take", 20);
        if (args.Count > 1)
        {
            throw new ArgumentException("Usage: sunder search [query] [--skip <count>] [--take <count>]");
        }

        var query = args.Count == 1 ? args[0] : null;
        var packages = await registryClient.SearchAsync(query, skip, take, cancellationToken);
        if (packages.Count == 0)
        {
            ConsoleOutput.WriteInfo("No packages found.");
            return Success;
        }

        WritePackageSummaries(packages);
        return Success;
    }

    private static async Task<int> InfoAsync(List<string> args, RegistryClient registryClient, CancellationToken cancellationToken)
    {
        var version = CommandLine.ConsumeOption(args, "--version");
        var packageId = CommandLine.RequireSingleArgument(args, "Usage: sunder info <package-id> [--version <version>]");

        if (!string.IsNullOrWhiteSpace(version))
        {
            var packageVersion = await registryClient.GetVersionAsync(packageId, version, cancellationToken);
            if (packageVersion is null)
            {
                ConsoleOutput.WriteError($"Package '{packageId}' {version} was not found.");
                return Failure;
            }

            WritePackageVersion(packageVersion);
            return Success;
        }

        var package = await registryClient.GetPackageAsync(packageId, cancellationToken);
        if (package is null)
        {
            ConsoleOutput.WriteError($"Package '{packageId}' was not found.");
            return Failure;
        }

        WritePackageDetails(package);
        return Success;
    }

    private static async Task<int> ListInstalledAsync(List<string> args, RuntimeClient runtimeClient, CancellationToken cancellationToken)
    {
        CommandLine.EnsureNoExtraArguments(args, "Usage: sunder list");

        var installedPackages = await runtimeClient.GetInstalledPackagesAsync(cancellationToken);
        if (installedPackages.Count == 0)
        {
            ConsoleOutput.WriteInfo("No packages installed.");
            return Success;
        }

        WriteInstalledPackages(installedPackages);
        return Success;
    }

    private static async Task<int> InstallAsync(
        List<string> args,
        RegistryClient registryClient,
        RuntimeClient runtimeClient,
        CancellationToken cancellationToken)
    {
        var packagePath = CommandLine.ConsumeOption(args, "--file");
        var version = CommandLine.ConsumeOption(args, "--version");
        var tagOption = CommandLine.ConsumeOption(args, "--tag");
        var tag = tagOption ?? "latest";
        var allowDowngrade = CommandLine.ConsumeFlag(args, "--allow-downgrade");
        var reinstall = CommandLine.ConsumeFlag(args, "--reinstall");

        if (!string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(tagOption))
        {
            throw new ArgumentException("Use either '--version' or '--tag', not both.");
        }

        if (!string.IsNullOrWhiteSpace(packagePath))
        {
            if (!string.IsNullOrWhiteSpace(version) || !string.IsNullOrWhiteSpace(tagOption))
            {
                throw new ArgumentException("Local file installs do not use '--version' or '--tag'.");
            }

            CommandLine.EnsureNoExtraArguments(args, "Usage: sunder install --file <package.sunderpkg> [--allow-downgrade] [--reinstall]");
            return await InstallFromFileAsync(packagePath, allowDowngrade, reinstall, runtimeClient, cancellationToken);
        }

        var packageId = CommandLine.RequireSingleArgument(
            args,
            "Usage: sunder install <package-id> [--version <version>|--tag <tag>] [--allow-downgrade] [--reinstall]");

        var installedPackages = await runtimeClient.GetInstalledPackagesAsync(cancellationToken);
        var request = new RegistryResolveInstallPlanRequest(
            packageId,
            version,
            string.IsNullOrWhiteSpace(version) ? tag : null,
            ToInstalledPackageStates(installedPackages),
            AllowDowngrade: allowDowngrade,
            Reinstall: reinstall);
        var plan = await registryClient.ResolveInstallPlanAsync(request, cancellationToken);
        if (!plan.Success)
        {
            WriteInstallPlanFailure(plan);
            return Failure;
        }

        return await ExecuteInstallPlanAsync(plan, allowDowngrade, reinstall, registryClient, runtimeClient, cancellationToken);
    }

    private static async Task<int> UpdateAsync(
        List<string> args,
        RegistryClient registryClient,
        RuntimeClient runtimeClient,
        CancellationToken cancellationToken)
    {
        var all = CommandLine.ConsumeFlag(args, "--all");
        var includePrerelease = CommandLine.ConsumeFlag(args, "--include-prerelease");
        if (args.Count > 1 || (all && args.Count == 1))
        {
            throw new ArgumentException("Usage: sunder update [package-id|--all] [--include-prerelease]");
        }

        var requestedPackageId = args.Count == 1 ? args[0] : null;
        var installedPackages = await runtimeClient.GetInstalledPackagesAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(requestedPackageId))
        {
            installedPackages = installedPackages
                .Where(package => string.Equals(package.PackageId, requestedPackageId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (installedPackages.Count == 0)
            {
                ConsoleOutput.WriteError($"Package '{requestedPackageId}' is not installed.");
                return Failure;
            }
        }

        if (installedPackages.Count == 0)
        {
            ConsoleOutput.WriteInfo("No packages installed.");
            return Success;
        }

        var request = new RegistryResolveUpdatesRequest(
            installedPackages.Select(package => new RegistryInstalledPackage(package.PackageId, package.Version)).ToArray(),
            includePrerelease);
        var response = await registryClient.ResolveUpdatesAsync(request, cancellationToken);

        if (response.Updates.Count == 0)
        {
            ConsoleOutput.WriteInfo("All selected packages are up to date.");
            return Success;
        }

        var exitCode = Success;
        foreach (var update in response.Updates)
        {
            ConsoleOutput.WriteInfo($"Updating {update.PackageId} {update.CurrentVersion} -> {update.AvailableVersion}...");
            var planRequest = new RegistryResolveInstallPlanRequest(
                update.PackageId,
                update.AvailableVersion,
                null,
                ToInstalledPackageStates(installedPackages),
                includePrerelease);
            var plan = await registryClient.ResolveInstallPlanAsync(planRequest, cancellationToken);
            var operationExitCode = plan.Success
                ? await ExecuteInstallPlanAsync(plan, allowDowngrade: false, reinstall: false, registryClient, runtimeClient, cancellationToken)
                : WriteInstallPlanFailure(plan);
            if (operationExitCode != Success)
            {
                exitCode = operationExitCode;
            }
            else
            {
                installedPackages = await runtimeClient.GetInstalledPackagesAsync(cancellationToken);
            }
        }

        return exitCode;
    }

    private static async Task<int> PublishAsync(
        List<string> args,
        RegistryClient registryClient,
        CancellationToken cancellationToken)
    {
        var packagePath = CommandLine.ConsumeOption(args, "--file");
        var token = ConsumeRegistryToken(args, registryClient);
        var devLocal = CommandLine.ConsumeFlag(args, "--dev-local");
        var setLatest = !CommandLine.ConsumeFlag(args, "--no-latest");
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("Usage: sunder publish --file <package.sunderpkg> [--token <token>] [--no-latest] [--dev-local] [--timeout <duration>]");
        }

        CommandLine.EnsureNoExtraArguments(args, "Usage: sunder publish --file <package.sunderpkg> [--token <token>] [--no-latest] [--dev-local] [--timeout <duration>]");

        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath))
        {
            ConsoleOutput.WriteError($"Package file '{fullPath}' was not found.");
            return Failure;
        }

        var validation = await ValidateArchiveAsync(fullPath, cancellationToken);
        if (!validation.Success || validation.Manifest?.Id is null || validation.Manifest.Version is null)
        {
            WriteValidationResult(validation);
            return Failure;
        }

        RegistryPublishPackageResponse result;
        if (devLocal)
        {
            ConsoleOutput.WriteInfo($"Publishing {validation.Manifest.Id} {validation.Manifest.Version} to the development registry...");
            result = await registryClient.PublishLocalPackageAsync(fullPath, setLatest, cancellationToken);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                ConsoleOutput.WriteError("Publish requires registry sign-in. Run 'sunder auth login', set SUNDER_REGISTRY_TOKEN, pass --token, or use --dev-local for a local development registry.");
                return Failure;
            }

            ConsoleOutput.WriteInfo($"Publishing {validation.Manifest.Id} {validation.Manifest.Version} to the registry...");
            result = await registryClient.PublishPackageAsync(fullPath, setLatest, token, cancellationToken);
        }

        return WritePublishResult(result);
    }

    private static async Task<int> SetYankedAsync(
        List<string> args,
        bool isYanked,
        RegistryClient registryClient,
        CancellationToken cancellationToken)
    {
        var token = ConsumeRegistryToken(args, registryClient);
        if (args.Count != 2)
        {
            throw new ArgumentException($"Usage: sunder {(isYanked ? "yank" : "unyank")} <package-id> <version> [--token <token>]");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            ConsoleOutput.WriteError("Package management requires registry sign-in. Run 'sunder auth login', set SUNDER_REGISTRY_TOKEN, or pass --token.");
            return Failure;
        }

        var result = await registryClient.SetVersionYankedAsync(args[0], args[1], isYanked, token, cancellationToken);
        return WriteManagementResult(result);
    }

    private static async Task<int> SetDeprecationAsync(
        List<string> args,
        bool clear,
        RegistryClient registryClient,
        CancellationToken cancellationToken)
    {
        var token = ConsumeRegistryToken(args, registryClient);
        var message = clear ? null : CommandLine.ConsumeOption(args, "--message");
        if (args.Count != 2 || (!clear && string.IsNullOrWhiteSpace(message)))
        {
            throw new ArgumentException(clear
                ? "Usage: sunder undeprecate <package-id> <version> [--token <token>]"
                : "Usage: sunder deprecate <package-id> <version> --message <message> [--token <token>]");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            ConsoleOutput.WriteError("Package management requires registry sign-in. Run 'sunder auth login', set SUNDER_REGISTRY_TOKEN, or pass --token.");
            return Failure;
        }

        var result = await registryClient.SetVersionDeprecationAsync(args[0], args[1], message, token, cancellationToken);
        return WriteManagementResult(result);
    }

    private static async Task<int> DistTagAsync(
        List<string> args,
        RegistryClient registryClient,
        CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("Usage: sunder dist-tag <list|set|delete> ...");
        }

        var command = args[0].ToLowerInvariant();
        args.RemoveAt(0);
        return command switch
        {
            "list" => await ListDistTagsAsync(args, registryClient, cancellationToken),
            "set" => await SetDistTagAsync(args, registryClient, cancellationToken),
            "delete" or "rm" => await DeleteDistTagAsync(args, registryClient, cancellationToken),
            _ => throw new ArgumentException($"Unknown dist-tag command '{command}'. Usage: sunder dist-tag <list|set|delete> ...")
        };
    }

    private static async Task<int> ListDistTagsAsync(
        List<string> args,
        RegistryClient registryClient,
        CancellationToken cancellationToken)
    {
        var packageId = CommandLine.RequireSingleArgument(args, "Usage: sunder dist-tag list <package-id>");
        var response = await registryClient.GetDistTagsAsync(packageId, cancellationToken);
        if (response is null)
        {
            ConsoleOutput.WriteError($"Package '{packageId}' was not found.");
            return Failure;
        }

        if (response.DistTags.Count == 0)
        {
            ConsoleOutput.WriteInfo($"Package '{packageId}' has no dist tags.");
            return Success;
        }

        var tagWidth = Math.Max("Tag".Length, response.DistTags.Max(tag => tag.Tag.Length));
        Console.WriteLine($"{"Tag".PadRight(tagWidth)}  Version");
        foreach (var distTag in response.DistTags)
        {
            Console.WriteLine($"{distTag.Tag.PadRight(tagWidth)}  {distTag.Version}");
        }

        return Success;
    }

    private static async Task<int> SetDistTagAsync(
        List<string> args,
        RegistryClient registryClient,
        CancellationToken cancellationToken)
    {
        var token = ConsumeRegistryToken(args, registryClient);
        if (args.Count != 3)
        {
            throw new ArgumentException("Usage: sunder dist-tag set <package-id> <tag> <version> [--token <token>]");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            ConsoleOutput.WriteError("Package management requires registry sign-in. Run 'sunder auth login', set SUNDER_REGISTRY_TOKEN, or pass --token.");
            return Failure;
        }

        var result = await registryClient.SetDistTagAsync(args[0], args[1], args[2], token, cancellationToken);
        return WriteManagementResult(result);
    }

    private static async Task<int> DeleteDistTagAsync(
        List<string> args,
        RegistryClient registryClient,
        CancellationToken cancellationToken)
    {
        var token = ConsumeRegistryToken(args, registryClient);
        if (args.Count != 2)
        {
            throw new ArgumentException("Usage: sunder dist-tag delete <package-id> <tag> [--token <token>]");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            ConsoleOutput.WriteError("Package management requires registry sign-in. Run 'sunder auth login', set SUNDER_REGISTRY_TOKEN, or pass --token.");
            return Failure;
        }

        var result = await registryClient.DeleteDistTagAsync(args[0], args[1], token, cancellationToken);
        return WriteManagementResult(result);
    }

    private static async Task<int> PackageAsync(List<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            throw new ArgumentException("Usage: sunder package validate <package.sunderpkg>");
        }

        var command = args[0].ToLowerInvariant();
        args.RemoveAt(0);
        return command switch
        {
            "validate" => await ValidatePackageAsync(args, cancellationToken),
            _ => throw new ArgumentException($"Unknown package command '{command}'. Usage: sunder package validate <package.sunderpkg>")
        };
    }

    private static async Task<int> ValidatePackageAsync(List<string> args, CancellationToken cancellationToken)
    {
        var packagePath = CommandLine.RequireSingleArgument(args, "Usage: sunder package validate <package.sunderpkg>");
        var result = await ValidateArchiveAsync(packagePath, cancellationToken);
        WriteValidationResult(result);
        return result.Success ? Success : Failure;
    }

    private static async Task<int> InstallFromFileAsync(
        string packagePath,
        bool allowDowngrade,
        bool reinstall,
        RuntimeClient runtimeClient,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPath))
        {
            ConsoleOutput.WriteError($"Package file '{fullPath}' was not found.");
            return Failure;
        }

        var validation = await ValidateArchiveAsync(fullPath, cancellationToken);
        if (!validation.Success || validation.Manifest?.Id is null)
        {
            WriteValidationResult(validation);
            return Failure;
        }

        var installedPackages = await runtimeClient.GetInstalledPackagesAsync(cancellationToken);
        var installedPackage = installedPackages.FirstOrDefault(package =>
            string.Equals(package.PackageId, validation.Manifest.Id, StringComparison.OrdinalIgnoreCase));
        var operationResult = installedPackage is null
            ? await runtimeClient.InstallPackageFromPathAsync(fullPath, cancellationToken)
            : await runtimeClient.UpgradePackageFromPathAsync(validation.Manifest.Id, fullPath, allowDowngrade, reinstall, cancellationToken);

        return WriteOperationResult(operationResult);
    }

    private static async Task<int> ExecuteInstallPlanAsync(
        RegistryResolveInstallPlanResponse plan,
        bool allowDowngrade,
        bool reinstall,
        RegistryClient registryClient,
        RuntimeClient runtimeClient,
        CancellationToken cancellationToken)
    {
        if (plan.Items.Count == 0)
        {
            ConsoleOutput.WriteInfo("No package changes required.");
            return Success;
        }

        WriteInstallPlan(plan);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "sunder-cli", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var item in plan.Items)
            {
                var packagePath = Path.Combine(tempDirectory, $"{item.PackageId}.{item.Version}.sunderpkg");
                ConsoleOutput.WriteInfo($"Downloading {item.PackageId} {item.Version}...");
                await registryClient.DownloadArtifactAsync(
                    item.Artifact,
                    item.PackageId,
                    item.Version,
                    packagePath,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(item.DeprecatedMessage))
                {
                    ConsoleOutput.WriteWarning($"Package version is deprecated: {item.DeprecatedMessage}");
                }

                var operationResult = item.CurrentVersion is null
                    ? await runtimeClient.InstallPackageFromPathAsync(packagePath, cancellationToken)
                    : await runtimeClient.UpgradePackageFromPathAsync(item.PackageId, packagePath, allowDowngrade, reinstall, cancellationToken);
                var exitCode = WriteOperationResult(operationResult);
                if (exitCode != Success)
                {
                    return exitCode;
                }
            }
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }

        return Success;
    }

    private static async Task<int> InstallResolvedPackageAsync(
        ResolvedPackage resolvedPackage,
        bool allowDowngrade,
        bool reinstall,
        RegistryClient registryClient,
        RuntimeClient runtimeClient,
        CancellationToken cancellationToken)
    {
        var installedPackages = await runtimeClient.GetInstalledPackagesAsync(cancellationToken);
        var installedPackage = installedPackages.FirstOrDefault(package =>
            string.Equals(package.PackageId, resolvedPackage.PackageId, StringComparison.OrdinalIgnoreCase));

        if (installedPackage is not null
            && string.Equals(installedPackage.Version, resolvedPackage.Version, StringComparison.OrdinalIgnoreCase)
            && !reinstall)
        {
            ConsoleOutput.WriteInfo($"Package '{resolvedPackage.PackageId}' {resolvedPackage.Version} is already installed. Use '--reinstall' to reinstall it.");
            return Success;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "sunder-cli", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(tempDirectory, $"{resolvedPackage.PackageId}.{resolvedPackage.Version}.sunderpkg");

        try
        {
            ConsoleOutput.WriteInfo($"Downloading {resolvedPackage.PackageId} {resolvedPackage.Version}...");
            await registryClient.DownloadArtifactAsync(
                resolvedPackage.Artifact,
                resolvedPackage.PackageId,
                resolvedPackage.Version,
                packagePath,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(resolvedPackage.DeprecatedMessage))
            {
                ConsoleOutput.WriteWarning($"Package version is deprecated: {resolvedPackage.DeprecatedMessage}");
            }

            var operationResult = installedPackage is null
                ? await runtimeClient.InstallPackageFromPathAsync(packagePath, cancellationToken)
                : await runtimeClient.UpgradePackageFromPathAsync(resolvedPackage.PackageId, packagePath, allowDowngrade, reinstall, cancellationToken);
            return WriteOperationResult(operationResult);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static async Task<ResolvedPackage?> ResolveExactVersionAsync(
        RegistryClient registryClient,
        string packageId,
        string version,
        CancellationToken cancellationToken)
    {
        var packageVersion = await registryClient.GetVersionAsync(packageId, version, cancellationToken);
        return packageVersion is null
            ? null
            : new ResolvedPackage(packageVersion.PackageId, packageVersion.Version, packageVersion.DeprecatedMessage, packageVersion.Artifact);
    }

    private static async Task<ResolvedPackage?> ResolveTagAsync(
        RegistryClient registryClient,
        string packageId,
        string tag,
        CancellationToken cancellationToken)
    {
        var resolvedPackage = await registryClient.ResolveAsync(packageId, tag, cancellationToken);
        return resolvedPackage is null
            ? null
            : new ResolvedPackage(resolvedPackage.PackageId, resolvedPackage.Version, resolvedPackage.DeprecatedMessage, resolvedPackage.Artifact);
    }

    private static async Task<SunderPackageArchiveValidationResult> ValidateArchiveAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        var stagingPath = Path.Combine(Path.GetTempPath(), "sunder-cli-validate", Guid.NewGuid().ToString("N"));
        try
        {
            return await SunderPackageArchiveInspector.ExtractAndValidateAsync(packagePath, stagingPath, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(stagingPath);
        }
    }

    private static IReadOnlyList<RegistryInstalledPackageState> ToInstalledPackageStates(IReadOnlyList<InstalledPackageDescriptor> packages)
        => packages
            .Select(package => new RegistryInstalledPackageState(
                package.PackageId,
                package.Version,
                package.DependsOn
                    .Select(dependency => new RegistryPackageDependency(dependency.PackageId, dependency.VersionRange))
                    .ToArray()))
            .ToArray();

    private static void WriteInstallPlan(RegistryResolveInstallPlanResponse plan)
    {
        ConsoleOutput.WriteInfo("Install plan:");
        foreach (var item in plan.Items)
        {
            var action = item.CurrentVersion is null
                ? item.Version
                : string.Equals(item.CurrentVersion, item.Version, StringComparison.OrdinalIgnoreCase)
                    ? $"reinstall {item.Version}"
                    : $"{item.CurrentVersion} -> {item.Version}";
            Console.WriteLine($"  {item.PackageId} {action}");
        }

        foreach (var warning in plan.Warnings)
        {
            ConsoleOutput.WriteWarning(warning);
        }
    }

    private static int WriteInstallPlanFailure(RegistryResolveInstallPlanResponse plan)
    {
        foreach (var error in plan.Errors)
        {
            ConsoleOutput.WriteError(error);
        }

        foreach (var conflict in plan.Conflicts)
        {
            ConsoleOutput.WriteError(conflict.Message);
        }

        return Failure;
    }

    private static void WritePackageSummaries(IReadOnlyList<RegistryPackageSummary> packages)
    {
        var idWidth = Math.Max("Package".Length, packages.Max(package => package.PackageId.Length));
        var versionWidth = Math.Max("Latest".Length, packages.Max(package => package.LatestVersion?.Length ?? 1));

        Console.WriteLine($"{"Package".PadRight(idWidth)}  {"Latest".PadRight(versionWidth)}  Summary");
        foreach (var package in packages)
        {
            Console.WriteLine($"{package.PackageId.PadRight(idWidth)}  {(package.LatestVersion ?? "-").PadRight(versionWidth)}  {package.Summary ?? string.Empty}");
        }
    }

    private static void WriteInstalledPackages(IReadOnlyList<InstalledPackageDescriptor> packages)
    {
        var idWidth = Math.Max("Package".Length, packages.Max(package => package.PackageId.Length));
        var versionWidth = Math.Max("Version".Length, packages.Max(package => package.Version.Length));

        Console.WriteLine($"{"Package".PadRight(idWidth)}  {"Version".PadRight(versionWidth)}  Enabled  Summary");
        foreach (var package in packages.OrderBy(package => package.PackageId, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{package.PackageId.PadRight(idWidth)}  {package.Version.PadRight(versionWidth)}  {(package.IsEnabled ? "yes" : "no ")}      {package.Summary ?? string.Empty}");
        }
    }

    private static void WritePackageDetails(RegistryPackageDetails package)
    {
        Console.WriteLine($"Package: {package.PackageId}");
        Console.WriteLine($"Name: {package.Name}");
        Console.WriteLine($"Latest: {package.LatestVersion ?? "-"}");
        if (!string.IsNullOrWhiteSpace(package.Summary))
        {
            Console.WriteLine($"Summary: {package.Summary}");
        }

        Console.WriteLine("Versions:");
        foreach (var version in package.Versions)
        {
            var suffix = version.IsYanked ? " yanked" : string.Empty;
            if (!string.IsNullOrWhiteSpace(version.DeprecatedMessage))
            {
                suffix += $" deprecated: {version.DeprecatedMessage}";
            }

            Console.WriteLine($"  {version.Version}{suffix}");
        }
    }

    private static void WritePackageVersion(RegistryPackageVersionDetails packageVersion)
    {
        Console.WriteLine($"Package: {packageVersion.PackageId}");
        Console.WriteLine($"Name: {packageVersion.Name}");
        Console.WriteLine($"Version: {packageVersion.Version}");
        if (!string.IsNullOrWhiteSpace(packageVersion.Summary))
        {
            Console.WriteLine($"Summary: {packageVersion.Summary}");
        }

        Console.WriteLine($"Entry Assembly: {packageVersion.EntryAssembly}");
        Console.WriteLine($"Target Framework: {packageVersion.TargetFramework ?? "-"}");
        Console.WriteLine($"SDK Version: {packageVersion.SdkVersion ?? "-"}");
        if (!string.IsNullOrWhiteSpace(packageVersion.DeprecatedMessage))
        {
            Console.WriteLine($"Deprecated: {packageVersion.DeprecatedMessage}");
        }

        Console.WriteLine("Dependencies:");
        if (packageVersion.DependsOn.Count == 0)
        {
            Console.WriteLine("  none");
        }
        else
        {
            foreach (var dependency in packageVersion.DependsOn)
            {
                Console.WriteLine($"  {dependency.PackageId} {dependency.VersionRange}");
            }
        }

        Console.WriteLine($"Artifact SHA-256: {packageVersion.Artifact.Sha256}");
        Console.WriteLine($"Artifact Size: {packageVersion.Artifact.Size} bytes");
    }

    private static void WriteValidationResult(SunderPackageArchiveValidationResult result)
    {
        foreach (var warning in result.Warnings)
        {
            ConsoleOutput.WriteWarning(warning);
        }

        foreach (var error in result.Errors)
        {
            ConsoleOutput.WriteError(error);
        }

        if (!result.Success)
        {
            return;
        }

        ConsoleOutput.WriteInfo($"Package is valid: {result.Manifest!.Id} {result.Manifest.Version}");
    }

    private static int WriteOperationResult(PackageOperationResult result)
    {
        foreach (var warning in result.Warnings)
        {
            ConsoleOutput.WriteWarning(warning);
        }

        foreach (var error in result.Errors)
        {
            ConsoleOutput.WriteError(error);
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            (result.Success ? Console.Out : Console.Error).WriteLine(result.Message);
        }

        if (result.ImpactedPackageIds.Count > 0)
        {
            ConsoleOutput.WriteInfo($"Impacted packages: {string.Join(", ", result.ImpactedPackageIds)}");
        }

        if (result.Success && result.ImpactedPackageIds.Count > 0 && !result.RuntimeSessionApplied)
        {
            ConsoleOutput.WriteWarning("The running package session did not apply this package change.");
        }

        if (result.RequiresAppRestart)
        {
            ConsoleOutput.WriteWarning("Restart Sunder to apply this package change.");
        }

        return result.Success ? Success : Failure;
    }

    private static int WritePublishResult(RegistryPublishPackageResponse result)
    {
        foreach (var warning in result.Warnings)
        {
            ConsoleOutput.WriteWarning(warning);
        }

        foreach (var error in result.Errors)
        {
            ConsoleOutput.WriteError(error);
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            (result.Success ? Console.Out : Console.Error).WriteLine(result.Message);
        }

        return result.Success ? Success : Failure;
    }

    private static int WriteManagementResult(RegistryPackageManagementOperationResponse result)
    {
        foreach (var error in result.Errors)
        {
            ConsoleOutput.WriteError(error);
        }

        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            (result.Success ? Console.Out : Console.Error).WriteLine(result.Message);
        }

        return result.Success ? Success : Failure;
    }

    private static string? ConsumeRegistryToken(List<string> args, RegistryClient registryClient)
    {
        var explicitToken = CommandLine.ConsumeOption(args, "--token")
            ?? Environment.GetEnvironmentVariable("SUNDER_REGISTRY_TOKEN");
        if (!string.IsNullOrWhiteSpace(explicitToken))
        {
            return explicitToken;
        }

        var savedToken = CliAuthStore.Load().GetToken(registryClient.RegistryUrl);
        if (savedToken is null)
        {
            return null;
        }

        return savedToken.ExpiresAtUtc is null || savedToken.ExpiresAtUtc > DateTimeOffset.UtcNow
            ? savedToken.Token
            : null;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup for temporary package downloads/extractions.
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Sunder CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  sunder auth login");
        Console.WriteLine("  sunder auth status");
        Console.WriteLine("  sunder auth logout");
        Console.WriteLine("  sunder search [query] [--skip <count>] [--take <count>]");
        Console.WriteLine("  sunder info <package-id> [--version <version>]");
        Console.WriteLine("  sunder list");
        Console.WriteLine("  sunder install <package-id> [--version <version>|--tag <tag>] [--allow-downgrade] [--reinstall]");
        Console.WriteLine("  sunder install --file <package.sunderpkg> [--allow-downgrade] [--reinstall]");
        Console.WriteLine("  sunder update [package-id|--all] [--include-prerelease]");
        Console.WriteLine("  sunder publish --file <package.sunderpkg> [--token <token>] [--no-latest] [--dev-local] [--timeout <duration>]");
        Console.WriteLine("  sunder yank <package-id> <version> [--token <token>]");
        Console.WriteLine("  sunder unyank <package-id> <version> [--token <token>]");
        Console.WriteLine("  sunder deprecate <package-id> <version> --message <message> [--token <token>]");
        Console.WriteLine("  sunder undeprecate <package-id> <version> [--token <token>]");
        Console.WriteLine("  sunder dist-tag list <package-id>");
        Console.WriteLine("  sunder dist-tag set <package-id> <tag> <version> [--token <token>]");
        Console.WriteLine("  sunder dist-tag delete <package-id> <tag> [--token <token>]");
        Console.WriteLine("  sunder package validate <package.sunderpkg>");
        Console.WriteLine();
        Console.WriteLine("Global options:");
        Console.WriteLine("  --registry-api-url <url>  Registry API URL. Debug default: http://localhost:5288/");
        Console.WriteLine("  --registry-web-url <url>  Registry Web URL for browser auth. Debug default: http://localhost:5288/");
        Console.WriteLine("  --registry-url <url>      Back-compatible alias that sets both registry URLs");
        Console.WriteLine("  --runtime-url <url>       Defaults to SUNDER_RUNTIME_URL or appsettings Runtime:Url");
        Console.WriteLine("  --timeout <duration>      Registry request timeout. Default: 15m. Examples: 15m, 900s, 900");
        Console.WriteLine("  SUNDER_REGISTRY_API_URL   Registry API URL override");
        Console.WriteLine("  SUNDER_REGISTRY_WEB_URL   Registry Web URL override");
        Console.WriteLine("  SUNDER_REGISTRY_TOKEN   Bearer token override for authenticated publish and package management");
    }

    private sealed record ResolvedPackage(
        string PackageId,
        string Version,
        string? DeprecatedMessage,
        RegistryPackageArtifact Artifact);
}
