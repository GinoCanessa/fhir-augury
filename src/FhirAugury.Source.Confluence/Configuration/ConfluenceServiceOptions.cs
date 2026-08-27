using FhirAugury.Common.Configuration;

namespace FhirAugury.Source.Confluence.Configuration;

/// <summary>
/// Strongly-typed configuration for the Confluence source service.
/// </summary>
public class ConfluenceServiceOptions
{
    public const string SectionName = "Confluence";

    public string BaseUrl { get; set; } = "https://confluence.hl7.org";
    public string AuthMode { get; set; } = "cookie";
    public string? Cookie { get; set; }
    public string? Username { get; set; }
    public string? ApiToken { get; set; }
    /// <summary>Spaces to ingest. Uses the null-as-default, empty-as-explicit-all convention; null uses default spaces and [] ingests no spaces. See docs/source-filter-conventions.md.</summary>
    public List<string>? Spaces { get; set; }

    public bool HasExplicitEmptySpaces => Spaces is { Count: 0 };

    public List<string> GetEffectiveSpaces() => Spaces ?? ["FHIR", "FHIRI", "SOA"];
    public string CachePath { get; set; } = "./cache";
    public string DatabasePath { get; set; } = "./data/confluence.db";
    public string SyncSchedule { get; set; } = "1.00:00:00";

    /// <summary>
    /// Minimum age of the last sync before a new sync is triggered on startup.
    /// Prevents redundant downloads when services are restarted frequently.
    /// </summary>
    public string MinSyncAge { get; set; } = "04:00:00";

    /// <summary>HTTP address of the orchestrator service for ingestion notifications.</summary>
    public string? OrchestratorAddress { get; set; }

    /// <summary>
    /// When true, pauses all ingestion (scheduled and on-demand). The service remains
    /// available for queries but will not download new content.
    /// </summary>
    public bool IngestionPaused { get; set; } = false;

    /// <summary>
    /// When true, the scheduled ingestion worker runs exactly one pass at
    /// startup (honoring <see cref="MinSyncAge"/> and <see cref="IngestionPaused"/>)
    /// and then exits its loop cleanly. The service itself keeps running, so HTTP
    /// endpoints and manual ingestion remain available. Useful for local/dev
    /// runs where a continuous sync loop is not desired.
    /// </summary>
    public bool RunIngestionOnStartupOnly { get; set; } = false;

    /// <summary>
    /// When true, rebuilds the database from cached responses on startup.
    /// </summary>
    public bool ReloadFromCacheOnStartup { get; set; } = false;

    public int PageSize { get; set; } = 25;

    /// <summary>
    /// Page size used by the body-less sweep. Larger than <see cref="PageSize"/>
    /// because a sweep entry carries no body: Confluence honours 200 verbatim
    /// (see docs/technical/confluence-api-notes.md), so the whole instance
    /// enumerates in roughly 1,660 requests.
    /// </summary>
    public int SweepPageSize { get; set; } = 200;

    /// <summary>
    /// Attachment blobs larger than this are not downloaded; their metadata is
    /// still swept, cached, replayed and indexed. <c>0</c> means unlimited.
    /// A negative value is rejected at startup.
    /// </summary>
    /// <remarks>
    /// The cap gates <em>downloading</em>, not <em>keeping</em>: lowering it
    /// never removes bytes already on disk, and raising it makes previously
    /// skipped blobs converge on the next run.
    /// </remarks>
    public long AttachmentMaxBytes { get; set; } = 104_857_600;

    /// <summary>
    /// A space whose manifest is younger than this is skipped by the sweep and
    /// its previous manifest reused. The shipped default re-sweeps every space
    /// on every run.
    /// </summary>
    /// <remarks>
    /// This is an age threshold, not a per-run request budget. It exists so the
    /// full-sweep decision can be revisited by editing a default rather than by
    /// a redesign; Phase 1's measurement (~5.5 minutes for the whole instance at
    /// 5 req/s) gives no present reason to raise it.
    /// </remarks>
    public string SpaceSweepMaxAge { get; set; } = "00:00:00";

    /// <summary>Parsed <see cref="SpaceSweepMaxAge"/>, falling back to zero.</summary>
    public TimeSpan GetSpaceSweepMaxAge() =>
        TimeSpan.TryParse(SpaceSweepMaxAge, out TimeSpan parsed) && parsed > TimeSpan.Zero
            ? parsed
            : TimeSpan.Zero;

    public PortConfiguration Ports { get; set; } = new() { Http = 5180 };
    public RateLimitConfiguration RateLimiting { get; set; } = new();
    public AuxiliaryDatabaseOptions AuxiliaryDatabase { get; set; } = new();
    public DictionaryDatabaseOptions DictionaryDatabase { get; set; } = new();
    public Bm25Options Bm25 { get; set; } = new();
}
