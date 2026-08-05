using System.Runtime.InteropServices;
using System.Text.Json;
using Sunder.Host.Contracts;

namespace Sunder.Host.Supervisor;

internal sealed record HostLifecyclePersistentState(
    HostRuntimeDesiredState DesiredState,
    long DeploymentGeneration,
    string? ActiveOperationId,
    IReadOnlyList<HostOperationDescriptor> Operations)
{
    public static HostLifecyclePersistentState Initial { get; } = new(
        HostRuntimeDesiredState.Running,
        0,
        null,
        []);
}

internal sealed class HostLifecycleStore : IDisposable
{
    private const int FormatVersion = 1;
    private const int MaximumTextLength = 2048;
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _statePath;
    private readonly FileStream _instanceLock;

    public HostLifecycleStore(string hostRootPath)
    {
        var stateRoot = Path.Combine(Path.GetFullPath(hostRootPath), "runtime", "v1");
        Directory.CreateDirectory(stateRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(stateRoot, PrivateDirectoryMode);
        }

        _statePath = Path.Combine(stateRoot, "lifecycle.json");
        var lockPath = Path.Combine(stateRoot, "lifecycle.lock");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
            }
            _instanceLock = new FileStream(lockPath, options);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(lockPath, PrivateFileMode);
            }
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another Sunder Host instance is already using this lifecycle state root.",
                exception);
        }
    }

    public HostLifecyclePersistentState LoadOrCreate()
    {
        if (!File.Exists(_statePath))
        {
            Save(HostLifecyclePersistentState.Initial);
            return HostLifecyclePersistentState.Initial;
        }

        try
        {
            var document = JsonSerializer.Deserialize<HostLifecycleDocument>(
                File.ReadAllText(_statePath),
                JsonOptions);
            if (document is null
                || document.Version != FormatVersion
                || document.DesiredState is null
                || document.DeploymentGeneration is null
                || document.Operations is null)
            {
                throw new InvalidDataException("Sunder Host lifecycle state has an unsupported format.");
            }

            var state = new HostLifecyclePersistentState(
                document.DesiredState.Value,
                document.DeploymentGeneration.Value,
                document.ActiveOperationId,
                Array.AsReadOnly(document.Operations));
            Validate(state);
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Sunder Host lifecycle state is invalid.", exception);
        }
    }

    public void Save(HostLifecyclePersistentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);
        var directory = Path.GetDirectoryName(_statePath)!;
        var tempPath = Path.Combine(directory, $".lifecycle.{Guid.NewGuid():N}.tmp");
        var document = new HostLifecycleDocument(
            FormatVersion,
            state.DesiredState,
            state.DeploymentGeneration,
            state.ActiveOperationId,
            state.Operations.ToArray());
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
            }

            using (var stream = new FileStream(tempPath, options))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_statePath))
            {
                File.Replace(tempPath, _statePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _statePath);
            }
            if (!OperatingSystem.IsWindows())
            {
                TrySyncDirectory(directory);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void Validate(HostLifecyclePersistentState state)
    {
        if (!Enum.IsDefined(state.DesiredState) || state.DeploymentGeneration < 0)
        {
            throw new InvalidDataException("Sunder Host lifecycle state contains invalid Runtime state.");
        }
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var mutationIds = new HashSet<Guid>();
        HostOperationDescriptor? activeOperation = null;
        foreach (var operation in state.Operations)
        {
            if (string.IsNullOrWhiteSpace(operation.OperationId)
                || !operationIds.Add(operation.OperationId)
                || operation.MutationId == Guid.Empty
                || !mutationIds.Add(operation.MutationId)
                || !IsKnownOperationKind(operation.Kind)
                || operation.ExpectedDeploymentGeneration < 0
                || !Enum.IsDefined(operation.State)
                || operation.CreatedAtUtc > operation.UpdatedAtUtc)
            {
                throw new InvalidDataException("Sunder Host lifecycle state contains an invalid operation.");
            }
            ValidateText(operation.Message, nameof(operation.Message));
            ValidateText(operation.FailureCode, nameof(operation.FailureCode));
            if (operation.State is HostOperationState.Accepted or HostOperationState.Running)
            {
                if (activeOperation is not null)
                {
                    throw new InvalidDataException("Sunder Host lifecycle state contains multiple active operations.");
                }
                activeOperation = operation;
            }
        }

        if (activeOperation is null)
        {
            if (state.ActiveOperationId is not null)
            {
                throw new InvalidDataException("Sunder Host lifecycle state references a missing active operation.");
            }
        }
        else if (!string.Equals(
                     state.ActiveOperationId,
                     activeOperation.OperationId,
                     StringComparison.Ordinal))
        {
            throw new InvalidDataException("Sunder Host lifecycle state has an inconsistent active operation.");
        }
        else
        {
            var expectedDesiredState = activeOperation.Kind == HostOperationKinds.RuntimeStop
                ? HostRuntimeDesiredState.Stopped
                : HostRuntimeDesiredState.Running;
            if (state.DesiredState != expectedDesiredState
                || state.DeploymentGeneration != activeOperation.ExpectedDeploymentGeneration + 1)
            {
                throw new InvalidDataException("Sunder Host lifecycle state has inconsistent active operation intent.");
            }
        }
    }

    private static bool IsKnownOperationKind(string kind)
        => kind is HostOperationKinds.RuntimeStart
            or HostOperationKinds.RuntimeStop
            or HostOperationKinds.RuntimeRestart;

    private static void ValidateText(string? value, string name)
    {
        if (value?.Length > MaximumTextLength)
        {
            throw new InvalidDataException($"Sunder Host lifecycle field '{name}' is too long.");
        }
    }

    private static void TrySyncDirectory(string path)
    {
        var descriptor = NativeMethods.Open(path, 0);
        if (descriptor < 0)
        {
            return;
        }
        try
        {
            _ = NativeMethods.Fsync(descriptor);
        }
        finally
        {
            NativeMethods.Close(descriptor);
        }
    }

    public void Dispose() => _instanceLock.Dispose();

    private sealed record HostLifecycleDocument(
        int? Version,
        HostRuntimeDesiredState? DesiredState,
        long? DeploymentGeneration,
        string? ActiveOperationId,
        HostOperationDescriptor[]? Operations);

    private static class NativeMethods
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        internal static extern int Open(string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static extern int Fsync(int descriptor);

        [DllImport("libc", EntryPoint = "close")]
        internal static extern int Close(int descriptor);
    }
}
