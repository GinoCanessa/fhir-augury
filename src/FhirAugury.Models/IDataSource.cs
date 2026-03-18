namespace FhirAugury.Models;

/// <summary>Defines a data source that can download, sync, and ingest items.</summary>
public interface IDataSource
{
    /// <summary>The canonical name of this source (e.g., "jira", "zulip").</summary>
    string SourceName { get; }

    /// <summary>Downloads all items from the source.</summary>
    Task<IngestionResult> DownloadAllAsync(IngestionOptions options, CancellationToken ct);

    /// <summary>Downloads items updated since the given timestamp.</summary>
    Task<IngestionResult> DownloadIncrementalAsync(DateTimeOffset since, IngestionOptions options, CancellationToken ct);

    /// <summary>Ingests a single item by its source-specific identifier.</summary>
    Task<IngestionResult> IngestItemAsync(string identifier, IngestionOptions options, CancellationToken ct);
}
