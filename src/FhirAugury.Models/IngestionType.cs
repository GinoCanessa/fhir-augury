namespace FhirAugury.Models;

/// <summary>The type of ingestion run.</summary>
public enum IngestionType
{
    /// <summary>Full download of all items.</summary>
    Full,

    /// <summary>Incremental update since last sync.</summary>
    Incremental,

    /// <summary>On-demand ingestion of a single item.</summary>
    OnDemand,
}
