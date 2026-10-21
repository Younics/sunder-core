# Data And Logging

> **Applies to:** Sunder SDK `1.1.x`, package manifest V1, .NET 10, and Runtime protocol revision 1.

Sunder separates user settings, opaque state, files, local-path workspaces, secrets, and logs. Choose the narrowest capability that matches the data.

## Choose A Store

| Data | API | Behavior |
| --- | --- | --- |
| User preference declared in a schema | `IPackageSettings` | Runtime validates it and App can render it. |
| Opaque operational string state | `IPackageStorageContext.State` | Package-owned key/value data; never shown as settings. |
| Bounded binary or text file | `IPackageStorageContext.Files` | Atomic package-scoped file replacement. |
| Database or library requiring a path | `IPackageStorageContext.RoleLocalWorkspace` | Persistent local path, separate for App and Runtime roles. |
| Credential or token | `IPackageSecrets` | Encrypted at rest by Runtime; no public enumeration API. |
| Diagnostic event | `IPackageLogging` | Runtime package log or App session log, depending on role. |

Never write mutable data under `ContentRootPath`; package content is a read-only activation snapshot. App data APIs proxy to the package's active Runtime role. An App-only package has no Runtime data owner, so packages requiring shared durable data must implement a Runtime role.

## Portable V1 Limits

`PackageStorageValidation` is the public validator and constant source.

| Input | Exact limit |
| --- | ---: |
| Key | 1 to 256 ASCII characters |
| String value | 1,048,576 UTF-8 bytes |
| Relative path | 1 to 1,024 characters |
| Path segment | 1 to 255 characters |
| One file | 16,777,216 bytes |

Keys are case-sensitive and may contain only ASCII letters, digits, `.`, `-`, and `_`. `ListKeysAsync` returns an ordinally sorted snapshot; an optional prefix is also case-sensitive. Missing key/value/secret/file reads return `null`.

Opaque identifiers from imports, remote systems, users, paths, or extension packages are domain data, not physical key fragments. Preserve the identifier in the domain model and derive a bounded key at the storage boundary:

```csharp
var key = PackageStorageKeyFactory.Create(
    "workspace-bindings.config",
    version: 2,
    opaqueBindingId);
```

The output is `{prefix}.v{version}.{lowercase-sha256-of-exact-UTF8}`. It is deterministic, portable, and independent of the current OS or culture. Do not normalize, truncate, or case-fold the opaque identifier before hashing unless that transformation is part of the domain identity itself.

Portable paths:

- use `/`, including on Windows;
- contain visible ASCII only;
- contain no empty, `.`, or `..` segment;
- contain no segment ending in a space or period;
- contain none of `<`, `>`, `:`, `"`, `|`, `?`, or `*`;
- reject Windows-reserved base names such as `CON`, `NUL`, `COM1`, and `LPT1`; and
- are lexically contained under the package root and cannot traverse a host-observed symbolic link or reparse point.

File-name casing follows the host filesystem. Do not create names that differ only by case.

## State And Files

```csharp
await context.Storage.State.SetValueAsync("sync.cursor", cursor, cancellationToken);
var savedCursor = await context.Storage.State.GetValueAsync("sync.cursor", cancellationToken);

await context.Storage.Files.WriteAsync("cache/index.json", jsonBytes, cancellationToken);
await using var input = await context.Storage.Files.OpenReadAsync("cache/index.json", cancellationToken);
```

State and file APIs are thread-safe. Mutations are atomic: cancellation before commit preserves the previous value/file; once replacement commits, the operation succeeds even if cancellation races afterward. File writes create parent directories. Stream writes advance but never dispose the caller-owned input stream. The caller must dispose streams returned by `OpenReadAsync`.

### Accepted App State Updates

An App which debounces or asynchronously validates a save must register the work
when the change is accepted, not later from `IAsyncDisposable`. Inject
`IPackageAppStateUpdates` into the App service and return its `RunAsync` task to
the caller (or observe it through an owned error-reporting task):

```csharp
return updates.RunAsync("bookmarks", async update =>
{
    using var delay = CancellationTokenSource.CreateLinkedTokenSource(
        update.SettlementRequested, update.CancellationToken);
    try
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300), delay.Token);
    }
    catch (OperationCanceledException error) when (
        error.CancellationToken == delay.Token &&
        update.SettlementRequested.IsCancellationRequested &&
        !update.CancellationToken.IsCancellationRequested)
    {
        // Normal replacement/shutdown requests a flush, not abandonment.
    }
    update.CancellationToken.ThrowIfCancellationRequested();
    await context.Storage.State.SetValueAsync("bookmarks", json, update.CancellationToken);
});
```

The generated App composition supplies this service and infers the required
`app-state-updates.v1` capability. Admission rejects synchronously before invoking
the callback when unpublished, retiring, revoked, or at capacity. Do not mark a
local snapshot accepted until this call admits it. Callbacks run asynchronously,
are not serialized, and must await all storage work; packages own coalescing and
ordering. Use stable portable document keys: an activation admits at most 128
distinct keys and 128 pending callbacks. Do not use one key per edit.

Keep callbacks bounded to state validation and persistence. Do not re-enter App
activation/navigation or wait for the owning activation's retirement from a
callback: settlement can already own the App lifecycle gate. Marshal any optional
UI notifications separately; persistence must not depend on the dispatcher.

Normal handoff closes new admission, requests settlement, and waits within the
existing retirement budget before replacement reads or final revocation. Only
the original accepted callback has settlement authority; a captured context or
forked task loses that authority when the callback ends. Independent security
revocation stays immediate and never grants post-revocation writes. Join accepted
work during disposal; do not start an unregistered final write there.

A newer successful callback can supersede a logical validation failure for the
same key. It cannot erase an uncertain transport/storage mutation. Observe and
report failures; cancellation or a closed HTTP request is not proof that a remote
write was abandoned. This contract does not promise a final save after process
termination, emergency security revocation, or expired settlement budgets.

### Migrating Legacy Keys

Worker V1 runs declared state and secret migrations automatically before activation.
In a canonical single-project package, declare one migration-plan provider:

```csharp
public sealed class StorageMigrations : ISunderWorkerStorageMigrationProvider
{
    public IReadOnlyList<PackageStorageKeyMigration> StateMigrations { get; } =
    [
        PackageStorageKeyMigration.Exact("docker.images:v1", "docker.images.v1"),
    ];
}
```

`SecretMigrations` can be supplied on the same provider; either list defaults to
empty. Generated composition snapshots these plans before advertising the Worker
catalog. Implement `ISunderWorkerStorageMigrationPreparation` when dynamic rules
need package-local routing initialized from read-only state:

```csharp
public sealed class MigrationPreparation : ISunderWorkerStorageMigrationPreparation
{
    public IReadOnlyList<string> LiveKeys { get; private set; } = [];

    public async ValueTask PrepareStorageMigrationsAsync(
        SunderWorkerMigrationPreparationContext context,
        CancellationToken cancellationToken = default)
    {
        LiveKeys = await context.ListStateKeysAsync("workspace-bindings.", cancellationToken);
    }
}
```

Preparation participants are DI-managed Worker roots and run in dependency-first
order. They may also implement the plan-provider interface; constructors and plan
getters must not access Host data. Preparation updates in-memory state used by the
already declared rules, not the immutable advertised plan.

The equivalent low-level Worker options are:

```csharp
return new SunderWorkerOptions(providers)
{
    StateMigrations =
    [
        PackageStorageKeyMigration.OpaqueId(
            "workspace-bindings:",
            ":config",
            "workspace-bindings.config",
            destinationVersion: 2),
        PackageStorageKeyMigration.Exact("docker.images:v1", "docker.images.v1"),
    ],
    OnBeforeStorageMigrations = async (preparation, cancellationToken) =>
    {
        var liveKeys = await preparation.ListStateKeysAsync("workspace-bindings.", cancellationToken);
        // Derive in-memory routing for DynamicCleanup without mutating persistent data.
    },
};
```

`OnBeforeStorageMigrations` receives the exact committed activation and session generation plus read-only `GetStateValueAsync`, `ContainsStateKeyAsync`, and `ListStateKeysAsync` operations. It runs after every process commits but before any declared migration or activation callback. Normal state access, all mutations, settings, files, secrets, and RPC remain unavailable during this barrier.

`OpaqueId` extracts the exact non-empty text between the legacy prefix and suffix, then derives the destination with `PackageStorageKeyFactory`. `Exact` handles fixed legacy keys. Rules and matching use ordinal comparison. `DynamicCleanup` is reserved for bounded package-owned key grammars where deleted entities or crash leftovers cannot be enumerated from live metadata. Its key-only resolver must return `NoMatch` for anything outside that grammar, `Delete` for recognized obsolete entries, or `Rewrite` for a current destination.

The host performs one document-locked atomic rewrite, increments the document revision, and retains the original as a `migration-backup` file. Repeating the same migration is a no-op. If a prior strict read quarantined a structurally valid document solely because it contained covered legacy keys, migration can recover that retained document. Authentication failures, malformed documents, unknown invalid keys, keys matching multiple rules, and unequal values colliding under ordinary rewrite rules remain fail-closed. Dynamic cleanup preserves an existing destination; without one, identical remnants converge, the highest declared precedence wins, and equally preferred conflicting values are removed without selecting one. Package code cannot enumerate secrets or inspect values through a dynamic resolver.

Use `RoleLocalWorkspace` only when an API needs a real local path:

```csharp
var databasePath = context.Storage.RoleLocalWorkspace.GetLocalPath("db/items.sqlite");
```

App and Runtime workspace roots are different. A Runtime worker receives one stable workspace capability during configuration, but its root and path resolution remain unavailable until activation; first resolve paths from `OnActivated`, `ISunderWorkerLifecycleService.ActivateAsync`, or later active work. The Host binds the canonical root through the private Worker control plane only after the previous generation has stopped and rollback snapshots exist. The root persists, but the capability belongs to the current activation and package code must not dispose it. A returned path is lexical authorization only; packages remain responsible for links they create or pass to third-party filesystem APIs.

## Settings

Worker V1 advertises at most one schema through its immutable options:

```csharp
return new SunderWorkerOptions(providers)
{
    SettingsSchema = new PackageSettingsSchema(
        summary: "Quickstart settings.",
        sections:
        [
            new PackageSettingsSection(
                sectionId: "general",
                title: "General",
                description: null,
                fields:
                [
                    new PackageSettingsField(
                        key: "greeting.enabled",
                        label: "Enable greeting",
                        kind: PackageSettingsFieldKind.Boolean,
                        defaultValue: "true")
                ])
        ])
};
```

The schema is advertised with the worker's immutable composition and cannot change after `worker.ready`.

Package id and display name come from the activating manifest. A schema must have at least one section; every section must have at least one field. Section ids and field keys use the portable key grammar. Section ids are unique, and field keys are unique across the entire schema, using ordinal case-sensitive comparison.

Field behavior:

| Kind | Validation |
| --- | --- |
| `Text` | Bounded UTF-8 string; whitespace is rejected when required. |
| `Boolean` | `bool.TryParse` must accept the value; write lowercase `true` or `false` for canonical data. |
| `Select` | At least one option; option values are ordinally unique; stored/default value must be declared. |
| `Secret` | Has no default and cannot be read or written through `IPackageSettings`; use `IPackageSecrets`. |

Non-select fields cannot declare options. Secret defaults, duplicate options, invalid defaults, duplicate ids, null entries, and empty collections throw during schema construction and therefore fail package activation.

`GetValueAsync` returns a stored value or the schema default. `GetStoredValueAsync` returns only the stored value. `DeleteValueAsync` makes the default effective again, but rejects deletion of a required field that has no nonblank default. Undeclared and secret keys are rejected.

## Secrets

```csharp
await context.Secrets.SetSecretAsync("provider.token", token, cancellationToken);
var token = await context.Secrets.GetSecretAsync("provider.token", cancellationToken);
await context.Secrets.DeleteSecretAsync("provider.token", cancellationToken);
```

Secrets use the same 256-character key and 1 MiB UTF-8 value limits. Runtime encrypts the secrets document with AES-GCM and a random 256-bit master key. It prefers DPAPI CurrentUser on Windows, Keychain on macOS, and Secret Service on Linux. If the platform provider is unavailable while creating a new key, Runtime falls back to a current-user-restricted key file and opportunistically re-protects that key when the provider becomes available.

Encryption at rest does not protect a secret from the executing package that requests it or from compromise of the current user account. Do not copy plaintext into state, files, logs, Stack archives, exception messages, process arguments, or App view models longer than necessary.

Secrets fail closed. Missing/unavailable key material preserves ciphertext. Authentication or structural corruption is quarantined when possible and replaced by a durable recovery marker; explicit Runtime reset/recovery is required. Runtime does not silently create a replacement key or reset corrupt secrets.

## Logging

Use either standard Microsoft logging or named structured events:

```csharp
var logger = context.Logging.LoggerFactory.CreateLogger("MyPackage.Sync");
logger.LogInformation("Imported {Count} items", count);

await context.Logging.Events.InformationAsync(
    "items.imported",
    "Import completed.",
    new Dictionary<string, object?> { ["item_count"] = count },
    cancellationToken);
```

Runtime writes separate daily `runtime-YYYY-MM-DD.log` and `events-YYYY-MM-DD.log` files and retains seven days by default. Logging is thread-safe and best effort: an I/O failure disables that writer rather than failing package activation or execution. Worker V1 routes both logging APIs through a dedicated bounded Host-call lane, drains lifecycle logs before shutdown acknowledgement, and bounds each entry to 64 scalar attributes and five exception entries. Packages should still avoid high-volume loops.

Runtime structured attribute values are redacted when the attribute key contains a normalized sensitive fragment such as `apikey`, `authorization`, `cookie`, `password`, `refresh`, `secret`, or `token`. This heuristic does not sanitize messages, exception text/stack traces, event names, categories, or unknown sensitive keys. Never rely on it as a secret boundary.

App-role logging goes to the App session log, not the Runtime package log files. App structured-event attributes are not persisted as structured fields in V1.

The App developer-log stream is a bounded presentation of Runtime logs, not a write limit:

| Stream behavior | Limit |
| --- | ---: |
| Parsed line | 16 KiB |
| Displayed message | 4,096 characters |
| Displayed category | 256 characters |
| Replay entries | 2,000 |
| Per-subscriber queue | 128 |
| Initial files considered | 128 |
| Initial bytes total / per file | 8 MiB / 2 MiB |
| Bytes read per file-change pass | 1 MiB |

Oversized/truncated entries are marked. Runtime discovers and tails files; App never receives their local paths.

## Errors

| Condition | Package-visible result |
| --- | --- |
| Null required argument | `ArgumentNullException`. |
| Invalid key, prefix, path, value, settings value, unreadable write stream, or oversized write | `ArgumentException`. |
| Existing/returned file exceeds 16 MiB or Runtime returns contract-invalid data | `InvalidDataException`. |
| Host filesystem failure | A sanitized `unavailable` Worker RPC error. |
| Cancellation before commit/read completion | `OperationCanceledException`. |
| Undeclared/secret setting or required setting deletion | `ArgumentException` with the setting reason. |
| Runtime-backed App data unavailable | Authenticated Runtime request failure; do not assume an App-local fallback. |
| Corrupt state/settings/secrets | Fail-closed storage error; package activation or request fails without implicit reset. |
| Unknown, ambiguous, or colliding legacy key migration | `InvalidDataException`, `InvalidOperationException`, or fail-closed recovery error without implicit reset. |

Validate at input boundaries with `PackageStorageValidation` when producing identifiers dynamically. Do not catch a corruption or key-unavailable error and overwrite the store; direct users to [Troubleshooting](TROUBLESHOOTING.md).
