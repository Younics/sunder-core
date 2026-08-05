namespace Sunder.Cli;

internal sealed class CliApplication
{
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly Func<CliOptions, ICliHostClient> _hostFactory;
    private readonly Func<CliOptions, ICliRuntimeClient> _runtimeFactory;
    private readonly Func<CliOptions, IRegistryClient> _registryFactory;
    private readonly IBrowserLauncher _browser;
    private readonly IRegistryPublishCredentialReader _publishCredentials;
    private readonly IPackageSecretValueReader _packageSecrets;
    private readonly Func<CliOptionOverrides, CliConfigurationRequirements, CliOptions> _optionsFactory;

    internal CliApplication(
        TextWriter stdout,
        TextWriter stderr,
        Func<CliOptions, ICliHostClient> hostFactory,
        Func<CliOptions, ICliRuntimeClient> runtimeFactory,
        Func<CliOptions, IRegistryClient> registryFactory,
        IBrowserLauncher browser,
        IRegistryPublishCredentialReader publishCredentials,
        IPackageSecretValueReader packageSecrets,
        Func<CliOptionOverrides, CliConfigurationRequirements, CliOptions>? optionsFactory = null)
    {
        _stdout = stdout;
        _stderr = stderr;
        _hostFactory = hostFactory;
        _runtimeFactory = runtimeFactory;
        _registryFactory = registryFactory;
        _browser = browser;
        _publishCredentials = publishCredentials;
        _packageSecrets = packageSecrets;
        _optionsFactory = optionsFactory ?? ((overrides, requirements) => CliOptions.Load(overrides, requirements));
    }

    public static CliApplication CreateDefault(TextReader stdin, TextWriter stdout, TextWriter stderr)
        => new(
            stdout,
            stderr,
            options => new CliHostClient(options.RequireRuntimeUrl()),
            options => new CliRuntimeClient(options.RequireRuntimeUrl(), options.RequestTimeout),
            options => new RegistryClient(options.RequireRegistryApiUrl()),
            new BrowserLauncher(),
            new RegistryPublishCredentialReader(stdin, () => Console.IsInputRedirected),
            new PackageSecretValueReader(stdin, () => Console.IsInputRedirected));

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var parser = new CliCommandParser();
        var parsed = parser.Parse(args);
        var output = new CliOutput(
            _stdout,
            _stderr,
            parsed.Json,
            parsed.Invocation?.CommandPath ?? parser.GetPath(parsed.HelpScope));

        if (parsed.ShowHelp)
        {
            var help = parser.FormatHelp(parsed.HelpScope);
            output.Line(help);
            output.Data(new { help });
            return Complete(output, CliExitCodes.Success);
        }
        if (parsed.Errors.Count > 0)
        {
            foreach (var error in parsed.Errors) output.Error(error, "cli.usage");
            return Complete(output, CliExitCodes.Usage);
        }

        var invocation = parsed.Invocation
            ?? throw new InvalidOperationException("A successful command parse must produce an invocation.");
        var plan = CliCommandPlan.For(invocation.Command);
        CliOptions options;
        try
        {
            options = _optionsFactory(invocation.Overrides, plan.Configuration);
            EnsureDevelopmentRegistry(invocation.Command, options);
        }
        catch (Exception exception) when (exception is CliConfigurationException or CliUsageException or ArgumentException)
        {
            var error = CliErrorMapper.Describe(exception);
            output.Error(error);
            return Complete(output, error.ExitCode);
        }

        ICliHostClient? host = null;
        ICliRuntimeClient? runtime = null;
        IRegistryClient? registry = null;
        CancellationTokenSource? timeout = null;
        CancellationTokenSource? linked = null;
        try
        {
            if (plan.Clients.HasFlag(CliClientRequirements.Host)) host = _hostFactory(options);
            if (plan.Clients.HasFlag(CliClientRequirements.Runtime)) runtime = _runtimeFactory(options);
            if (plan.Clients.HasFlag(CliClientRequirements.Registry)) registry = _registryFactory(options);

            var effectiveToken = cancellationToken;
            if (plan.UseRequestTimeout)
            {
                timeout = new CancellationTokenSource(options.RequestTimeout);
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                effectiveToken = linked.Token;
            }

            var dispatcher = new CliCommandDispatcher(
                host,
                runtime,
                registry,
                output,
                new CliProgress(output),
                _browser,
                options,
                _publishCredentials,
                _packageSecrets);
            var exitCode = await dispatcher.ExecuteAsync(invocation.Command, effectiveToken).ConfigureAwait(false);
            return Complete(output, exitCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output.Error("Operation cancelled.", "cli.cancelled");
            return Complete(output, CliExitCodes.Cancelled);
        }
        catch (Exception exception) when (timeout?.IsCancellationRequested == true || CliErrorMapper.IsTimeout(exception))
        {
            var error = CliErrorMapper.Describe(exception is OperationCanceledException
                ? new TimeoutException("The command request timed out.", exception)
                : exception);
            output.Error(error);
            return Complete(output, CliExitCodes.Timeout);
        }
        catch (Exception exception)
        {
            var error = CliErrorMapper.Describe(exception);
            output.Error(error);
            return Complete(output, error.ExitCode);
        }
        finally
        {
            linked?.Dispose();
            timeout?.Dispose();
            registry?.Dispose();
            runtime?.Dispose();
            host?.Dispose();
        }
    }

    private static int Complete(CliOutput output, int exitCode)
    {
        output.Complete(exitCode);
        return exitCode;
    }

    private static void EnsureDevelopmentRegistry(CliCommand command, CliOptions options)
    {
        if (command is PublishPackageCommand { DevLocal: true } or PublishStackCommand { DevLocal: true }
            && !options.RequireRegistryApiUrl().IsLoopback)
        {
            throw new CliUsageException("Development Registry publication requires a loopback Registry API URL.");
        }
    }

}
