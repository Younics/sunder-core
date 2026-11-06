<p><img src="https://raw.githubusercontent.com/Younics/sunder-core/main/languages/csharp/dotnet/packages/Sunder.Sdk/icon.png" alt="Sunder" width="96" height="96" /></p>

# Sunder.Sdk.Avalonia

**Create desktop views that feel at home in Sunder.**

Add Avalonia views, custom settings pages, navigation, and theme-aware controls to
a [Sunder](https://sunderapp.io) package. This SDK connects your Avalonia UI to the
Sunder shell and supplies semantic colors, spacing, and control styles.

Use it for dashboards, editors, integration panels, and other interactive desktop
tools. Your UI calls typed package services while the backend runs in a managed
worker process.

## Install and prerequisites

Use the **.NET 10 SDK**, Sunder, and the `sunder` CLI. The package uses **Avalonia 12**.
For a new project, the `--withAvalonia` template option supplies the appropriate
references and view registration. For an existing Sunder package:

```sh
dotnet add package Sunder.Sdk.Avalonia
```

Keep the reference scoped to the generated App role, as the template does:

```xml
<PackageReference Include="Sunder.Sdk.Avalonia" Version="1.0.1" SunderPackageAssetRole="app" />
```

## Tutorial: a themed greeting panel

### 1. Create the UI package

```sh
dotnet new install Sunder.Package.Templates
dotnet new sunder-package --name HelloSunder --packageId example.hello --packageName "Hello Sunder" --withAvalonia --output HelloSunder
cd HelloSunder
```

The starter contains a service, view model, and working button handler. The view
model receives the generated service API:

```csharp
using Sunder.Generated.Packages.Example_Hello_Services;
using Sunder.Sdk.Abstractions;

namespace HelloSunder.Views;

public sealed class DefaultPackageViewModel(IPackageContext packageContext, ISampleService sample)
{
    public string PackageId { get; } = packageContext.PackageId;

    public Task<string> GreetAsync(string name, CancellationToken cancellationToken)
        => sample.GreetAsync(name, cancellationToken);
}
```

### 2. Style the view

Replace `Views/DefaultPackageView.axaml` with the following. Keep the generated
`DefaultPackageView.axaml.cs`; its `GreetOnClick` handler calls the view model,
handles errors, and cancels outstanding work when the view is disposed.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:HelloSunder.Views"
             x:Class="HelloSunder.Views.DefaultPackageView"
             x:DataType="vm:DefaultPackageViewModel"
             x:CompileBindings="True">
  <Border Classes="workspace-surface" Padding="24">
    <StackPanel Spacing="12">
      <TextBlock Classes="page-title" Text="Hello Sunder"
                 Foreground="{DynamicResource Sunder.Brush.Foreground.Primary}" />
      <TextBlock Classes="muted" Text="A desktop view backed by your C# service." />
      <TextBlock Text="{Binding PackageId}" />
      <TextBox x:Name="NameInput" Text="Sunder" MaxLength="120"
               PlaceholderText="Your name" />
      <Button Classes="primary-action" Content="Create greeting" Click="GreetOnClick" />
      <TextBlock x:Name="StatusText" TextWrapping="Wrap" />
    </StackPanel>
  </Border>
</UserControl>
```

`x:DataType` enables typed bindings. `DynamicResource` uses the active Sunder theme,
so your view follows shell theme changes.

### 3. Run it

```sh
dotnet build
sunder runtime ensure sunder.runtime.dotnet ">=10.0.0 <11.0.0" sunder.dotnet-worker-module.v1
dotnet run --no-build
```

Open **Hello Sunder**, enter a name, and choose **Create greeting**. The response
comes from the worker's `SampleService`, through the generated interface.

## Register views and settings

Template-created controls use `SunderPackageView` from `Sunder.Sdk.Packaging`:

```csharp
[SunderPackageView("example.hello.default", "Hello Sunder",
    ViewModel = typeof(DefaultPackageViewModel), IconPath = "assets/icon.png")]
```

This attribute belongs on the view class. Use a stable, package-prefixed view ID;
Sunder uses it for navigation and saved workspace layout. Build tooling generates
the App registration and dependency injection.

For preferences, choose the simplest fit:

- **Standard fields:** implement a Worker settings-schema provider. Sunder renders
  text, boolean, secret, and select fields for you.
- **Custom settings UI:** mark an Avalonia control with `SunderPackageSettingsView`
  and persist values through package settings/secrets.

## Useful UI capabilities

| Capability | Use it for |
| --- | --- |
| `IPackageViewNavigationTarget` | Load data when a view is opened or navigated to. |
| `IPackageViewNavigationPreparationTarget` | Prepare authoritative state before showing an interactive view. |
| `IPackageViewWarmupTarget` | Cancellable, read-only preparation before first presentation. |
| `IPackageShellViewService` | Open views and work with shell panels. |
| `IPackageSettingsNavigationService` | Open global or package settings. |

Views are created lazily and cached for their App activation. Refresh data on
navigation when appropriate, keep the UI thread responsive, and cancel/unsubscribe
owned work during disposal.

## Theme building blocks

Use `workspace-surface`, `card-surface`, `page-title`, `section-title`, `muted`,
`primary-action`, and `secondary-action` classes for common UI elements.

Semantic resources include `Sunder.Brush.Foreground.Primary`,
`Sunder.Brush.Foreground.Secondary`, `Sunder.Brush.Surface.Raised`,
`Sunder.Brush.Accent`, `Sunder.Radius.Medium`, and `Sunder.Spacing.Medium`.
Sunder loads the resources; your package consumes them.

## Troubleshooting and more examples

- **Binding fails to compile:** check `x:DataType`, the namespace, and the public property name.
- **View is absent:** check the view attribute, its ID, and the generated App output.
- **View shows old data on reopen:** cached controls need explicit refresh behavior.
- [Avalonia lifecycle, navigation, and theme reference](../../../../../docs/package-development/AVALONIA.md)
- [Complete compiled example](../../samples/Sunder.Package.Quickstart/README.md)
- [C# service tutorial](../Sunder.Sdk/README.md)
- [Report an issue](https://github.com/Younics/sunder-core/issues)

Licensed under [MIT](../Sunder.Sdk/LICENSE).
