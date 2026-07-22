# Avalonia Views

> **Applies to:** Sunder SDK `1.1.x`, `Sunder.Sdk.Avalonia` `1.1.x`, Avalonia 12, package manifest V1, and Runtime protocol revision 3.

Avalonia UI belongs to the App role. Headless packages should not reference `Sunder.Sdk.Avalonia` or Avalonia.

## Add An App View

The template option is the shortest path:

```powershell
dotnet new sunder-package `
  --name MyPackage `
  --packageId my.company.package `
  --packageName "My Package" `
  --withAvalonia
```

For an existing project, reference `Sunder.Sdk.Avalonia` and the Avalonia packages used directly by the project, using the coordinated `1.1` SDK range.

Register a control from `RegisterAppContributions`:

```csharp
registry.RegisterPackageView<QuickstartView>(new PackageViewRegistration(
    id: "my.company.package.main",
    name: "My Package",
    iconAssetPath: "assets/icon.png",
    defaultPlacement: PackageViewPlacement.Middle,
    showInHotbarByDefault: true));
```

View ids are persistent shell/navigation identities. Make them stable, globally unique, and package-prefixed. The current App treats collisions case-insensitively. `DefaultPlacement` and `ShowInHotbarByDefault` apply before the user customizes the shell.

The App creates a view lazily with `ActivatorUtilities` from the package App provider, so constructor parameters may be package services or host-provided App services. A view is cached once per view id and App generation until explicitly invalidated, package disable/unload, or generation replacement. Do not assume a new control for each navigation.

## Settings UI

Prefer a Runtime `PackageSettingsSchema` when standard text, secret, boolean, or select fields are sufficient. The host renders and validates those fields.

For package-specific UI, register one custom settings control:

```csharp
registry.RegisterSettingsView<MyPackageSettingsView>();
```

Custom settings controls are also resolved from the App provider and cached. Persist values through `IPackageSettings` or `IPackageSecrets`, not through control fields or App-local files. Host-rendered schemas are Runtime contributions; custom settings controls are App contributions and may coexist.

## Navigation

Implement `IPackageViewNavigationTarget` on the control or its data context:

```csharp
public async ValueTask OnNavigatedToAsync(
    PackageViewNavigationContext context,
    CancellationToken cancellationToken = default)
{
    var itemId = context.Parameters.GetValueOrDefault("itemId");
    await LoadInitialStateAsync(itemId, cancellationToken);
}
```

The control takes precedence when both it and its data context implement the interface. App calls the target for initial presentation, restored/direct selection, and programmatic navigation. Parameters are an immutable, case-sensitive snapshot and values may be `null`.

Navigation starts on the App UI dispatcher and operations for one view are serialized. A later navigation to that view or closing/resetting it cancels the current call. Return after the view has a stable initial presentation; move continuing work to a cancellable background process or package service. If an `await` opts out of the captured context, dispatch subsequent control access explicitly.

An unhandled view construction, warmup, navigation, or hosted rendering exception disables that package in the current App generation and is logged as a client-local presentation fault. It does not fault the Runtime package generation.

## Warmup

Implement `IPackageViewWarmupTarget` on the control or data context for parameter-free preparation before first presentation:

```csharp
public ValueTask WarmupAsync(CancellationToken cancellationToken = default)
    => cache.EnsureLoadedAsync(cancellationToken);
```

Warmup may run while the cached view is hidden and before visual attachment. It runs on the UI dispatcher, normally once for that cached view/generation, and normal navigation still runs when the view is shown. It must be idempotent, promptly cancellable, and independent of visual-tree attachment. Do not navigate the shell, show prompts, or make durable mutations during speculative warmup.

## Shell Navigation

Inject `IPackageShellViewService` to add/move hotbar entries or open/close panels. Methods marshal mutations to the UI thread and return `false` when the view is unknown or no state changed. Before App generation publication, snapshots are empty and mutations do not run.

```csharp
await shell.OpenViewPanelAsync(
    "my.company.package.main",
    new Dictionary<string, string?> { ["itemId"] = itemId },
    cancellationToken);
```

Inject `IPackageSettingsNavigationService` to open global settings or a package settings page. A `false` result means navigation was unavailable or no matching page exists.

## Disposal

On invalidation/unload, App detaches cached controls from common parents. It then disposes a disposable data context (unless it is the control itself), followed by a disposable control. Finally, it disposes the App provider and unloads the package load context.

Views and view models should cancel work, unsubscribe events, and implement `IDisposable` when necessary. Do not hold package controls/types in static fields or host-global event handlers.

## Semantic Theme Resources

Sunder App loads `Sunder.Sdk.Avalonia` theme resources and package styles. Package UI should consume them with `DynamicResource`; do not hardcode the shell palette or merge a private copy of the Sunder dictionaries.

```xml
<Border Classes="workspace-surface">
  <TextBlock Classes="page-title"
             Text="My Package"
             Foreground="{DynamicResource Sunder.Brush.Foreground.Primary}" />
</Border>
```

The [compiled quickstart view](../samples/Sunder.Package.Quickstart/QuickstartView.axaml) uses these resources.

### Resource Families

| Family | Keys |
| --- | --- |
| Background/surfaces | `Sunder.Brush.Background.App`, `Surface.Base`, `Surface.Raised`, `Surface.Popover`, `Surface.Workspace`, `Surface.Hover`, `Surface.Selected`, `Surface.DragOver`, `Surface.Code` |
| Borders | `Sunder.Brush.Border.Subtle`, `Strong`, `Warning`, `Danger`, `Focus` |
| Foreground | `Sunder.Brush.Foreground.Primary`, `Secondary`, `Muted`, `OnAccent`, `OnDanger`, `Code` |
| State/accent | `Sunder.Brush.Accent`, `Accent.Soft`, `Accent.Overlay`, `Success`, `Success.Soft`, `Warning`, `Warning.Soft`, `Danger`, `Danger.Soft`, `Error`, `Error.Soft`, `Info`, `Info.Soft` |
| Interaction/overlay | `Sunder.Brush.Focus`, `Selection`, `Disabled`, `Transparent`, `Overlay.Backdrop`, `Overlay.Backdrop.Strong`, `Overlay.Surface`, `Overlay.Border` |
| Raw colors | `Sunder.Color.Background.App`, `Surface.Base`, `Surface.Raised`, `Surface.Workspace`, `AppGradient.Start`, `Middle`, `End`, `Loading.Overlay.Start`, `Middle`, `Soft`, `End` |
| Radius | `Sunder.Radius.Small`, `Medium`, `Large`, `Full` |
| Spacing | `Sunder.Spacing.XSmall`, `Small`, `Medium`, `Large`, `XLarge` |
| Padding | `Sunder.Padding.Card`, `Panel`, `ActionButton` |
| Typography | `Sunder.FontSize.Caption`, `Body`, `SectionTitle`, `PageTitle` |
| Shadows | `Sunder.Shadow.WorkspacePanel`, `ShellPanel`, `Toast`, `Prompt`, `WelcomeLogo`, `PackagePanel`, `PackageCard`, `Overlay` |

`Sunder.Sdk.Avalonia.Theming.SunderThemeKeys` exposes public constants for the color, brush, radius, spacing, font-size, and shadow families; the padding resources currently use their stable string keys directly. `BrushKeys` contains the semantic brush-key set. The SDK style classes are `panel-surface`, `workspace-surface`, `card-surface`, `primary-action`, `secondary-action`, `danger-action`, `compact-action`, `page-title`, `section-title`, `caption`, and `muted`.

Referencing Sunder resources in source or compiled Avalonia XAML causes build tooling to infer `theming.v1`. If generated/dynamic XAML cannot be classified, follow the build diagnostic and declare the capability explicitly rather than bypassing inference.

Package icons are untrusted assets with their own validation policy. See the [Sunder Package Standard](../SUNDER-PACKAGE-STANDARD.md#package-icons) rather than duplicating theming or icon-format rules here.
