# Compatibility probe authoring sources

These are **new probe sources**, not captured historical releases. Their package
versions and current SDK defaults are authoring choices. Retain actual build
inputs/results and their provenance before using them as a compatibility reference.
The [owning profiles](../../../docs/testing/sdk-compatibility-probes.md) define the
required behavior and artifact layout.

Copy the selected fixture to a disposable authoring directory outside the Core
workspace so it cannot inherit source/project references or workspace build props.
Use the actual selected package archives; do not satisfy `1.1.0` from a stale global
NuGet/npm cache merely because its version string matches.

## .NET Worker and App

`dotnet-worker/` is an advanced Worker V1 executable fixture with an explicit
private RPC descriptor. `app/` is a managed App module/view fixture. Each references
the actual NuGet SDK/Build packages and uses the ordinary build/package targets.

From the copied project directory, using your real candidate version and local
NuGet feed (including third-party dependencies):

```sh
dotnet restore <project>.csproj --source "$FEED" --packages "$NEW_ISOLATED_CACHE" \
  -p:CorpusSdkVersion="$SDK_VERSION" -p:NuGetAudit=false
dotnet msbuild <project>.csproj -t:PackSunderPackage -p:Configuration=Release \
  -p:CorpusSdkVersion="$SDK_VERSION" -nr:false
```

Use a fresh cache and keep the resulting `packages.lock.json`. The default target
is the actual build RID; create/test each native RID's inputs in its owning lane.
Keep the original `.sunderpkg` and complete dependency closure. No compatibility
owner invokes these build commands on an old binary artifact.

## TypeScript and checked JavaScript

Copy `node-ts/` or `node-js/` into the disposable directory, then copy the authored
`node-common/probe.js` helper to its `src/probe.js`. That complete copied tree is
the authored input to retain. JS services and helper are real checked JavaScript
with JSDoc; the JS profile has `allowJs`, `checkJs`, `strict` and no `.ts` source.

Use Node >=24.18.1 <25 and npm 11.19 (or the reviewed original toolchain). Select
the actual local candidate SDK/tool tarballs in one install operation:

```sh
npm install --ignore-scripts --no-audit --no-fund --save-exact "$SDK_TGZ" "$TOOL_TGZ"
node node_modules/@sunder/package-tool/dist/cli.js generate --project .
node node_modules/typescript/bin/tsc --project tsconfig.json --noEmit
node node_modules/@sunder/package-tool/dist/cli.js build --project . --rid "$RID"
node node_modules/@sunder/package-tool/dist/cli.js package --project . --discovery-root dist/targets
```

Retain `package.json`, **the resulting original npm lock v3**, all authored files
and contract/API locks, and the exact original dependency `.tgz` closure. For each
applicable lock entry, the tarball's id/version and SHA-512 must agree with the lock.
Include native optional dependencies for the lanes that will rebuild the source;
a macOS-only closure does not qualify the Linux portable lane. Exclude generated
`node_modules`, `dist` and `.sunder` from the source archive. Retain the generated
`.sunderpkg` separately for binary execution. Copying these current sources later
cannot stand in for an unavailable old authored-source archive.

## Probe expectations

Both Worker implementations use the explicit `sunder.compatibility.probe.v1`
challenge/report protocol documented with the owners. Read actual provider,
descriptor and ABI identities from the generated manifest and review them into
the retained `probe` layout object. Do not invent hashes or source commits.

The App fixture is deliberately independent of Runtime RPC so the provided App
component environment can test activation, isolated load context, shared SDK
identity, DI/view behavior and disposal using the existing headless harness.
Its fixed package/view id is `compatibility.app.probe`.

Original source/SDK version evidence, retention identities and native execution
results belong to their actual input owners. These files contain no release
qualification, frozen ledger update or publication claim.
