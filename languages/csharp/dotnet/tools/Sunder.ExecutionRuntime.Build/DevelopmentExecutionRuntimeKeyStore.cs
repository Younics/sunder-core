using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Sunder.Package.Format;

namespace Sunder.ExecutionRuntime.Build;

internal sealed record DevelopmentExecutionRuntimePublicKey(
    string KeyId,
    string PublicKeyBase64,
    string PublicKeySha256);

internal sealed record DevelopmentExecutionRuntimeTrust(
    int SchemaVersion,
    long Generation,
    DevelopmentExecutionRuntimePublicKey ReleaseKey,
    DevelopmentExecutionRuntimePublicKey NativeAttestationKey);

internal static class DevelopmentExecutionRuntimeKeyStore
{
    public const string StateRootEnvironmentVariable = "SUNDER_EXECUTION_RUNTIME_DEVELOPMENT_STATE_ROOT";
    public const int CurrentSchemaVersion = 1;

    internal const string StateFileName = "current.json";
    internal const string GenerationsDirectoryName = "generations";
    internal const string ReleasePrivateKeyFileName = "release.ed25519.private";
    internal const string ReleasePublicKeyFileName = "release.ed25519.pub";
    internal const string NativeAttestationPrivateKeyFileName = "native-attestation.ed25519.private";
    internal const string NativeAttestationPublicKeyFileName = "native-attestation.ed25519.pub";
    internal const string GenerationTrustFileName = "trust.json";

    private const int PrivateKeyBytes = 32;
    private const int MaximumStateBytes = 16 * 1024;
    private const long MaximumSafeJsonInteger = 9_007_199_254_740_991;
    private const string ReleaseKeyIdPrefix = "sunder.runtime.development.release.";
    private const string NativeAttestationKeyIdPrefix = "sunder.runtime.development.native-attestation.";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        MaxDepth = 16,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static string GetDefaultStateRoot()
    {
        var localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException(
                "The current user has no local application-data directory for Development execution-runtime keys.");
        }
        return Path.Combine(localData, "Sunder", "development", "execution-runtime-keys", "v1");
    }

    public static string? ConsumeConfiguredStateRoot()
    {
        var configured = Environment.GetEnvironmentVariable(StateRootEnvironmentVariable);
        Environment.SetEnvironmentVariable(StateRootEnvironmentVariable, null);
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    public static DevelopmentExecutionRuntimeTrust Ensure(string? stateRoot = null)
    {
        var root = NormalizeRoot(stateRoot);
        using var stateLock = AcquireStateLock(root);
        InitializeRoot(root);
        var generationsRoot = Path.Combine(root, GenerationsDirectoryName);
        var statePath = Path.Combine(root, StateFileName);
        if (!File.Exists(statePath))
        {
            if (Directory.EnumerateFileSystemEntries(generationsRoot).Any())
            {
                throw InvalidState(
                    "Development execution-runtime key generations exist without a current state marker; run an explicit reset.");
            }
            return CreateGeneration(root, generation: 1).ToTrust();
        }

        var validated = ReadAndValidateState(root);
        try
        {
            return validated.State.ToTrust();
        }
        finally
        {
            validated.DisposePrivateKeys();
        }
    }

    public static DevelopmentExecutionRuntimeTrust Rotate(string? stateRoot = null)
    {
        var root = NormalizeRoot(stateRoot);
        using var stateLock = AcquireStateLock(root);
        InitializeRoot(root);
        var statePath = Path.Combine(root, StateFileName);
        if (!File.Exists(statePath))
        {
            var generationsRoot = Path.Combine(root, GenerationsDirectoryName);
            if (Directory.EnumerateFileSystemEntries(generationsRoot).Any())
            {
                throw InvalidState(
                    "Development execution-runtime key generations exist without a current state marker; run an explicit reset.");
            }
            return CreateGeneration(root, generation: 1).ToTrust();
        }

        var validated = ReadAndValidateState(root);
        DevelopmentExecutionRuntimeState current;
        try
        {
            current = validated.State;
        }
        finally
        {
            validated.DisposePrivateKeys();
        }
        if (current.Generation >= MaximumSafeJsonInteger)
        {
            throw InvalidState("Development execution-runtime trust generation cannot be incremented safely.");
        }
        return CreateGeneration(root, checked(current.Generation + 1)).ToTrust();
    }

    public static void Reset(string? stateRoot = null)
    {
        var root = NormalizeRoot(stateRoot);
        using var stateLock = AcquireStateLock(root);
        if (File.Exists(root))
        {
            throw InvalidState("Development execution-runtime key state root is not a directory.");
        }
        if (!Directory.Exists(root))
        {
            return;
        }
        EnsureNotLink(root, "Development execution-runtime key state root");
        Directory.Delete(root, recursive: true);
    }

    internal static DevelopmentExecutionRuntimeSigningSession OpenSigningSession(string? stateRoot = null)
    {
        var root = NormalizeRoot(stateRoot);
        using var stateLock = AcquireStateLock(root);
        InitializeRoot(root);
        if (!File.Exists(Path.Combine(root, StateFileName)))
        {
            var generationsRoot = Path.Combine(root, GenerationsDirectoryName);
            if (Directory.EnumerateFileSystemEntries(generationsRoot).Any())
            {
                throw InvalidState(
                    "Development execution-runtime key generations exist without a current state marker; run an explicit reset.");
            }
            _ = CreateGeneration(root, generation: 1);
        }

        var validated = ReadAndValidateState(root);
        return new DevelopmentExecutionRuntimeSigningSession(
            validated.State.ToTrust(),
            validated.ReleasePrivateKey,
            validated.NativeAttestationPrivateKey);
    }

    internal static string GetGenerationDirectory(string stateRoot, long generation)
        => Path.Combine(
            Path.GetFullPath(stateRoot),
            GenerationsDirectoryName,
            GenerationDirectoryName(generation));

    private static string NormalizeRoot(string? stateRoot)
    {
        var selected = string.IsNullOrWhiteSpace(stateRoot) ? GetDefaultStateRoot() : stateRoot;
        if (!Path.IsPathFullyQualified(selected))
        {
            throw new ArgumentException(
                "Development execution-runtime key state root must be an absolute path.",
                nameof(stateRoot));
        }
        var root = Path.GetFullPath(selected);
        if (string.Equals(root, Path.GetPathRoot(root), PathComparison))
        {
            throw new ArgumentException(
                "Development execution-runtime key state root cannot be a filesystem root.",
                nameof(stateRoot));
        }
        return root;
    }

    private static FileStream AcquireStateLock(string root)
    {
        var parent = Path.GetDirectoryName(root)
                     ?? throw new InvalidOperationException("Development key state root has no parent directory.");
        Directory.CreateDirectory(parent);
        EnsureNotLink(parent, "Development execution-runtime key state parent");
        var lockPath = root + ".lock";
        var started = Stopwatch.StartNew();
        while (true)
        {
            var existed = File.Exists(lockPath);
            try
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough,
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = PrivateFileMode;
                }
                var stream = new FileStream(lockPath, options);
                try
                {
                    if (!existed)
                    {
                        RestrictFile(stream, lockPath);
                    }
                    ValidatePrivateFile(lockPath, expectedLength: null);
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException) when (started.Elapsed < LockTimeout)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void InitializeRoot(string root)
    {
        if (File.Exists(root))
        {
            throw InvalidState("Development execution-runtime key state root is not a directory.");
        }
        EnsurePrivateDirectory(root);
        var generationsRoot = Path.Combine(root, GenerationsDirectoryName);
        EnsurePrivateDirectory(generationsRoot);
        var expected = new HashSet<string>(PathComparer)
        {
            Path.Combine(root, GenerationsDirectoryName),
            Path.Combine(root, StateFileName),
        };
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (!expected.Contains(entry))
            {
                throw InvalidState(
                    $"Development execution-runtime key state contains unexpected entry '{Path.GetFileName(entry)}'.");
            }
        }
    }

    private static DevelopmentExecutionRuntimeState CreateGeneration(string root, long generation)
    {
        if (generation is <= 0 or > MaximumSafeJsonInteger)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        var generationsRoot = Path.Combine(root, GenerationsDirectoryName);
        var finalDirectory = Path.Combine(generationsRoot, GenerationDirectoryName(generation));
        if (File.Exists(finalDirectory) || Directory.Exists(finalDirectory))
        {
            throw InvalidState($"Development execution-runtime trust generation {generation} already exists.");
        }

        var temporaryDirectory = Path.Combine(generationsRoot, $".create-{Guid.NewGuid():N}");
        byte[]? releasePrivate = null;
        byte[]? nativeAttestationPrivate = null;
        try
        {
            EnsurePrivateDirectory(temporaryDirectory);
            releasePrivate = RandomNumberGenerator.GetBytes(PrivateKeyBytes);
            nativeAttestationPrivate = RandomNumberGenerator.GetBytes(PrivateKeyBytes);
            var releasePublic = DerivePublicKey(releasePrivate);
            var nativeAttestationPublic = DerivePublicKey(nativeAttestationPrivate);
            if (releasePublic.AsSpan().SequenceEqual(nativeAttestationPublic))
            {
                throw new CryptographicException(
                    "Development release and native-attestation keys must be distinct.");
            }

            var state = new DevelopmentExecutionRuntimeState
            {
                SchemaVersion = CurrentSchemaVersion,
                Generation = generation,
                ReleaseKey = CreatePublicState(ReleaseKeyIdPrefix, releasePublic),
                NativeAttestationKey = CreatePublicState(
                    NativeAttestationKeyIdPrefix,
                    nativeAttestationPublic),
            };
            WritePrivateFile(
                Path.Combine(temporaryDirectory, ReleasePrivateKeyFileName),
                releasePrivate);
            WritePrivateFile(
                Path.Combine(temporaryDirectory, NativeAttestationPrivateKeyFileName),
                nativeAttestationPrivate);
            WritePrivateFile(
                Path.Combine(temporaryDirectory, ReleasePublicKeyFileName),
                releasePublic);
            WritePrivateFile(
                Path.Combine(temporaryDirectory, NativeAttestationPublicKeyFileName),
                nativeAttestationPublic);
            var stateBytes = SerializeState(state);
            WritePrivateFile(
                Path.Combine(temporaryDirectory, GenerationTrustFileName),
                stateBytes);
            Directory.Move(temporaryDirectory, finalDirectory);
            WriteCurrentState(root, stateBytes);
            return state;
        }
        catch
        {
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
        finally
        {
            if (releasePrivate is not null)
            {
                CryptographicOperations.ZeroMemory(releasePrivate);
            }
            if (nativeAttestationPrivate is not null)
            {
                CryptographicOperations.ZeroMemory(nativeAttestationPrivate);
            }
        }
    }

    private static ValidatedState ReadAndValidateState(string root)
    {
        InitializeRoot(root);
        var statePath = Path.Combine(root, StateFileName);
        var stateBytes = ReadPrivateFile(statePath, MaximumStateBytes, expectedLength: null);
        var state = DeserializeState(stateBytes, "Development execution-runtime current trust state");
        ValidateStateShape(state);
        if (!stateBytes.AsSpan().SequenceEqual(SerializeState(state)))
        {
            throw InvalidState("Development execution-runtime current trust state is not canonical JSON.");
        }

        var generationDirectories = Directory.EnumerateFileSystemEntries(
                Path.Combine(root, GenerationsDirectoryName))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (generationDirectories.Length != state.Generation)
        {
            throw InvalidState(
                "Development execution-runtime key generations are incomplete or do not match the current trust generation.");
        }

        ValidatedState? current = null;
        for (var index = 0; index < generationDirectories.Length; index++)
        {
            var expectedGeneration = index + 1L;
            var directory = generationDirectories[index];
            if (!string.Equals(
                    Path.GetFileName(directory),
                    GenerationDirectoryName(expectedGeneration),
                    StringComparison.Ordinal))
            {
                throw InvalidState("Development execution-runtime key generation names are not canonical and contiguous.");
            }
            var validated = ValidateGenerationDirectory(directory, expectedGeneration);
            if (expectedGeneration == state.Generation)
            {
                current = validated;
            }
            else
            {
                validated.DisposePrivateKeys();
            }
        }
        if (current is null || current.State != state)
        {
            current?.DisposePrivateKeys();
            throw InvalidState(
                "Development execution-runtime current trust state does not match its immutable generation.");
        }
        return current;
    }

    private static ValidatedState ValidateGenerationDirectory(string directory, long expectedGeneration)
    {
        ValidatePrivateDirectory(directory);
        var expected = new HashSet<string>(PathComparer)
        {
            Path.Combine(directory, ReleasePrivateKeyFileName),
            Path.Combine(directory, ReleasePublicKeyFileName),
            Path.Combine(directory, NativeAttestationPrivateKeyFileName),
            Path.Combine(directory, NativeAttestationPublicKeyFileName),
            Path.Combine(directory, GenerationTrustFileName),
        };
        var entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
        if (entries.Length != expected.Count || entries.Any(entry => !expected.Contains(entry)))
        {
            throw InvalidState(
                $"Development execution-runtime key generation {expectedGeneration} has an unexpected inventory.");
        }

        var stateBytes = ReadPrivateFile(
            Path.Combine(directory, GenerationTrustFileName),
            MaximumStateBytes,
            expectedLength: null);
        var state = DeserializeState(
            stateBytes,
            $"Development execution-runtime trust generation {expectedGeneration}");
        ValidateStateShape(state);
        if (state.Generation != expectedGeneration || !stateBytes.AsSpan().SequenceEqual(SerializeState(state)))
        {
            throw InvalidState(
                $"Development execution-runtime trust generation {expectedGeneration} is not canonical or has the wrong identity.");
        }

        var releasePrivate = ReadPrivateFile(
            Path.Combine(directory, ReleasePrivateKeyFileName),
            PrivateKeyBytes,
            PrivateKeyBytes);
        var nativeAttestationPrivate = ReadPrivateFile(
            Path.Combine(directory, NativeAttestationPrivateKeyFileName),
            PrivateKeyBytes,
            PrivateKeyBytes);
        try
        {
            var releasePublic = ReadPrivateFile(
                Path.Combine(directory, ReleasePublicKeyFileName),
                SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes,
                SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes);
            var nativeAttestationPublic = ReadPrivateFile(
                Path.Combine(directory, NativeAttestationPublicKeyFileName),
                SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes,
                SunderExecutionRuntimeFormat.Ed25519PublicKeyBytes);
            ValidateKeyState(state.ReleaseKey!, ReleaseKeyIdPrefix, releasePrivate, releasePublic);
            ValidateKeyState(
                state.NativeAttestationKey!,
                NativeAttestationKeyIdPrefix,
                nativeAttestationPrivate,
                nativeAttestationPublic);
            if (releasePublic.AsSpan().SequenceEqual(nativeAttestationPublic))
            {
                throw InvalidState(
                    $"Development execution-runtime trust generation {expectedGeneration} reuses key material across signing roles.");
            }
            return new ValidatedState(state, releasePrivate, nativeAttestationPrivate);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(releasePrivate);
            CryptographicOperations.ZeroMemory(nativeAttestationPrivate);
            throw;
        }
    }

    private static void ValidateStateShape(DevelopmentExecutionRuntimeState state)
    {
        if (state.SchemaVersion != CurrentSchemaVersion
            || state.Generation is <= 0 or > MaximumSafeJsonInteger
            || state.ReleaseKey is null
            || state.NativeAttestationKey is null)
        {
            throw InvalidState("Development execution-runtime trust state has an unsupported shape.");
        }
    }

    private static void ValidateKeyState(
        DevelopmentExecutionRuntimePublicKeyState state,
        string keyIdPrefix,
        ReadOnlySpan<byte> privateKey,
        ReadOnlySpan<byte> publicKey)
    {
        var derivedPublic = DerivePublicKey(privateKey);
        if (!CryptographicOperations.FixedTimeEquals(derivedPublic, publicKey))
        {
            throw InvalidState($"Development execution-runtime key '{state.KeyId}' does not match its public key.");
        }
        var expected = CreatePublicState(keyIdPrefix, publicKey);
        if (state != expected)
        {
            throw InvalidState($"Development execution-runtime key '{state.KeyId}' has inconsistent public metadata.");
        }
    }

    private static DevelopmentExecutionRuntimePublicKeyState CreatePublicState(
        string keyIdPrefix,
        ReadOnlySpan<byte> publicKey)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        return new DevelopmentExecutionRuntimePublicKeyState
        {
            KeyId = keyIdPrefix + fingerprint[..24],
            PublicKeyBase64 = Convert.ToBase64String(publicKey),
            PublicKeySha256 = fingerprint,
        };
    }

    private static byte[] DerivePublicKey(ReadOnlySpan<byte> privateKey)
        => new Ed25519PrivateKeyParameters(privateKey).GeneratePublicKey().GetEncoded();

    private static void WriteCurrentState(string root, ReadOnlySpan<byte> stateBytes)
    {
        var statePath = Path.Combine(root, StateFileName);
        var temporaryPath = Path.Combine(root, $".{StateFileName}.write-{Guid.NewGuid():N}");
        try
        {
            WritePrivateFile(temporaryPath, stateBytes);
            File.Move(temporaryPath, statePath, overwrite: true);
            ValidatePrivateFile(statePath, stateBytes.Length);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static byte[] SerializeState(DevelopmentExecutionRuntimeState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateJsonOptions);
        var result = new byte[bytes.Length + 1];
        bytes.CopyTo(result, 0);
        result[^1] = (byte)'\n';
        return result;
    }

    private static DevelopmentExecutionRuntimeState DeserializeState(ReadOnlySpan<byte> bytes, string label)
        => StrictJson.Deserialize<DevelopmentExecutionRuntimeState>(bytes, label);

    private static void EnsurePrivateDirectory(string path)
    {
        if (File.Exists(path))
        {
            throw InvalidState($"Secure Development key directory '{path}' is a file.");
        }
        if (!Directory.Exists(path))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(path, PrivateDirectoryMode);
            }
            RestrictDirectory(path);
        }
        ValidatePrivateDirectory(path);
    }

    private static void WritePrivateFile(string path, ReadOnlySpan<byte> bytes)
    {
        EnsureNotLink(Path.GetDirectoryName(path)!, "Development execution-runtime key directory");
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
        using (var stream = new FileStream(path, options))
        {
            RestrictFile(stream, path);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        ValidatePrivateFile(path, bytes.Length);
    }

    private static byte[] ReadPrivateFile(string path, int maximumLength, int? expectedLength)
    {
        ValidatePrivateFile(path, expectedLength);
        var observed = new FileInfo(path);
        if (observed.Length < 0 || observed.Length > maximumLength)
        {
            throw InvalidState($"Secure Development key file '{path}' exceeds its bounded size.");
        }
        var length = checked((int)observed.Length);
        var writeTime = observed.LastWriteTimeUtc;
        var bytes = new byte[length];
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   bufferSize: 4096,
                   FileOptions.SequentialScan))
        {
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw InvalidState($"Secure Development key file '{path}' changed while being read.");
            }
        }
        observed.Refresh();
        if (observed.Length != length || observed.LastWriteTimeUtc != writeTime)
        {
            throw InvalidState($"Secure Development key file '{path}' changed while being read.");
        }
        ValidatePrivateFile(path, expectedLength);
        return bytes;
    }

    private static void ValidatePrivateDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw InvalidState($"Secure Development key directory '{path}' is missing.");
        }
        EnsureNotLink(path, "Secure Development key directory");
        if (OperatingSystem.IsWindows())
        {
            ValidateWindowsAccess(path, isDirectory: true);
        }
        else if (File.GetUnixFileMode(path) != PrivateDirectoryMode)
        {
            throw InvalidState(
                $"Secure Development key directory '{path}' must grant access only to its owner (0700).");
        }
    }

    private static void ValidatePrivateFile(string path, int? expectedLength)
    {
        if (!File.Exists(path))
        {
            throw InvalidState($"Secure Development key file '{path}' is missing.");
        }
        EnsureNotLink(path, "Secure Development key file");
        if (expectedLength is { } length && new FileInfo(path).Length != length)
        {
            throw InvalidState($"Secure Development key file '{path}' has an invalid length.");
        }
        if (OperatingSystem.IsWindows())
        {
            ValidateWindowsAccess(path, isDirectory: false);
        }
        else if (File.GetUnixFileMode(path) != PrivateFileMode)
        {
            throw InvalidState(
                $"Secure Development key file '{path}' must grant access only to its owner (0600).");
        }
    }

    private static void RestrictDirectory(string path)
    {
        EnsureNotLink(path, "Secure Development key directory");
        if (OperatingSystem.IsWindows())
        {
            RestrictWindowsAccess(path, isDirectory: true);
        }
        else
        {
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
    }

    private static void RestrictFile(FileStream stream, string path)
    {
        EnsureNotLink(path, "Secure Development key file");
        if (OperatingSystem.IsWindows())
        {
            RestrictWindowsAccess(stream);
        }
        else
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsAccess(FileStream stream)
    {
        var identity = WindowsIdentity.GetCurrent().User
                       ?? throw InvalidState("The current Windows user has no security identifier.");
        var security = new FileSecurity();
        security.SetOwner(identity);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        stream.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsAccess(string path, bool isDirectory)
    {
        var identity = WindowsIdentity.GetCurrent().User
                       ?? throw InvalidState("The current Windows user has no security identifier.");
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetOwner(identity);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            isDirectory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        if (isDirectory)
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsAccess(string path, bool isDirectory)
    {
        var identity = WindowsIdentity.GetCurrent().User
                       ?? throw InvalidState("The current Windows user has no security identifier.");
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!identity.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected)
        {
            throw InvalidState($"Secure Development key path '{path}' is not owned and isolated to the current user.");
        }
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (rules.Length != 1
            || rules[0].AccessControlType != AccessControlType.Allow
            || !identity.Equals(rules[0].IdentityReference)
            || (rules[0].FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl)
        {
            throw InvalidState($"Secure Development key path '{path}' grants access outside the current user.");
        }
    }

    private static void EnsureNotLink(string path, string label)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidState($"{label} '{path}' must not be a link or reparse point.");
        }
    }

    private static string GenerationDirectoryName(long generation)
        => generation.ToString("D16", CultureInfo.InvariantCulture);

    private static InvalidDataException InvalidState(string message)
        => new(message);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record DevelopmentExecutionRuntimeState
    {
        [JsonPropertyName("schemaVersion"), JsonRequired]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("generation"), JsonRequired]
        public long Generation { get; init; }

        [JsonPropertyName("releaseKey"), JsonRequired]
        public DevelopmentExecutionRuntimePublicKeyState? ReleaseKey { get; init; }

        [JsonPropertyName("nativeAttestationKey"), JsonRequired]
        public DevelopmentExecutionRuntimePublicKeyState? NativeAttestationKey { get; init; }

        public DevelopmentExecutionRuntimeTrust ToTrust()
            => new(
                SchemaVersion,
                Generation,
                ReleaseKey!.ToPublicKey(),
                NativeAttestationKey!.ToPublicKey());
    }

    private sealed record DevelopmentExecutionRuntimePublicKeyState
    {
        [JsonPropertyName("keyId"), JsonRequired]
        public string KeyId { get; init; } = string.Empty;

        [JsonPropertyName("publicKeyBase64"), JsonRequired]
        public string PublicKeyBase64 { get; init; } = string.Empty;

        [JsonPropertyName("publicKeySha256"), JsonRequired]
        public string PublicKeySha256 { get; init; } = string.Empty;

        public DevelopmentExecutionRuntimePublicKey ToPublicKey()
            => new(KeyId, PublicKeyBase64, PublicKeySha256);
    }

    private sealed record ValidatedState(
        DevelopmentExecutionRuntimeState State,
        byte[] ReleasePrivateKey,
        byte[] NativeAttestationPrivateKey)
    {
        public void DisposePrivateKeys()
        {
            CryptographicOperations.ZeroMemory(ReleasePrivateKey);
            CryptographicOperations.ZeroMemory(NativeAttestationPrivateKey);
        }
    }
}

internal sealed class DevelopmentExecutionRuntimeSigningSession
    : ISunderExecutionRuntimeArchiveSigner, IDisposable
{
    private readonly byte[] _releasePrivateKey;
    private readonly byte[] _nativeAttestationPrivateKey;
    private bool _disposed;

    public DevelopmentExecutionRuntimeSigningSession(
        DevelopmentExecutionRuntimeTrust trust,
        byte[] releasePrivateKey,
        byte[] nativeAttestationPrivateKey)
    {
        Trust = trust;
        _releasePrivateKey = releasePrivateKey;
        _nativeAttestationPrivateKey = nativeAttestationPrivateKey;
    }

    public DevelopmentExecutionRuntimeTrust Trust { get; }

    public string KeyId => Trust.ReleaseKey.KeyId;

    public byte[] Sign(ReadOnlySpan<byte> signatureInput)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Sign(signatureInput, _releasePrivateKey);
    }

    public byte[] SignNativeAttestation(ReadOnlySpan<byte> signatureInput)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Sign(signatureInput, _nativeAttestationPrivateKey);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CryptographicOperations.ZeroMemory(_releasePrivateKey);
        CryptographicOperations.ZeroMemory(_nativeAttestationPrivateKey);
    }

    private static byte[] Sign(ReadOnlySpan<byte> input, byte[] privateKey)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey));
        signer.BlockUpdate(input);
        return signer.GenerateSignature();
    }
}
