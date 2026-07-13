namespace Sunder.Cli;

internal static class CliUsage
{
    public const string Text = """
        Sunder CLI

        Usage:
          sunder system status
          sunder runtime reset --yes
          sunder auth login
          sunder auth status
          sunder auth logout
          sunder search [query] [--skip <count>] [--take <count>]
          sunder info <package-id> [--version <version>]
          sunder list
          sunder install <package-id> [--version <version>|--tag <tag>] [--allow-downgrade] [--reinstall]
          sunder install --file <package.sunderpkg> [--allow-downgrade] [--reinstall]
          sunder update [package-id|--all] [--include-prerelease]
          sunder publish --file <package.sunderpkg> [--no-latest] [--dev-local]
          sunder yank <package-id> <version>
          sunder unyank <package-id> <version>
          sunder deprecate <package-id> <version> --message <message>
          sunder undeprecate <package-id> <version>
          sunder dist-tag list <package-id>
          sunder dist-tag set <package-id> <tag> <version>
          sunder dist-tag delete <package-id> <tag>
          sunder package validate <package.sunderpkg>
          sunder validate <package.sunderpkg>
          sunder stack search [query] [--skip <count>] [--take <count>]
          sunder stack info <stack-id>
          sunder stack download <stack-id> [--output <path>]
          sunder stack publish --file <stack.sunderstack> [--dev-local]
          sunder stack update <stack-id> --file <stack.sunderstack>
          sunder stack delete <stack-id>
          sunder stack use <stack-id>
          sunder stack inspect <stack.sunderstack>
          sunder stack validate <stack.sunderstack>

        Global options:
          --registry-api-url <url>  Registry API URL
          --registry-web-url <url>  Registry Web URL used for browser auth
          --runtime-url <url>       Authenticated local Runtime URL
          --timeout <duration>      Request timeout; default 15m
          --json                    Emit one deterministic JSON result
          --help, -h                Show this help

        Destructive commands:
          runtime reset --yes       Drain Runtime and delete validated local Runtime V1 state only
        """;
}
