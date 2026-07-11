# Sunder App

`Sunder.App` is the Avalonia desktop shell for Sunder. It launches or connects to `Sunder.Runtime.Host`, loads package UI contributions, and presents local package management and Registry browsing UX.

## Responsibilities

`Sunder.App` owns:

- desktop shell windows and workspace layout
- app-side package module activation
- package view and settings view creation
- package view caching
- package icon loading and fallback rendering
- shell notifications
- app-side package fault reporting to the runtime
- observation of Runtime package/session generations and package logs through authenticated bounded streams
- visual theme resources and app branding

`Sunder.App` does not own installed package state, dev-package directory watching, or package-log discovery. Those responsibilities belong to `Sunder.Runtime.Host`.

App-to-Runtime HTTP endpoints are versioned under `/api/v1` and require a per-Runtime-instance bearer token, including health and status. The App generates the token before launching its managed Runtime, passes it only through the inherited child environment, and publishes the matching URL/token through the per-user private Runtime connection file used by the App and CLI. Tokens are never command-line arguments.

## Local State V1

Persistent local state uses explicit, destructive V1 boundaries under the current user's local application data directory:

- `Sunder/runtime/v1` owns the installed catalog and payloads, transactions/tombstones, package state/files/secrets/logs, uploads/snapshots, Registry credentials, Runtime lease, and Runtime connection document.
- `Sunder/app/v1` owns shell/UI state, notifications and update settings, local Stack library data, package workspaces, image caches, and App logs.
- Runtime and App package snapshot/session materialization uses explicitly named `V1` temporary roots.

Each persistent V1 root contains `schema.json` with product identity `Sunder`, schema version `1`, and its Runtime or App local-state API identity. A fresh empty root is initialized with an atomic create. A non-empty V1 root with missing, malformed, or incompatible metadata is rejected; no legacy root is inferred, scanned, or migrated.

Package and Registry secrets use Runtime-V1-specific DPAPI entropy and macOS Keychain/Linux Secret Service namespaces. Existing unversioned filesystem data and old credential items remain untouched.

The V1 boundary does not assume a shared filesystem. Runtime package UI is exposed as bounded, generation-scoped snapshot descriptors and authenticated ZIP streams. The App verifies each immutable SHA-256 revision and extracts it into an App-owned session directory; old directories are deleted only after views, services, assembly probes, and load contexts detach. Package and Stack mutations upload bytes to Runtime-owned temporary storage and use opaque handles. Runtime install, dev, staging, workspace, and transfer paths are never returned in API JSON.

## Startup Arguments

Load a development package folder:

```powershell
Sunder.App.exe --dev-package C:\Packages\MyPackage\bin\Debug\net10.0\sunder-dev
```

Load multiple development package folders:

```powershell
Sunder.App.exe --dev-package C:\Packages\Host\bin\Debug\net10.0\sunder-dev --dev-package C:\Packages\Extension\bin\Debug\net10.0\sunder-dev
```

Connect to an explicit runtime URL:

```powershell
Sunder.App.exe --runtime-url http://127.0.0.1:5276 --dev-package C:\Packages\MyPackage\bin\Debug\net10.0\sunder-dev
```

Point the app at a runtime host executable or folder:

```powershell
Sunder.App.exe --runtime-host-path C:\Sunder\Sunder.Runtime.Host.exe
```

Argument forms:

- `--dev-package <folder>`
- `--dev-package=<folder>`
- `--watch` (requires at least one `--dev-package`)
- `--runtime-url <url>`
- `--runtime-url=<url>`
- `--runtime-host-path <path>`
- `--runtime-host-path=<path>`

Environment variables:

| Variable | Meaning |
| --- | --- |
| `SUNDER_RUNTIME_URL` | Runtime URL used by the app and CLI |
| `SUNDER_RUNTIME_HOST_PATH` | Runtime host executable or folder used by the app |

Default runtime URL:

```text
http://127.0.0.1:5275/
```

## Runtime Host Debugging

Start the runtime host and wait for debugger attach:

```powershell
Sunder.Runtime.Host.exe --wait-for-debugger --urls http://127.0.0.1:5276
```

Start the app against that runtime:

```powershell
Sunder.App.exe --runtime-url http://127.0.0.1:5276 --dev-package C:\Packages\MyPackage\bin\Debug\net10.0\sunder-dev
```

Runtime host also supports:

| Option or variable | Meaning |
| --- | --- |
| `--wait-for-debugger` | Blocks runtime startup until a debugger is attached |
| `SUNDER_WAIT_FOR_DEBUGGER=1` | Enables debugger wait through environment |
| `--urls <url>` | ASP.NET Core URL binding passed through to the web host |
| `--development-allow-non-loopback-runtime-listen` | Development-only override permitting a non-loopback Runtime listen URL |
| `SUNDER_DEVELOPMENT_ALLOW_NON_LOOPBACK_RUNTIME_LISTEN=1` | Environment form of the development-only non-loopback override |

The Runtime defaults to `http://127.0.0.1:5275` and rejects wildcard or non-loopback listen URLs unless the explicitly named development override is supplied. Managed App launches never supply that override. A directly launched Runtime generates its own instance token and atomically publishes private connection information for the current user. On Unix, the connection directory/file modes are `0700`/`0600`; on Windows, the file is under Local App Data and its token is protected with current-user DPAPI.

## Dev Package Flow

When `--dev-package` is used:

1. The app normalizes each dev package folder path.
2. The app sends the dev package folder list to the runtime host.
3. The runtime host shadow-materializes and validates runtime package content.
4. The runtime host activates runtime package modules and reports active descriptors.
5. The app downloads generation-scoped package UI snapshots without inspecting the dev directories.
6. The app activates app-side package modules and registers package UI contributions.

With `--watch`, the App sends watch intent through the typed Runtime API. Runtime owns recursive and parent-folder watchers, debounce, stability polling, stage/commit generation fencing, and reload result publication. The App keeps one cancellable Runtime event subscription and reconciles package UI on its dispatcher when the session generation changes.

Runtime lifecycle events and package logs use authenticated SSE feeds with monotonic sequence IDs, bounded replay, and snapshot fallback after a replay gap. Package log files are discovered and tailed only by Runtime. The App developer log combines its own `AppSessionLog` entries with structured, safely truncated package entries received through the Runtime API; API payloads do not contain package log paths.

Installed package changes still save to local runtime state while dev-package override mode is active. The runtime reports a warning when local changes are saved during a dev-package session.

## Package Icons

Package icons come from package metadata `Icon` asset paths.

Current behavior:

- Installed package descriptors expose package icon asset paths.
- The app turns package icon paths into runtime asset URLs.
- Runtime serves authenticated icon assets through `/api/v1/packages/{packageId}/assets/{assetPath}`.
- SVG icons load through `Svg.Controls.Skia.Avalonia` / Avalonia SVG support.
- Raster icons load through Avalonia bitmap support.
- File extension routing is used before content sniffing to avoid loading raster images as SVG.
- Package image icons render directly at full size in package lists/details.
- Glyph fallback uses the first character of the package name.
- Rounded dark icon containers are used only for glyph fallback.
- Icon load failures are written to `AppSessionLog`.

Supported icon formats in current runtime/app paths:

- BMP
- GIF
- ICO
- JPG/JPEG
- PNG
- SVG/SVGZ
- WebP

## Branding

The app uses two different branding assets:

- `app.ico` for Windows executable, taskbar, and window chrome icons.
- `logo.png` for in-app Sunder branding badges.

Do not replace executable/window icons with padded logo artwork. Use `app.ico` for OS chrome and `logo.png` for app branding surfaces.

## Theme Direction

The current app visual direction is graphite/gray with subtle amber accenting.

Package UI guidance:

- Use Sunder semantic resources for shell-sensitive colors, surfaces, spacing, and typography.
- Avoid hardcoding shell palette values in package views.
- Keep package UI independent of `Sunder.App` implementation details.
- Use regular Avalonia controls and layout patterns inside package views.

Semantic theme keys are exposed in `Sunder.Sdk.Avalonia.Theming.SunderThemeKeys`.

Common keys:

- `Sunder.Brush.Background.App`
- `Sunder.Brush.Surface.Base`
- `Sunder.Brush.Surface.Raised`
- `Sunder.Brush.Surface.Workspace`
- `Sunder.Brush.Foreground.Primary`
- `Sunder.Brush.Foreground.Secondary`
- `Sunder.Brush.Foreground.Muted`
- `Sunder.Brush.Accent`
- `Sunder.Brush.Warning`
- `Sunder.Brush.Danger`
- `Sunder.Radius.Medium`
- `Sunder.Spacing.Medium`
- `Sunder.FontSize.Body`

Themes are app-side UI data. They are not runtime packages managed by `Sunder.Runtime.Host`.

## Package Management UI

The package window shows Registry marketplace packages and locally installed packages.

Current package UI actions include:

- browse Registry packages
- view package details and versions
- install from Registry
- install local `.sunderpkg` files
- update installed packages
- enable installed packages
- disable installed packages
- uninstall installed packages
- inspect package readiness and faults

Registry browsing uses remote Registry APIs. Local installed state always comes from `Sunder.Runtime.Host`.

## Build Output Locks

If `Sunder.App`, `Sunder.Runtime.Host`, or `Sunder.Registry.Server` is running, normal build outputs can be locked on Windows.

Use alternate output and intermediate paths for validation builds when needed:

```powershell
dotnet build .\src\Host\Sunder.App\Sunder.App.csproj --no-restore -p:OutputPath=.\artifacts\tmp\sunder-app\bin\ -p:IntermediateOutputPath=.\artifacts\tmp\sunder-app\obj\
```
