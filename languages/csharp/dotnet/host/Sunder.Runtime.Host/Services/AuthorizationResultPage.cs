using System.Net;

namespace Sunder.Runtime.Host.Services;

internal static class AuthorizationResultPage
{
    internal static readonly IReadOnlyList<(string Name, string Value)> Headers = Array.AsReadOnly<(string, string)>(
    [
        ("Cache-Control", "no-store"),
        ("Pragma", "no-cache"),
        ("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'"),
        ("Referrer-Policy", "no-referrer"),
        ("X-Content-Type-Options", "nosniff"),
        ("X-Frame-Options", "DENY"),
    ]);

    internal static string Build(bool success, string message, string? titleOverride = null)
    {
        var title = WebUtility.HtmlEncode(titleOverride ?? (success ? "Authorization complete" : "Authorization failed"));
        var subtitle = WebUtility.HtmlEncode(message);
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{{title}}</title>
                <style>
                    :root {
                        color-scheme: dark;
                        --bg: #15171a;
                        --bg-lifted: #1d2025;
                        --text: #dedad3;
                        --muted: #ccc7be;
                        --accent-strong: #e7b765;
                        --accent-rgb: 217, 154, 58;
                        --success-rgb: 110, 231, 183;
                        --white-rgb: 255, 255, 255;
                        font-family: "IBM Plex Sans", system-ui, sans-serif;
                    }
                    * { box-sizing: border-box; }
                    body {
                        margin: 0;
                        min-height: 100vh;
                        background:
                            radial-gradient(circle at 20% 10%, rgba(var(--accent-rgb), 0.18), transparent 28rem),
                            radial-gradient(circle at 85% 0%, rgba(var(--success-rgb), 0.12), transparent 24rem),
                            linear-gradient(180deg, var(--bg-lifted) 0%, var(--bg) 42rem);
                        color: var(--text);
                    }
                    .boot-shell {
                        display: flex;
                        align-items: center;
                        justify-content: center;
                        gap: 14px;
                        min-height: 100vh;
                        padding: 24px;
                        overflow-wrap: anywhere;
                    }
                    .boot-mark {
                        display: grid;
                        flex: 0 0 44px;
                        width: 44px;
                        height: 44px;
                        place-items: center;
                        border: 1px solid rgba(var(--accent-rgb), 0.46);
                        border-radius: 14px;
                        background: linear-gradient(135deg, rgba(var(--accent-rgb), 0.2), rgba(var(--white-rgb), 0.04));
                        color: var(--accent-strong);
                        font-weight: 800;
                    }
                    .boot-title { font-weight: 800; }
                    .boot-subtitle { color: var(--muted); }
                </style>
            </head>
            <body>
                <main class="boot-shell">
                    <div class="boot-mark" aria-hidden="true">S</div>
                    <div>
                        <div class="boot-title">{{title}}</div>
                        <div class="boot-subtitle">{{subtitle}}</div>
                    </div>
                </main>
            </body>
            </html>
            """;
    }
}
