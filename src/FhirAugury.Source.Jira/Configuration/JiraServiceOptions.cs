using FhirAugury.Common.Configuration;

namespace FhirAugury.Source.Jira.Configuration;

/// <summary>
/// Strongly-typed configuration for the Jira source service.
/// </summary>
public class JiraServiceOptions
{
    public const string SectionName = "Jira";

    public string BaseUrl { get; set; } = "https://jira.hl7.org";
    public string AuthMode { get; set; } = "cookie";
    public string? Cookie { get; set; }
    public string? ApiToken { get; set; }
    public string? Email { get; set; }
    public string CachePath { get; set; } = "./cache";
    public string DatabasePath { get; set; } = "./data/jira.db";
    public string SyncSchedule { get; set; } = "01:00:00";

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

    public string DefaultProject { get; set; } = "FHIR";
    public string? DefaultJql { get; set; }

    /// <summary>
    /// List of Jira projects to download. Each project gets its own cache
    /// subdirectory and sync state. When empty, falls back to
    /// <see cref="DefaultProject"/>.
    /// </summary>
    public List<JiraProjectConfig> Projects { get; set; } = [];

    /// <summary>
    /// Returns the effective list of enabled projects. If <see cref="Projects"/>
    /// is populated, returns only enabled entries. Otherwise creates a
    /// single-element list from <see cref="DefaultProject"/>.
    /// </summary>
    public List<JiraProjectConfig> GetEffectiveProjects()
    {
        if (Projects.Count > 0)
        {
            return Projects.Where(p => p.Enabled).ToList();
        }

        return [new JiraProjectConfig { Key = DefaultProject }];
    }
    public int PageSize { get; set; } = 100;
    public PortConfiguration Ports { get; set; } = new() { Http = 5160 };
    public RateLimitConfiguration RateLimiting { get; set; } = new();
    public AuxiliaryDatabaseOptions AuxiliaryDatabase { get; set; } = new();
    public DictionaryDatabaseOptions DictionaryDatabase { get; set; } = new();
    public Bm25Options Bm25 { get; set; } = new();

    /// <summary>
    /// Optional configuration for the HL7 work-group CodeSystem XML support
    /// file. When unset, the acquirer logs a single info message and the
    /// downstream work-group ingestion (FR-02) gracefully skips.
    /// </summary>
    public WorkGroupSourceXmlOptions Hl7WorkGroupSourceXml { get; set; } = new();

    /// <summary>
    /// Validates project-level configuration. Returns a list of human-readable
    /// error messages; an empty list means the configuration is valid.
    /// </summary>
    public IEnumerable<string> Validate()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        foreach (JiraProjectConfig p in Projects)
        {
            if (p.DownloadWindowDays < 1 || p.DownloadWindowDays > JiraProjectConfig.DownloadWindowDaysMax)
                yield return $"Project '{p.Key}': DownloadWindowDays must be between 1 and {JiraProjectConfig.DownloadWindowDaysMax} (got {p.DownloadWindowDays}).";
            if (p.StartDate is DateOnly sd && sd > today)
                yield return $"Project '{p.Key}': StartDate must not be in the future (got {sd:yyyy-MM-dd}).";
            if (p.BaselineValue < 0 || p.BaselineValue > 10)
                yield return $"Project '{p.Key}': BaselineValue must be between 0 and 10 (got {p.BaselineValue}).";
        }

        string? filename = Hl7WorkGroupSourceXml.Filename;
        if (string.IsNullOrWhiteSpace(filename))
        {
            yield return "Hl7WorkGroupSourceXml.Filename must be non-empty.";
        }
        else if (filename.IndexOf(Path.DirectorySeparatorChar) >= 0
                 || filename.IndexOf(Path.AltDirectorySeparatorChar) >= 0
                 || filename.IndexOf('\0') >= 0
                 || filename.Contains(".."))
        {
            yield return $"Hl7WorkGroupSourceXml.Filename '{filename}' must not contain path separators or '..'.";
        }
    }
}
