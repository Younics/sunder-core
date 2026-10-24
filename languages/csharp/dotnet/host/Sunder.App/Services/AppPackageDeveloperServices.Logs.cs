using Sunder.Runtime.Contracts;
using Sunder.Sdk.Abstractions;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Sunder.Sdk.Logging;

namespace Sunder.App.Services;

public sealed partial class AppPackageDeveloperServices
{
    Task<PackageDevelopmentLogs> IAppPackageDevelopmentBackend.GetLogsAsync(string packageId, int maximumEntries, CancellationToken token)
        => ReadLogsAsync(packageId, new() { MaximumEntries = maximumEntries }, token);

    Task<PackageDevelopmentLogs> IAppPackageDevelopmentBackend.GetLogsAsync(string packageId, PackageDevelopmentLogQuery query, CancellationToken token)
        => ReadLogsAsync(packageId, query, token);

    private async Task<PackageDevelopmentLogs> ReadLogsAsync(string packageId, PackageDevelopmentLogQuery query, CancellationToken token)
    {
        query.Validate();
        var filter = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{packageId}\n{query.Since?.UtcTicks}\n{query.Until?.UtcTicks}\n{query.MinimumLevel}\n{query.Source}\n{query.Contains}")));
        var cursor = ReadCursor(query.Cursor, filter);
        using var client = clients.CreateClient<IRuntimeLogClient>();
        List<PackageLogEntryDescriptor> runtime = [];
        Guid? runtimeId = null;
        long runtimeCutoff = 0;
        long after = 0;
        var gap = false;
        // The Runtime feed retains at most 2,000 entries. Read at most two bounded
        // pages; filter before exposing anything to the development package.
        for (var page = 0; page < 2; page++)
        {
            var logs = await client.GetPackageLogSnapshotAsync(after, 1000, token).ConfigureAwait(false);
            if (runtimeId is not null && runtimeId != logs.RuntimeInstanceId)
                throw new InvalidOperationException("The Runtime changed while development logs were being observed.");
            runtimeId = logs.RuntimeInstanceId;
            if (page == 0) runtimeCutoff = cursor?.RuntimeCutoff ?? logs.SequenceId;
            gap |= logs.HistoryGap;
            gap |= page == 0 && logs.Entries.Count > 0 && logs.Entries[0].SequenceId > 1;
            runtime.AddRange(logs.Entries.Where(entry => entry.PackageId == packageId && entry.SequenceId <= runtimeCutoff));
            if (logs.Entries.Count == 0 || logs.Entries[^1].SequenceId >= logs.SequenceId) break;
            after = logs.Entries[^1].SequenceId;
            if (page == 1) gap = true;
        }
        var appSnapshot = AppSessionLog.Snapshot();
        gap = query.Source != "app" && gap || query.Source != "runtime" && appSnapshot.Count > 0 && appSnapshot[0].SequenceId > 1;
        if (cursor is not null && (query.Source != "app" && cursor.RuntimeId != runtimeId || query.Source != "runtime" && cursor.AppId != AppSessionLog.InstanceId))
            return new(packageId, [], false) { HistoryGap = true };
        var appCutoff = cursor?.AppCutoff ?? appSnapshot.LastOrDefault()?.SequenceId ?? 0;
        var app = appSnapshot.Where(entry => entry.Scope == DeveloperLogEntryScope.Package && entry.Source == packageId && entry.SequenceId <= appCutoff)
            .Select(entry => new LogItem(entry.SequenceId, new(entry.Timestamp, "app", entry.Level.ToString(), "package", entry.Message, false)));
        var all = runtime.Select(entry => new LogItem(entry.SequenceId,
                new(entry.Timestamp, "runtime", entry.Level.ToString(), entry.Category, entry.Message, entry.Truncated) { Properties = entry.Properties }))
            .Concat(app).Where(item => Matches(item, query) && (cursor is null || Before(item, cursor)))
            .OrderByDescending(item => item.Entry.Timestamp).ThenByDescending(item => item.Entry.Source, StringComparer.Ordinal)
            .ThenByDescending(item => item.Sequence).ToArray();
        List<LogItem> selected = [];
        foreach (var item in all.Take(query.MaximumEntries))
        {
            token.ThrowIfCancellationRequested();
            var entry = item.Entry;
            var category = ClipLogText(entry.Category, 256);
            var message = ClipLogText(entry.Message, 4096);
            var bounded = entry with { Category = category, Message = message,
                WasTruncated = entry.WasTruncated || category != entry.Category || message != entry.Message,
                Properties = entry.Properties.Take(16).ToDictionary(pair => ClipLogText(pair.Key, 64), pair => ClipLogText(pair.Value, 256), StringComparer.Ordinal) };
            selected.Add(item with { Entry = bounded });
            while (JsonSerializer.SerializeToUtf8Bytes(Render()).Length > query.MaximumBytes)
            {
                if (bounded.Message.Length > 0) bounded = bounded with { Message = ClipLogText(bounded.Message, bounded.Message.Length / 2), WasTruncated = true };
                else if (bounded.Properties.Count > 0) bounded = bounded with { Properties = new Dictionary<string, string>(), WasTruncated = true };
                else if (bounded.Category.Length > 0) bounded = bounded with { Category = ClipLogText(bounded.Category, bounded.Category.Length / 2), WasTruncated = true };
                else break;
                selected[^1] = item with { Entry = bounded };
            }
            if (JsonSerializer.SerializeToUtf8Bytes(Render()).Length > query.MaximumBytes)
            { selected.RemoveAt(selected.Count - 1); break; }
        }
        return Render();

        PackageDevelopmentLogs Render()
        {
            var more = selected.Count < all.Length;
            var next = more && selected.Count > 0 ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new LogCursor(
                filter, runtimeId ?? Guid.Empty, AppSessionLog.InstanceId, runtimeCutoff, appCutoff,
                selected[^1].Entry.Timestamp, selected[^1].Entry.Source, selected[^1].Sequence))) : null;
            return new(packageId, selected.AsEnumerable().Reverse().Select(item => item.Entry).ToArray(), selected.Any(item => item.Entry.WasTruncated))
                { HasMore = more, HistoryGap = gap, NextCursor = next };
        }
    }

    private sealed record LogItem(long Sequence, PackageDevelopmentLogEntry Entry);
    private sealed record LogCursor(string Filter, Guid RuntimeId, Guid AppId, long RuntimeCutoff, long AppCutoff,
        DateTimeOffset Timestamp, string Source, long Sequence);
    private static LogCursor? ReadCursor(string? text, string filter)
    {
        if (text is null) return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<LogCursor>(Convert.FromBase64String(text), new JsonSerializerOptions { MaxDepth = 4 });
            if (cursor is null || cursor.Filter != filter || cursor.RuntimeCutoff < 0 || cursor.AppCutoff < 0 || cursor.Sequence < 1
                || cursor.Source is not ("app" or "runtime")) throw new JsonException();
            return cursor;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        { throw new ArgumentException("The log cursor does not match this package/query.", nameof(text)); }
    }
    private static bool Matches(LogItem item, PackageDevelopmentLogQuery query)
        => (query.Source == "all" || query.Source == item.Entry.Source)
            && (query.Contains is null || item.Entry.Message.Contains(query.Contains, StringComparison.OrdinalIgnoreCase))
            && (query.Since is null || item.Entry.Timestamp >= query.Since)
            && (query.Until is null || item.Entry.Timestamp < query.Until)
            && Enum.TryParse<PackageLogLevel>(item.Entry.Level, out var level) && level >= query.MinimumLevel;
    private static bool Before(LogItem item, LogCursor cursor)
        => item.Entry.Timestamp < cursor.Timestamp || item.Entry.Timestamp == cursor.Timestamp
            && (string.CompareOrdinal(item.Entry.Source, cursor.Source) < 0 || item.Entry.Source == cursor.Source && item.Sequence < cursor.Sequence);

    private static string ClipLogText(string value, int maximumCharacters)
    {
        var length = Math.Min(value.Length, maximumCharacters);
        if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
        return value[..length];
    }
}
