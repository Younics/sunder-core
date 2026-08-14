using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

internal static class WorkerReleaseMetrics
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static Process GetProcess(object worker)
    {
        var process = worker.GetType()
            .GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(worker) as Process;
        Assert.NotNull(process);
        Assert.False(process.HasExited);
        process.Refresh();
        return process;
    }

    public static long DirectoryBytes(string path)
        => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Sum(static file => new FileInfo(file).Length);

    public static async Task RecordAsync(
        string profileName,
        string rid,
        string archivePath,
        long currentRidInstalledBytes,
        TimeSpan startup,
        int workerProcessCount,
        Process workerProcess,
        string? outputPath)
    {
        await Task.Delay(250);
        workerProcess.Refresh();
        var measurement = new WorkerReleaseMeasurement(
            1,
            profileName,
            rid,
            Path.GetFileName(archivePath),
            new FileInfo(archivePath).Length,
            currentRidInstalledBytes,
            startup.TotalMilliseconds,
            workerProcessCount,
            workerProcess.WorkingSet64,
            DateTimeOffset.UtcNow);
        var thresholds = LoadThresholds(profileName);
        Assert.InRange(measurement.ArchiveBytes, 1, thresholds.MaximumArchiveBytes);
        Assert.InRange(measurement.CurrentRidInstalledBytes, 1, thresholds.MaximumCurrentRidInstalledBytes);
        Assert.InRange(measurement.StartupMilliseconds, 0.001, thresholds.MaximumStartupMilliseconds);
        Assert.Equal(thresholds.ExpectedWorkerProcessCount, measurement.WorkerProcessCount);
        Assert.InRange(measurement.WorkerRssBytes, 1, thresholds.MaximumWorkerRssBytes);
        if (string.IsNullOrWhiteSpace(outputPath)) return;

        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(new WorkerReleaseEvidence(measurement, thresholds), JsonOptions));
    }

    private static WorkerReleaseThresholds LoadThresholds(string profileName)
    {
        var path = Path.Combine(FindRepositoryRoot(), "eng", "release", "worker-v3-thresholds.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(1, document.RootElement.GetProperty("formatVersion").GetInt32());
        var profile = document.RootElement.GetProperty("profiles").GetProperty(profileName);
        return JsonSerializer.Deserialize<WorkerReleaseThresholds>(profile, JsonOptions)
               ?? throw new InvalidDataException($"Release threshold profile '{profileName}' was empty.");
    }

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.Core.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Sunder Core repository root.");
    }

    private sealed record WorkerReleaseEvidence(
        WorkerReleaseMeasurement Measurement,
        WorkerReleaseThresholds Thresholds);

    private sealed record WorkerReleaseMeasurement(
        int FormatVersion,
        string Profile,
        string Rid,
        string Archive,
        long ArchiveBytes,
        long CurrentRidInstalledBytes,
        double StartupMilliseconds,
        int WorkerProcessCount,
        long WorkerRssBytes,
        DateTimeOffset CapturedAtUtc);

    private sealed record WorkerReleaseThresholds(
        long MaximumArchiveBytes,
        long MaximumCurrentRidInstalledBytes,
        double MaximumStartupMilliseconds,
        int ExpectedWorkerProcessCount,
        long MaximumWorkerRssBytes);
}
