using System.Collections.Concurrent;

namespace FhirAugury.Common.Indexing;

/// <summary>
/// In-memory singleton that tracks index rebuild state within a source service.
/// Thread-safe for concurrent access from ingestion pipelines and gRPC handlers.
/// </summary>
public class IndexTracker : IIndexTracker
{
    private readonly ConcurrentDictionary<string, IndexEntry> _indexes = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterIndex(string name, string description, Func<int> recordCountProvider)
    {
        _indexes[name] = new IndexEntry
        {
            Name = name,
            Description = description,
            RecordCountProvider = recordCountProvider,
        };
    }

    public void MarkStarted(string indexName)
    {
        if (_indexes.TryGetValue(indexName, out IndexEntry? entry))
        {
            lock (entry)
            {
                entry.IsRebuilding = true;
                entry.LastRebuildStartedAt = DateTimeOffset.UtcNow;
                entry.LastError = null;
            }
        }
    }

    public void MarkCompleted(string indexName, int? recordCount = null)
    {
        if (_indexes.TryGetValue(indexName, out IndexEntry? entry))
        {
            lock (entry)
            {
                entry.IsRebuilding = false;
                entry.LastRebuildCompletedAt = DateTimeOffset.UtcNow;
                entry.LastError = null;
                if (recordCount.HasValue)
                    entry.CachedRecordCount = recordCount.Value;
            }
        }
    }

    public void MarkFailed(string indexName, string error)
    {
        if (_indexes.TryGetValue(indexName, out IndexEntry? entry))
        {
            lock (entry)
            {
                entry.IsRebuilding = false;
                entry.LastRebuildCompletedAt = DateTimeOffset.UtcNow;
                entry.LastError = error;
            }
        }
    }

    public IReadOnlyList<IndexInfo> GetAllStatuses()
    {
        List<IndexInfo> result = [];
        foreach (IndexEntry entry in _indexes.Values)
        {
            result.Add(ToIndexInfo(entry));
        }
        return result;
    }

    public IndexInfo? GetStatus(string indexName)
    {
        return _indexes.TryGetValue(indexName, out IndexEntry? entry)
            ? ToIndexInfo(entry)
            : null;
    }

    private static IndexInfo ToIndexInfo(IndexEntry entry)
    {
        int recordCount;
        try
        {
            recordCount = entry.RecordCountProvider();
        }
        catch
        {
            recordCount = entry.CachedRecordCount;
        }

        lock (entry)
        {
            return new IndexInfo
            {
                Name = entry.Name,
                Description = entry.Description,
                IsRebuilding = entry.IsRebuilding,
                LastRebuildStartedAt = entry.LastRebuildStartedAt,
                LastRebuildCompletedAt = entry.LastRebuildCompletedAt,
                RecordCount = recordCount,
                LastError = entry.LastError,
            };
        }
    }

    private sealed class IndexEntry
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required Func<int> RecordCountProvider { get; init; }
        public bool IsRebuilding { get; set; }
        public DateTimeOffset? LastRebuildStartedAt { get; set; }
        public DateTimeOffset? LastRebuildCompletedAt { get; set; }
        public int CachedRecordCount { get; set; }
        public string? LastError { get; set; }
    }
}
