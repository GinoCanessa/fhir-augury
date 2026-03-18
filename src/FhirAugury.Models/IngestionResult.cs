namespace FhirAugury.Models;

/// <summary>Captures the outcome of an ingestion run.</summary>
public record IngestionResult
{
    /// <summary>Total items processed during the run.</summary>
    public required int ItemsProcessed { get; init; }

    /// <summary>Number of newly created items.</summary>
    public required int ItemsNew { get; init; }

    /// <summary>Number of items that were updated.</summary>
    public required int ItemsUpdated { get; init; }

    /// <summary>Number of items that failed to process.</summary>
    public required int ItemsFailed { get; init; }

    /// <summary>Errors encountered during ingestion.</summary>
    public required IReadOnlyList<IngestionError> Errors { get; init; }

    /// <summary>When the ingestion run started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the ingestion run completed.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Items that were created or updated, for cross-reference linking.</summary>
    public required IReadOnlyList<IngestedItem> NewAndUpdatedItems { get; init; }
}

/// <summary>Describes an error encountered during ingestion.</summary>
public record IngestionError(string ItemId, string Message, Exception? Exception = null);
