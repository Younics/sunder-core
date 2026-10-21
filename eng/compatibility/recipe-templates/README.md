# SDK Compatibility Recipe Templates

These `.json.in` files describe the supported retained-input corpus profiles. They
contain placeholders and are not release evidence or executable release recipes.

Create a reviewed recipe under `eng/compatibility/recipes/<reference-id>/` only
after selecting genuine source, package, dependency-closure, runtime and Host/App
artifacts. Bind their exact identities and the owning test selectors through the
release qualification tools. For the first supported baseline, use actual tested
candidate artifacts; do not describe them as previously released binaries.

- `old-source-new-sdk.json.in`: canonical C# source rebuild.
- `old-source-new-sdk-typescript.json.in`: TypeScript source rebuild.
- `old-source-new-sdk-checked-js.json.in`: genuine checked-JavaScript source rebuild.
- `old-package-new-host.json.in`: retained C# Worker activation.
- `old-package-new-host-dotnet-rpc.json.in`: C# Worker unary/stream/cancellation probe.
- `old-package-new-host-node-rpc.json.in`: Node Worker unary/stream/cancellation probe.
- `old-package-new-host-avalonia-app.json.in`: managed, headless App package activation
  with the selected App/SDK assemblies.
- `new-feature-old-host.json.in`: precise unsupported-feature admission rejection.

See the [corpus input contract](../../../docs/testing/sdk-compatibility-corpus.md),
[expanded profile limits](../../../docs/testing/sdk-compatibility-probes.md), and
[release qualification](../../../docs/SUNDER-CORE-RELEASES.md).
