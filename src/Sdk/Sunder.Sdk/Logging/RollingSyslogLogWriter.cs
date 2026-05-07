using System.Globalization;

namespace Sunder.Sdk.Logging;

internal sealed class RollingSyslogLogWriter : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly string _logsRootPath;
    private readonly string _filePrefix;
    private readonly TimeSpan _retentionPeriod;
    private DateOnly _currentDate;
    private bool _isDisabled;

    public RollingSyslogLogWriter(string logsRootPath, string filePrefix, TimeSpan retentionPeriod)
    {
        _logsRootPath = logsRootPath;
        _filePrefix = filePrefix;
        _retentionPeriod = retentionPeriod;
        _currentDate = DateOnly.FromDateTime(DateTime.Now);
        try
        {
            Directory.CreateDirectory(_logsRootPath);
            DeleteLegacyJsonLinesFiles();
            ApplyRetention(_currentDate);
        }
        catch
        {
            _isDisabled = true;
        }
    }

    public void Write(string line)
    {
        if (_isDisabled)
        {
            return;
        }

        lock (_syncRoot)
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (today != _currentDate)
                {
                    _currentDate = today;
                    ApplyRetention(today);
                }

                File.AppendAllText(BuildFilePath(today), line + Environment.NewLine);
            }
            catch
            {
                // Logging must never fail package activation or execution.
                _isDisabled = true;
            }
        }
    }

    public void Dispose()
    {
    }

    private string BuildFilePath(DateOnly date)
        => Path.Combine(_logsRootPath, string.Create(CultureInfo.InvariantCulture, $"{_filePrefix}-{date:yyyy-MM-dd}.log"));

    private void ApplyRetention(DateOnly today)
    {
        var oldestDate = DateOnly.FromDateTime(DateTime.Now.Subtract(_retentionPeriod));
        foreach (var filePath in Directory.EnumerateFiles(_logsRootPath, $"{_filePrefix}-*.log"))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var datePart = fileName.Length > _filePrefix.Length + 1
                ? fileName[(_filePrefix.Length + 1)..]
                : string.Empty;
            if (DateOnly.TryParseExact(datePart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate))
            {
                if (fileDate < oldestDate)
                {
                    TryDelete(filePath);
                }
            }
            else if (File.GetLastWriteTime(filePath) < today.ToDateTime(TimeOnly.MinValue).Subtract(_retentionPeriod))
            {
                TryDelete(filePath);
            }
        }
    }

    private void DeleteLegacyJsonLinesFiles()
    {
        foreach (var filePath in Directory.EnumerateFiles(_logsRootPath, $"{_filePrefix}-*.jsonl"))
        {
            TryDelete(filePath);
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch
        {
            // Logging must never fail package activation or execution.
        }
    }
}
