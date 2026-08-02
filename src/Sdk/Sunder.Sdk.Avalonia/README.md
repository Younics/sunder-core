# Sunder.Sdk.Avalonia

> **Release/source channel:** The NuGet README describes that published package version. The repository copy tracks current source and may be ahead of NuGet; use the matching `sdk/v*` tag when auditing a release.

Avalonia-specific package author contracts and Sunder shell theme resources.

Reference this package only from package projects that contribute App views or settings UI. Headless Runtime packages need only `Sunder.Sdk`.

Avalonia is the current managed App preset, not a package-format requirement. Framework-agnostic web App targets and future Host-supported .NET UI approaches use their own target tooling rather than taking an unnecessary Avalonia dependency.

Contribution registration has no UI-thread guarantee. Register control types and metadata only; the App constructs and presents controls on its UI thread.

See the [Avalonia package guide](https://github.com/Younics/sunder-core/blob/main/docs/package-development/AVALONIA.md) for registration, navigation, warmup, caching, disposal, and semantic Sunder theme resources.
